using System.Globalization;
using System.Text;
using Ordering.Application.Abstractions.Messaging;
using Ordering.Infrastructure.Notifications;
using RabbitMQ.Client;

namespace Ordering.Infrastructure.Messaging;

/// <summary>
/// <see cref="IEventPublisher"/> over one confirmed channel (the relay's). Publishes to
/// <see cref="RabbitMqTopology.EventsExchange"/> with routing key = event type,
/// <c>MessageId</c> = the outbox id, persistent, body = the stored payload verbatim. The await
/// returns once the broker has persisted the message; anything else throws and the relay
/// releases the row.
/// </summary>
internal sealed class RabbitMqEventPublisher(RabbitMqConnection connection, ConsumerOptions consumerOptions) : IEventPublisher, IAsyncDisposable
{
    public const string AggregateIdHeader = "x-aggregate-id";
    public const string JsonContentType = "application/json";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChannel? _channel;

    public async Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken)
    {
        var channel = await GetChannelAsync(cancellationToken);
        var properties = Properties(@event.Id, @event.Type, @event.AggregateId);

        await channel.BasicPublishAsync(
            RabbitMqTopology.EventsExchange,
            routingKey: @event.Type,
            mandatory: false,
            properties,
            Encoding.UTF8.GetBytes(@event.Payload),
            cancellationToken);
    }

    /// <summary>The one shape every outbox message has on the wire; the consumer re-publishes retries with the same properties.</summary>
    internal static BasicProperties Properties(long eventId, string type, long aggregateId) => new()
    {
        MessageId = eventId.ToString(CultureInfo.InvariantCulture),
        Type = type,
        Persistent = true,
        ContentType = JsonContentType,
        Headers = new Dictionary<string, object?> { [AggregateIdHeader] = aggregateId.ToString(CultureInfo.InvariantCulture) },
    };

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true } open)
        {
            return open;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_channel is { IsOpen: true } opened)
            {
                return opened;
            }

            if (_channel is not null)
            {
                await _channel.DisposeAsync();
                _channel = null;
            }

            var channel = await connection.CreateChannelAsync(consumerConcurrency: 1, cancellationToken);
            await RabbitMqTopology.DeclareAsync(channel, consumerOptions.MaxAttempts, consumerOptions.BaseBackoff, consumerOptions.MaxBackoff, cancellationToken);
            _channel = channel;
            return channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        _gate.Dispose();
    }
}
