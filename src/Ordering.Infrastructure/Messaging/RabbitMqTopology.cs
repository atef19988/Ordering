using RabbitMQ.Client;

namespace Ordering.Infrastructure.Messaging;

/// <summary>
/// Every exchange, queue and binding, declared idempotently by each service that uses them
/// (a declare with identical arguments is a no-op). Everything is durable; messages are persistent.
/// The retry tiers are one queue per attempt because RabbitMQ expires only from the head of a
/// queue — a single queue with per-message TTLs would let a long TTL delay every shorter one
/// behind it. The cost is that backoff has no jitter: the tiers are fixed per attempt.
/// </summary>
internal static class RabbitMqTopology
{
    /// <summary>Topic exchange the relay publishes to; routing key = <c>outbox_messages.type</c>.</summary>
    public const string EventsExchange = "ordering.events";

    /// <summary>Direct exchange the consumer re-publishes failed attempts to; routing key = <c>attempt.{n}</c>.</summary>
    public const string RetryExchange = "ordering.retry";

    /// <summary>Fanout for messages nobody can parse; a human looks at <see cref="DeadQueue"/>.</summary>
    public const string DeadExchange = "ordering.dead";

    /// <summary>
    /// Fanout for change hints (Task 13): non-durable, messages non-persistent, no confirms. Each
    /// API instance binds an exclusive auto-delete queue; a lost hint costs nothing but a later
    /// refresh, so none of this survives a broker restart on purpose.
    /// </summary>
    public const string HintsExchange = "ordering.hints";

    public const string OrderCreatedQueue = "notifications.order-created";

    public const string DeadQueue = "notifications.dead";

    public const string OrderCreatedRoutingKey = "order.created";

    public static string RetryQueue(int attempt) => $"notifications.retry.a{attempt}";

    public static string RetryRoutingKey(int attempt) => $"attempt.{attempt}";

    /// <summary>
    /// Delay after failed attempt <paramref name="attempt"/>: <c>min(base × 2^(attempt−1), cap)</c>,
    /// i.e. 200, 400, 800, 1600 ms with the defaults. Also the TTL of tier queue <c>a{attempt}</c>.
    /// </summary>
    public static TimeSpan RetryDelay(int attempt, TimeSpan baseBackoff, TimeSpan maxBackoff)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var delayMs = Math.Min(baseBackoff.TotalMilliseconds * (1L << exponent), maxBackoff.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(delayMs);
    }

    /// <summary>The hints fanout alone; declared by every publisher and every listener, transient on both sides.</summary>
    public static Task DeclareHintsAsync(IChannel channel, CancellationToken cancellationToken) =>
        channel.ExchangeDeclareAsync(HintsExchange, ExchangeType.Fanout, durable: false, autoDelete: false, cancellationToken: cancellationToken);

    /// <param name="maxAttempts">Tiers exist for attempts 1 … maxAttempts − 1; the last attempt has no retry.</param>
    public static async Task DeclareAsync(IChannel channel, int maxAttempts, TimeSpan baseBackoff, TimeSpan maxBackoff, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(EventsExchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(RetryExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(DeadExchange, ExchangeType.Fanout, durable: true, autoDelete: false, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            OrderCreatedQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = DeadExchange },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(OrderCreatedQueue, EventsExchange, OrderCreatedRoutingKey, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(DeadQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(DeadQueue, DeadExchange, routingKey: string.Empty, cancellationToken: cancellationToken);

        // Changing MaxAttempts or the backoff changes these arguments; RabbitMQ then refuses the
        // declare (406 PRECONDITION_FAILED) until the old tier queues are deleted. Development only.
        for (var attempt = 1; attempt < maxAttempts; attempt++)
        {
            var ttl = (int)RetryDelay(attempt, baseBackoff, maxBackoff).TotalMilliseconds;

            await channel.QueueDeclareAsync(
                RetryQueue(attempt),
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: new Dictionary<string, object?>
                {
                    ["x-message-ttl"] = ttl,
                    ["x-dead-letter-exchange"] = EventsExchange,
                    ["x-dead-letter-routing-key"] = OrderCreatedRoutingKey,
                },
                cancellationToken: cancellationToken);
            await channel.QueueBindAsync(RetryQueue(attempt), RetryExchange, RetryRoutingKey(attempt), cancellationToken: cancellationToken);
        }
    }
}
