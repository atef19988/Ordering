using Ordering.Application.Abstractions.Outbox;
using Ordering.Infrastructure.Persistence;

namespace Ordering.Infrastructure.Outbox;

/// <summary>
/// The outbox's write side: the message is just another tracked row, so it commits with the
/// aggregate or not at all. Task 7 drains the table: <c>OutboxRelay</c> publishes to RabbitMQ,
/// <c>NotificationConsumer</c> writes the verdict back.
/// </summary>
internal sealed class TransactionalOutbox(OrderingDbContext context) : IOutbox
{
    public void Enqueue(OutboxMessage message) => context.OutboxMessages.Add(message);
}
