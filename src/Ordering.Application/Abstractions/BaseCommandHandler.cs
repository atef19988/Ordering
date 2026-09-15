using Ordering.Application.Abstractions.Messaging;

namespace Ordering.Application.Abstractions;

/// <summary>
/// Template for write handlers: <see cref="HandleCore"/> expresses the business intent, this class
/// flushes pending changes on success. The surrounding transaction belongs to
/// <see cref="Behaviors.TransactionBehavior{TRequest,TResponse}"/>.
/// </summary>
public abstract class BaseCommandHandler<TCommand, TResult>(IUnitOfWork unitOfWork, IClock clock)
    : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    protected IUnitOfWork UnitOfWork { get; } = unitOfWork;

    protected IClock Clock { get; } = clock;

    public async Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var result = await HandleCore(command, cancellationToken);

        if (result.IsSuccess)
        {
            await UnitOfWork.SaveChangesAsync(cancellationToken);
        }

        return result;
    }

    protected abstract Task<Result<TResult>> HandleCore(TCommand command, CancellationToken cancellationToken);
}
