using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ordering.Application.Abstractions;
using Ordering.Application.Abstractions.Outbox;
using Ordering.Application.Features.Orders;
using Ordering.Application.Notifications;
using Ordering.Infrastructure.Messaging;
using Ordering.Infrastructure.Outbox;

namespace Ordering.Infrastructure.Notifications;

/// <summary>
/// Delivers <c>order.created</c> notifications from the broker and writes the verdict back to
/// the outbox row. Safe to run N times: every write is guarded by <c>status = 'Processing'</c>,
/// and the attempt is counted in the same statement that checks the row is still live, so a
/// duplicate delivery of a terminal row is acknowledged without a second send. Delivery itself
/// runs with no database connection open. The two <see cref="IFailurePoint"/> calls let a test
/// kill the process at the only moments a redelivery is possible after a successful send.
/// </summary>
internal sealed partial class NotificationConsumer(
    INotificationQueue queue,
    OutboxStore store,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ConsumerOptions options,
    IFailurePoint failurePoint,
    ILogger<NotificationConsumer> logger) : BackgroundService
{
    /// <summary>Back-off between consume sessions when the broker is unreachable.</summary>
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, options.Prefetch, options.MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await queue.ConsumeAsync(options.Prefetch, HandleAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogConsumeFailed(logger, exception, ReconnectDelay.TotalSeconds);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }
    }

    private async Task<Acknowledgement> HandleAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        if (!long.TryParse(message.MessageId, NumberStyles.None, CultureInfo.InvariantCulture, out var eventId)
            || !TryDeserialize(message.Body, out var payload))
        {
            LogUnparseable(logger, message.MessageId);
            return Acknowledgement.Dead;
        }

        // Count and check liveness in one statement. Null = already Sent/Failed = a duplicate.
        var attempt = await store.CountAttemptAsync(eventId, cancellationToken);

        if (attempt is null)
        {
            LogDuplicate(logger, eventId);
            return Acknowledgement.Ack;
        }

        DeliveryResult result;

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var delivery = scope.ServiceProvider.GetRequiredService<INotificationDeliveryService>();
            result = await delivery.SendAsync(eventId, attempt.Value, payload.CustomerReference, payload.Total, cancellationToken);
        }

        if (result.Succeeded)
        {
            // Dying here is the one duplicate the row cannot absorb: the broker redelivers, the
            // row is still Processing, the attempt is counted and sent again. Only the delivery
            // service, keyed on eventId, can tell the two apart (README, at-least-once).
            await failurePoint.ReachedAsync(FailurePoints.NotificationDeliveredBeforeVerdict, cancellationToken);
            await store.MarkSentAsync(eventId, clock.UtcNow, cancellationToken);
            // Dying here is harmless: the redelivery finds the row Sent and is acknowledged unsent.
            await failurePoint.ReachedAsync(FailurePoints.NotificationSentBeforeAck, cancellationToken);
            // Task 13: publish a change hint for payload.OrderId here.
            return Acknowledgement.Ack;
        }

        var error = result.Error ?? "Delivery failed.";

        if (attempt.Value >= options.MaxAttempts)
        {
            await store.MarkFailedAsync(eventId, clock.UtcNow, error, cancellationToken);
            LogExhausted(logger, eventId, attempt.Value);
            // Task 13: publish a change hint for payload.OrderId here.
            return Acknowledgement.Ack;
        }

        var now = clock.UtcNow;
        var delay = RabbitMqTopology.RetryDelay(attempt.Value, options.BaseBackoff, options.MaxBackoff);
        await store.ScheduleRetryAsync(eventId, now + delay, now, error, cancellationToken);

        // Publish the retry before acknowledging the original: dying in between costs one extra,
        // still bounded, attempt — never a lost message.
        await queue.RepublishForRetryAsync(message, attempt.Value, cancellationToken);
        LogRetryScheduled(logger, eventId, attempt.Value, delay.TotalMilliseconds);
        return Acknowledgement.Ack;
    }

    private static bool TryDeserialize(ReadOnlyMemory<byte> body, out OrderCreated payload)
    {
        try
        {
            payload = JsonSerializer.Deserialize<OrderCreated>(body.Span, OutboxMessage.PayloadSerializerOptions)!;
            return payload is not null;
        }
        catch (JsonException)
        {
            payload = null!;
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification consumer started (prefetch {Prefetch}, max attempts {MaxAttempts})")]
    private static partial void LogStarted(ILogger logger, ushort prefetch, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Consuming failed; reconnecting in {DelaySeconds} s")]
    private static partial void LogConsumeFailed(ILogger logger, Exception exception, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Message {MessageId} is unparseable; sending it to the dead queue")]
    private static partial void LogUnparseable(ILogger logger, string? messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Duplicate delivery of {OutboxEventId}: row already terminal, acknowledged without sending")]
    private static partial void LogDuplicate(ILogger logger, long outboxEventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {OutboxEventId} attempt {Attempt} failed; retrying in {DelayMs} ms")]
    private static partial void LogRetryScheduled(ILogger logger, long outboxEventId, int attempt, double delayMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification {OutboxEventId} failed after {Attempt} attempts; marked Failed")]
    private static partial void LogExhausted(ILogger logger, long outboxEventId, int attempt);
}
