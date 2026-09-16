using StackExchange.Redis;
using Testcontainers.Redis;

namespace Ordering.IntegrationTests.Backend;

/// <summary>
/// One Redis 7 container per test collection: the output cache every test host shares. Reset
/// flushes it, so a page cached by one test (1 s TTL) can never answer the next test's read of
/// a freshly reset catalogue.
/// </summary>
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7-alpine").Build();

    private ConnectionMultiplexer? _connection;

    /// <summary>What a host puts in <c>Redis:Configuration</c>.</summary>
    public string Configuration { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Configuration = $"{_container.GetConnectionString()},abortConnect=false";
        _connection = await ConnectionMultiplexer.ConnectAsync(Configuration + ",allowAdmin=true");
    }

    public async Task DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>Empties every key: cached catalogue pages and, from Task 14, gate and idempotency entries.</summary>
    public async Task FlushAsync()
    {
        var connection = _connection ?? throw new InvalidOperationException("Redis has not been started.");

        foreach (var endpoint in connection.GetEndPoints())
        {
            await connection.GetServer(endpoint).FlushAllDatabasesAsync();
        }
    }
}
