using Ordering.Infrastructure.Messaging;
using Ordering.Infrastructure.Notifications;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace Ordering.IntegrationTests.Backend;

/// <summary>
/// One RabbitMQ 4 container per test collection. Exposes what a host needs to connect and the
/// two things a test needs from the broker: an empty topology between tests and a queue depth.
/// </summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    // Not guest/guest: the guest user may only connect from loopback and Docker's proxy is not loopback.
    private const string Credential = "ordering";

    private readonly RabbitMqContainer _container = new RabbitMqBuilder("rabbitmq:4-management")
        .WithUsername(Credential)
        .WithPassword(Credential)
        .Build();

    private IConnection? _connection;

    public RabbitMqOptions Options { get; private set; } = new();

    /// <summary>Every queue in <see cref="RabbitMqTopology"/>: main, the retry tiers of the default consumer options, dead.</summary>
    public static IReadOnlyList<string> Queues { get; } =
    [
        RabbitMqTopology.OrderCreatedQueue,
        RabbitMqTopology.DeadQueue,
        .. Enumerable.Range(1, new ConsumerOptions().MaxAttempts - 1).Select(RabbitMqTopology.RetryQueue),
    ];

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        Options = new RabbitMqOptions
        {
            Host = _container.Hostname,
            Port = _container.GetMappedPublicPort(RabbitMqBuilder.RabbitMqPort),
            User = Credential,
            Password = Credential,
        };

        _connection = await new ConnectionFactory
        {
            HostName = Options.Host,
            Port = Options.Port,
            UserName = Options.User,
            Password = Options.Password,
            ClientProvidedName = "integration-tests",
        }.CreateConnectionAsync();
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>Declares the topology (so a purge cannot 404 on a fresh broker) and empties every queue.</summary>
    public async Task PurgeAsync()
    {
        await using var channel = await OpenChannelAsync();
        var defaults = new ConsumerOptions();
        await RabbitMqTopology.DeclareAsync(channel, defaults.MaxAttempts, defaults.BaseBackoff, defaults.MaxBackoff, CancellationToken.None);

        foreach (var queue in Queues)
        {
            await channel.QueuePurgeAsync(queue);
        }
    }

    /// <summary>Messages sitting ready in <paramref name="queue"/> — a passive declare, exact, not a stats sample.</summary>
    public async Task<uint> ReadyCountAsync(string queue)
    {
        await using var channel = await OpenChannelAsync();
        return await channel.MessageCountAsync(queue);
    }

    private Task<IChannel> OpenChannelAsync() =>
        (_connection ?? throw new InvalidOperationException("The broker has not been started.")).CreateChannelAsync();
}
