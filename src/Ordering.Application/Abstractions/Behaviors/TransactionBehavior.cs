using Microsoft.Extensions.Logging;
using Ordering.Application.Abstractions.Messaging;

namespace Ordering.Application.Abstractions.Behaviors;

/// <summary>
/// The one place a transaction is opened. Applies to commands only (the <see cref="IBaseCommand"/>
/// constraint keeps queries out); commits on a success <see cref="Result"/>, rolls back otherwise.
/// Every write runs under <see cref="LockTimeout"/>, so a wait on a busy row ends in a typed
/// failure the handler maps to 409/503 instead of an unbounded stall. <see cref="IFailurePoint"/>
/// is consulted between the handler and the commit so a test can crash a real transaction there.
/// After the commit, and only then, the actions the handler registered with
/// <see cref="IUnitOfWork.OnCommitted"/> run in order, each on its own try/catch: the commit is
/// already the truth, so a failed eviction or hint is a warning, never a failed request.
/// </summary>
public sealed partial class TransactionBehavior<TRequest, TResponse>(
    IUnitOfWork unitOfWork,
    IFailurePoint failurePoint,
    ILogger<TransactionBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IBaseCommand
    where TResponse : Result, IResultFactory<TResponse>
{
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(3);

    private static readonly string RequestName = typeof(TRequest).Name;

    public async Task<TResponse> Handle(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
    {
        await unitOfWork.BeginTransactionAsync(cancellationToken);
        TResponse response;

        try
        {
            await unitOfWork.SetLockTimeoutAsync(LockTimeout, cancellationToken);

            response = await next();

            if (response.IsSuccess)
            {
                await failurePoint.ReachedAsync(FailurePoints.BeforeCommit, cancellationToken);
                await unitOfWork.CommitAsync(cancellationToken);
            }
            else
            {
                await unitOfWork.RollbackAsync(CancellationToken.None);
                return response;
            }
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }

        await RunCommittedActionsAsync();
        return response;
    }

    /// <summary>
    /// Runs with no cancellation token on purpose: the caller may have gone away, but the commit
    /// happened, so the eviction or hint that follows it still must. Each action bounds its own
    /// time (Redis and broker timeouts), so this cannot stall a response indefinitely.
    /// </summary>
    private async Task RunCommittedActionsAsync()
    {
        foreach (var action in unitOfWork.TakeCommittedActions())
        {
            try
            {
                await action(CancellationToken.None);
            }
            catch (Exception exception)
            {
                LogCommittedActionFailed(logger, exception, RequestName);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A post-commit action of {RequestName} failed; the transaction is committed and the request succeeds")]
    private static partial void LogCommittedActionFailed(ILogger logger, Exception exception, string requestName);
}
