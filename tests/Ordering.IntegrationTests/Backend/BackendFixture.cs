using Ordering.IntegrationTests.Persistence;

namespace Ordering.IntegrationTests.Backend;

/// <summary>
/// The real backend, once per collection: SQL Server and RabbitMQ side by side, started
/// together. Every integration test class joins <see cref="BackendCollection"/>, so the whole
/// run pays for one container of each. Tests that leave rows or messages behind start from
/// <see cref="ResetAsync"/>; nothing relies on test order.
/// </summary>
public sealed class BackendFixture : IAsyncLifetime
{
    public SqlServerFixture Sql { get; } = new();

    public RabbitMqFixture RabbitMq { get; } = new();

    public Task InitializeAsync() => Task.WhenAll(Sql.InitializeAsync(), RabbitMq.InitializeAsync());

    public Task DisposeAsync() => Task.WhenAll(Sql.DisposeAsync(), RabbitMq.DisposeAsync());

    /// <summary>Empty tables, the two seed products back, every queue purged.</summary>
    public async Task ResetAsync()
    {
        await Sql.ResetAsync();
        await RabbitMq.PurgeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class BackendCollection : ICollectionFixture<BackendFixture>
{
    public const string Name = "Backend";
}
