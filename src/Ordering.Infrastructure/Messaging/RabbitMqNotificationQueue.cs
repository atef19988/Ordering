using System.Text;
using Microsoft.Extensions.Logging;
using Ordering.Infrastructure.Notifications;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Ordering.Infrastructure.Messaging;

/// <summary>One delivery from <see cref="RabbitMqTopology.OrderCreatedQueue"/>, stripped of every RabbitMQ type.</summary>
internal sealed record InboundMessage(string? MessageId, string? Type, string? AggregateId, ReadOnlyMemory<byte> Body);

/// <summary>What the consumer tells the broker after handling a message.</summary>
internal enum Acknowledgement
{
    /// <summary>Done with it, whatever the verdict was — the outbox row carries the state.</summary>
    Ack,

    /// <summary>Cannot be parsed; NACK without requeue so it dead-letters to <see cref="RabbitMqTopology.DeadQueue"/>.</summary>
    Dead,
}

/// <summary>
/// The consumer's side of the broker: manual acks, prefetch, and the re-publish to a retry tier.
/// <see cref="NotificationConsumer"/> works with <see cref="InboundMessage"/> only, so no channel,
/// exchange or queue name leaves <c>Infrastructure/Messaging</c>.
/// </summary>
internal interface INotificationQueue
{
    /// <summary>Consumes until <paramref name="cancellationToken"/> is cancelled; <paramref name="handle"/> runs once per delivery, up to <paramref name="prefetch"/> at a time.</summary>
    Task ConsumeAsync(ushort prefetch, Func<InboundMessage, CancellationToken, Task<Acknowledgement>> handle, CancellationToken cancellationToken);

    /// <summary>Publishes the same body and message id to tier <paramref name="attempt"/>; confirmed by the broker before it returns.</summary>
    Task RepublishForRetryAsync(InboundMessage message, int attempt, CancellationToken cancellationToken);
}

internal sealed partial class RabbitMqNotificationQueue(RabbitMqConnection connection, ConsumerOptions options, ILogger<RabbitMqNotificationQueue> logger) : INotificationQueue
{
    /// <summary>How long a delivery whose handler threw (database unreachable) waits before it is requeued.</summary>
    private static readonly TimeSpan RequeueDelay = TimeSpan.FromSeconds(2);

    private IChannel? _channel;

    public async Task ConsumeAsync(ushort prefetch, Func<InboundMessage, CancellationToken, Task<Acknowledgement>> handle, CancellationToken cancellationToken)
    {
        await using var channel = await connection.CreateChannelAsync(prefetch, cancellationToken);
        await RabbitMqTopology.DeclareAsync(channel, options.MaxAttempts, options.BaseBackoff, options.MaxBackoff, cancellationToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: prefetch, global: false, cancellationToken);

        _channel = channel;

        try
        {
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, delivery, handle, cancellationToken);

            await channel.BasicConsumeAsync(RabbitMqTopology.OrderCreatedQueue, autoAck: false, consumer, cancellationToken);

            // Deliveries arrive on the channel's dispatcher; this task only keeps the channel alive.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            _channel = null;
        }
    }

    public Task RepublishForRetryAsync(InboundMessage message, int attempt, CancellationToken cancellationToken)
    {
        var channel = _channel ?? throw new InvalidOperationException("Retries can only be published while consuming.");

        var properties = new BasicProperties
        {
            MessageId = message.MessageId,
            Type = message.Type,
            Persistent = true,
            ContentType = RabbitMqEventPublisher.JsonContentType,
            Headers = new Dictionary<string, object?> { [RabbitMqEventPublisher.AggregateIdHeader] = message.AggregateId },
        };

        return channel.BasicPublishAsync(
            RabbitMqTopology.RetryExchange,
            RabbitMqTopology.RetryRoutingKey(attempt),
            mandatory: false,
            properties,
            message.Body,
            cancellationToken).AsTask();
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, Func<InboundMessage, CancellationToken, Task<Acknowledgement>> handle, CancellationToken cancellationToken)
    {
        // The body buffer is only valid during the callback; copy it so a retry can re-publish it.
        var message = new InboundMessage(
            delivery.BasicProperties.MessageId,
            delivery.BasicProperties.Type,
            HeaderString(delivery.BasicProperties, RabbitMqEventPublisher.AggregateIdHeader),
            delivery.Body.ToArray());

        try
        {
            var verdict = await handle(message, cancellationToken);

            if (verdict == Acknowledgement.Ack)
            {
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken);
            }
            else
            {
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Left unacked on purpose: the broker redelivers it once the channel closes.
        }
        catch (Exception exception)
        {
            // Bookkeeping failed (typically the database); the outbox row is unchanged or already
            // counted, so a redelivery is safe. The delay keeps a dead database from a hot loop.
            LogHandlerFailed(logger, exception, message.MessageId);
            await Task.Delay(RequeueDelay, cancellationToken);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken);
        }
    }

    private static string? HeaderString(IReadOnlyBasicProperties properties, string name) =>
        properties.Headers is { } headers && headers.TryGetValue(name, out var value)
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => value?.ToString(),
            }
            : null;

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling message {MessageId} threw; requeueing")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string? messageId);
}
