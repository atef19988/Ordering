using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Ordering.Application.Abstractions;
using Ordering.Application.Abstractions.Messaging;
using Ordering.Infrastructure.Common;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Ordering.Infrastructure.Messaging;

/// <summary>
/// The receiving side of the change hints, stripped of every RabbitMQ type: the API's
/// <c>ChangeHintListener</c> calls <see cref="ConsumeAsync"/> and feeds its in-process hub.
/// </summary>
public interface IChangeHintSubscription
{
    /// <summary>
    /// Declares this instance's exclusive, auto-delete queue on the hints fanout and calls
    /// <paramref name="onOrderChanged"/> for every hint until <paramref name="cancellationToken"/>
    /// is cancelled. Throws when the broker is unreachable; the caller decides when to retry.
    /// </summary>
    Task ConsumeAsync(Action<long> onOrderChanged, CancellationToken cancellationToken);
}

/// <summary>
/// Both ends of <see cref="RabbitMqTopology.HintsExchange"/>. Publishing is fire-and-forget on
/// a channel without confirms: the body is the order id, non-persistent, and the whole call is
/// bounded by <see cref="PublishTimeout"/> so a dead broker delays a cancel response by at most
/// that, then is logged (once a minute) and forgotten — the truth is in SQL Server, and the
/// stream re-sends full state on reconnect. Consuming is auto-ack on a server-named exclusive
/// queue: nothing is ever redelivered, and nothing needs to be.
/// </summary>
internal sealed partial class RabbitMqChangeHints(RabbitMqConnection connection, IClock clock, ILogger<RabbitMqChangeHints> logger)
    : IChangeHintPublisher, IChangeHintSubscription, IAsyncDisposable
{
    /// <summary>Longest a caller waits on the broker for a hint, connection attempt included.</summary>
    public static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LogThrottle _failureLog = new(LogInterval);
    private IChannel? _publishChannel;

    public async Task OrderChangedAsync(long orderId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PublishTimeout);

        try
        {
            var channel = await GetPublishChannelAsync(timeout.Token);
            var properties = new BasicProperties { Persistent = false, ContentType = "text/plain" };

            await channel.BasicPublishAsync(
                RabbitMqTopology.HintsExchange,
                routingKey: string.Empty,
                mandatory: false,
                properties,
                Encoding.ASCII.GetBytes(orderId.ToString(CultureInfo.InvariantCulture)),
                timeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Best-effort by contract: the client learns about the change at its next reconnect or read.
            if (_failureLog.ShouldLog(clock.UtcNow))
            {
                LogPublishFailed(logger, exception, orderId, PublishTimeout.TotalSeconds);
            }
        }
    }

    public async Task ConsumeAsync(Action<long> onOrderChanged, CancellationToken cancellationToken)
    {
        await using var channel = await connection.CreateChannelAsync(consumerConcurrency: 1, cancellationToken, confirms: false);
        await RabbitMqTopology.DeclareHintsAsync(channel, cancellationToken);

        // Server-named, exclusive to this connection, gone when it closes: one per API instance.
        var queue = await channel.QueueDeclareAsync(queue: string.Empty, durable: false, exclusive: true, autoDelete: true, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(queue.QueueName, RabbitMqTopology.HintsExchange, routingKey: string.Empty, cancellationToken: cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) =>
        {
            if (long.TryParse(Encoding.ASCII.GetString(delivery.Body.Span), NumberStyles.None, CultureInfo.InvariantCulture, out var orderId))
            {
                onOrderChanged(orderId);
            }

            return Task.CompletedTask;
        };

        await channel.BasicConsumeAsync(queue.QueueName, autoAck: true, consumer, cancellationToken);
        LogListening(logger, queue.QueueName);

        // Deliveries arrive on the channel's dispatcher; this task only keeps the channel alive.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private async Task<IChannel> GetPublishChannelAsync(CancellationToken cancellationToken)
    {
        if (_publishChannel is { IsOpen: true } open)
        {
            return open;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_publishChannel is { IsOpen: true } opened)
            {
                return opened;
            }

            if (_publishChannel is not null)
            {
                await _publishChannel.DisposeAsync();
                _publishChannel = null;
            }

            var channel = await connection.CreateChannelAsync(consumerConcurrency: 1, cancellationToken, confirms: false);
            await RabbitMqTopology.DeclareHintsAsync(channel, cancellationToken);
            _publishChannel = channel;
            return channel;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_publishChannel is not null)
        {
            await _publishChannel.DisposeAsync();
        }

        _gate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Change hint for order {OrderId} was not published within {TimeoutSeconds} s; clients will catch up on reconnect (logged at most once a minute)")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, long orderId, double timeoutSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening for change hints on {Queue}")]
    private static partial void LogListening(ILogger logger, string queue);
}
