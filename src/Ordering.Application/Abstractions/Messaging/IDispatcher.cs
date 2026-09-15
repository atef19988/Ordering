namespace Ordering.Application.Abstractions.Messaging;

public interface IDispatcher
{
    Task<Result> Send(ICommand command, CancellationToken cancellationToken = default);

    Task<Result<TResult>> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);

    Task<Result<TResult>> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);
}
