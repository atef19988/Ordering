using Ordering.Application.Abstractions.Messaging;

namespace Ordering.Application.Abstractions.Behaviors;

/// <summary>
/// The one place a transaction is opened. Applies to commands only (the <see cref="IBaseCommand"/>
/// constraint keeps queries out); commits on a success <see cref="Result"/>, rolls back otherwise.
/// </summary>
public sealed class TransactionBehavior<TRequest, TResponse>(IUnitOfWork unitOfWork)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IBaseCommand
    where TResponse : Result, IResultFactory<TResponse>
{
    public async Task<TResponse> Handle(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
    {
        await unitOfWork.BeginTransactionAsync(cancellationToken);

        try
        {
            var response = await next();

            if (response.IsSuccess)
            {
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
