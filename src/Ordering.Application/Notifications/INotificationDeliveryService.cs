namespace Ordering.Application.Notifications;

/// <summary>Outcome of one delivery attempt; expected failures are values, not exceptions.</summary>
public sealed record DeliveryResult(bool Succeeded, string? Error)
{
    public static DeliveryResult Success() => new(true, null);

    public static DeliveryResult Failure(string error) => new(false, error);
}

/// <summary>
/// Where an <c>order.created</c> notification finally goes. The consumer (Task 7) calls it with
/// no database connection open and passes the stable outbox event id, so a real implementation
/// can deduplicate: delivery is at-least-once and the same id may arrive more than once.
/// </summary>
public interface INotificationDeliveryService
{
    /// <param name="eventId">The outbox row id; identical across retries, tiers and re-publishes.</param>
    /// <param name="attempt">1-based delivery attempt, read from the outbox row by the consumer.</param>
    Task<DeliveryResult> SendAsync(long eventId, int attempt, string customerReference, decimal total, CancellationToken cancellationToken);
}
