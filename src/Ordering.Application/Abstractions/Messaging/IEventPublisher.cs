namespace Ordering.Application.Abstractions.Messaging;

/// <summary>
/// One claimed <c>outbox_messages</c> row on its way to the broker. <see cref="Id"/> is the
/// Snowflake event id and becomes the message id on the wire; <see cref="Payload"/> travels
/// verbatim, the relay never re-serialises it.
/// </summary>
public sealed record IntegrationEvent(long Id, string Type, long AggregateId, string Payload);

/// <summary>
/// The only broker seam <c>Application</c> sees. Implemented over RabbitMQ in
/// <c>Infrastructure/Messaging/RabbitMqEventPublisher</c>; used by the outbox relay only —
/// handlers write outbox rows, they never publish (<c>CLAUDE.md</c> rule 10).
/// </summary>
public interface IEventPublisher
{
    /// <summary>Completes once the broker has persisted the message (publisher confirm); throws otherwise.</summary>
    Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken);
}
