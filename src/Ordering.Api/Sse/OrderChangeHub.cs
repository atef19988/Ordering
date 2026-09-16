using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Ordering.Api.Sse;

/// <summary>
/// One bounded <see cref="Channel{T}"/> of capacity 1 per subscription, dropping the oldest on
/// overflow: several hints that arrive before the stream re-reads collapse into one re-read,
/// which is exactly right because the re-read returns the latest state anyway. Subscriptions
/// for one order id are an immutable array swapped atomically, so publish is a lock-free read.
/// </summary>
public sealed class OrderChangeHub : IOrderChangeHub
{
    private readonly ConcurrentDictionary<long, Subscription[]> _subscriptions = new();

    public IOrderChangeSubscription Subscribe(long orderId)
    {
        var subscription = new Subscription(this, orderId);
        _subscriptions.AddOrUpdate(orderId, _ => [subscription], (_, existing) => [.. existing, subscription]);
        return subscription;
    }

    public void Publish(long orderId)
    {
        if (_subscriptions.TryGetValue(orderId, out var subscriptions))
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Writer.TryWrite(orderId);
            }
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        var remaining = _subscriptions.AddOrUpdate(
            subscription.OrderId,
            _ => [],
            (_, existing) => existing.Where(s => !ReferenceEquals(s, subscription)).ToArray());

        if (remaining.Length == 0)
        {
            // Conditional on the value still being the empty array we just installed.
            _subscriptions.TryRemove(new KeyValuePair<long, Subscription[]>(subscription.OrderId, remaining));
        }
    }

    private sealed class Subscription : IOrderChangeSubscription
    {
        private readonly OrderChangeHub _hub;
        private readonly Channel<long> _channel = Channel.CreateBounded<long>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        public Subscription(OrderChangeHub hub, long orderId)
        {
            _hub = hub;
            OrderId = orderId;
        }

        public long OrderId { get; }

        public ChannelWriter<long> Writer => _channel.Writer;

        public ChannelReader<long> Hints => _channel.Reader;

        public void Dispose()
        {
            _hub.Unsubscribe(this);
            _channel.Writer.TryComplete();
        }
    }
}
