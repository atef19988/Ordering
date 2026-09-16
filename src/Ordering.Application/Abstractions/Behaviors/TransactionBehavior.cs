using Ordering.Application.Abstractions.Messaging;

namespace Ordering.Application.Abstractions.Behaviors;

/// <summary>
/// The one place a transaction is opened. Applies to commands only (the <see cref="IBaseCommand"/>
/// constraint keeps queries out); commits on a success <see cref="Result"/>, rolls back otherwise.
/// Every write runs under <see cref="LockTimeout"/>, so a wait on a busy row ends in a typed
/// failure the handler maps to 409/503 instead of an unbounded stall. <see cref="IFailurePoint"/>
/// is consulted between the handler and the commit so a test can crash a real transaction there.
/// </summary>
public sealed class TransactionBehavior<TRequest, TResponse>(IUnitOfWork unitOfWork, IFailurePoint failurePoint)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IBaseCommand
    where TResponse : Result, IResultFactory<TResponse>
{
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(3);

    public async Task<TResponse> Handle(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
    {
        await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            await unitOfWork.SetLockTimeoutAsync(LockTimeout, cancellationToken);

            var response = await next();

            if (response.IsSuccess)
            {
                await failurePoint.ReachedAsync(FailurePoints.BeforeCommit, cancellationToken);
                await unitOfWork.CommitAsync(cancellationToken);
            }
            else
            {
                await unitOfWork.RollbackAsync(CancellationToken.None);
            }

            return response;
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
