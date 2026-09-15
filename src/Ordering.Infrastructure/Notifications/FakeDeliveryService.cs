using Microsoft.Extensions.Logging;
using Ordering.Application.Notifications;

namespace Ordering.Infrastructure.Notifications;

/// <summary>
/// Stands in for the notification provider. It keeps no state: the failure decision is a pure
/// function of the configured mode and the attempt number the consumer read from the outbox row,
/// so it behaves the same across consumer instances and process restarts.
/// </summary>
internal sealed partial class FakeDeliveryService(DeliveryOptions options, ILogger<FakeDeliveryService> logger) : INotificationDeliveryService
{
    public async Task<DeliveryResult> SendAsync(long eventId, int attempt, string customerReference, decimal total, CancellationToken cancellationToken)
    {
        if (options.LatencyMs > 0)
        {
            await Task.Delay(options.LatencyMs, cancellationToken);
        }

        var fails = options.FailureMode switch
        {
            DeliveryFailureMode.AlwaysFail => true,
            DeliveryFailureMode.FailFirstN => attempt <= options.FailFirstN,
            DeliveryFailureMode.Random => Random.Shared.NextDouble() < options.FailureRate,
            _ => false,
        };

        if (fails)
        {
            LogFailed(logger, eventId, attempt, options.FailureMode);
            return DeliveryResult.Failure($"Simulated failure ({options.FailureMode}) on attempt {attempt}.");
        }

        LogDelivered(logger, eventId, attempt, customerReference, total);
        return DeliveryResult.Success();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Delivered order-created notification {OutboxEventId} (attempt {Attempt}) to {CustomerReference}, total {Total}")]
    private static partial void LogDelivered(ILogger logger, long outboxEventId, int attempt, string customerReference, decimal total);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Simulated delivery failure for {OutboxEventId} (attempt {Attempt}, mode {FailureMode})")]
    private static partial void LogFailed(ILogger logger, long outboxEventId, int attempt, DeliveryFailureMode failureMode);
}
