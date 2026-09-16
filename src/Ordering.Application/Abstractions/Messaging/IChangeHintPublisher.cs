namespace Ordering.Application.Abstractions.Messaging;

/// <summary>
/// Tells every API instance that <c>GET /api/orders/{id}</c> would answer differently now, so
/// an open event stream re-reads and pushes the order (Task 13). Implemented over a non-durable
/// RabbitMQ fanout in <c>Infrastructure/Messaging</c>; fire-and-forget, never throws, never
/// waits for the broker. A hint carries no state and may be lost or duplicated: the stream sends
/// the full order on every hint and on every (re)connect, and the truth stays
/// <c>GET /api/orders/{id}</c>. Called after a commit (<see cref="Abstractions.IUnitOfWork.OnCommitted"/>)
/// or after the consumer wrote a verdict — never inside a transaction.
/// </summary>
public interface IChangeHintPublisher
{
    Task OrderChangedAsync(long orderId, CancellationToken cancellationToken);
}
