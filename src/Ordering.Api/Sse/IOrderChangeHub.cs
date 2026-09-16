using System.Threading.Channels;

namespace Ordering.Api.Sse;

/// <summary>
/// In-process fan-out of change hints to the event streams open on this instance. A hint is
/// "re-read order X"; it carries no state, so losing or coalescing hints is harmless — every
/// stream sends the full order on every hint it does see.
/// </summary>
public interface IOrderChangeHub
{
    /// <summary>Dispose to unsubscribe. Hints for other orders never reach this reader.</summary>
    IOrderChangeSubscription Subscribe(long orderId);

    /// <summary>Wakes every stream subscribed to <paramref name="orderId"/>; never blocks.</summary>
    void Publish(long orderId);
}

public interface IOrderChangeSubscription : IDisposable
{
    /// <summary>Yields the order id once per (coalesced) hint.</summary>
    ChannelReader<long> Hints { get; }
}
