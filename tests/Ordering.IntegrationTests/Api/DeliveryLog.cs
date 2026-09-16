using System.Collections.Concurrent;
using Ordering.Application.Notifications;
using Ordering.Infrastructure.Notifications;

namespace Ordering.IntegrationTests.Api;

/// <summary>What one host's delivery service was asked to do: the side effects the consumer caused.</summary>
public sealed record DeliveryCall(long EventId, int Attempt, bool Succeeded);

public sealed class DeliveryLog
{
    private readonly ConcurrentQueue<DeliveryCall> _calls = new();

    public IReadOnlyList<DeliveryCall> Calls => _calls.ToArray();

    /// <summary>Successful sends of one notification: the number a customer would have received.</summary>
    public int SendsOf(long eventId) => Calls.Count(call => call.EventId == eventId && call.Succeeded);

    public void Record(DeliveryCall call) => _calls.Enqueue(call);
}

/// <summary>
/// Wraps the real <see cref="FakeDeliveryService"/> (its failure modes still decide the verdict)
/// and records every call, so a test can count downstream side effects per event id instead of
/// inferring them from the outbox row.
/// </summary>
internal sealed class RecordingDeliveryService(FakeDeliveryService inner, DeliveryLog log) : INotificationDeliveryService
{
    public async Task<DeliveryResult> SendAsync(long eventId, int attempt, string customerReference, decimal total, CancellationToken cancellationToken)
    {
        var result = await inner.SendAsync(eventId, attempt, customerReference, total, cancellationToken);
        log.Record(new DeliveryCall(eventId, attempt, result.Succeeded));
        return result;
    }
}
