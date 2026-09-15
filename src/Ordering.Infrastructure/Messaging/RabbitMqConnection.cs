using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Ordering.Infrastructure.Messaging;

/// <summary>
/// The one <see cref="IConnection"/> per process, opened on first use (the host must start even
/// when the broker is not up yet) with automatic recovery on. Channels are handed out one per
/// hosted service; nothing outside <c>Infrastructure/Messaging</c> ever sees a RabbitMQ type.
/// </summary>
public sealed partial class RabbitMqConnection(RabbitMqOptions options, string clientName, ILogger<RabbitMqConnection> logger) : IAsyncDisposable
{
    // Serialises connection creation only; it protects no business state (CLAUDE.md rule 2).
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    internal async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_connection is { IsOpen: true } opened)
            {
                return opened;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }

            var factory = new ConnectionFactory
            {
                HostName = options.Host,
                Port = options.Port,
                UserName = options.User,
                Password = options.Password,
                VirtualHost = options.VirtualHost,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                ClientProvidedName = clientName,
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            LogConnected(logger, options.Host, options.Port, clientName);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A channel with publisher confirms on: <c>BasicPublishAsync</c> then completes only once the
    /// broker has persisted the message. <paramref name="consumerConcurrency"/> is how many
    /// deliveries the channel's consumer may handle at once (1 for a publish-only channel).
    /// </summary>
    internal async Task<IChannel> CreateChannelAsync(ushort consumerConcurrency, CancellationToken cancellationToken)
    {
        var connection = await GetAsync(cancellationToken);
        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true,
            consumerDispatchConcurrency: consumerConcurrency);

        return await connection.CreateChannelAsync(channelOptions, cancellationToken);
    }

    /// <summary>For <c>/health</c>: connects if needed and reports whether the connection is open.</summary>
    public async Task<bool> IsOpenAsync(CancellationToken cancellationToken)
    {
        var connection = await GetAsync(cancellationToken);
        return connection.IsOpen;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to RabbitMQ at {Host}:{Port} as {ClientName}")]
    private static partial void LogConnected(ILogger logger, string host, int port, string clientName);
}
