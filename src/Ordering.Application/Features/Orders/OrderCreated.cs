namespace Ordering.Application.Features.Orders;

/// <summary>
/// Payload of the <c>order.created</c> outbox message. Serialized with
/// <c>OutboxMessage.PayloadSerializerOptions</c>, so <see cref="OrderId"/> is a JSON string.
/// The Task 7 worker deserializes this same record to call the delivery service.
/// </summary>
public sealed record OrderCreated(long OrderId, string CustomerReference, decimal Total, DateTimeOffset OccurredAt)
{
    public const string EventType = "order.created";
}
