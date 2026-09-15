using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Ordering.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace Ordering.IntegrationTests.Persistence;

/// <summary>
/// One SQL Server 2022 container per test collection, migrated once. Tests share the database and
/// must leave it as they found it.
/// </summary>
// Task 8: add ResetAsync() (delete orders/outbox/idempotency rows, re-seed) and OrderingApiFactory on top of this.
public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // The container's own string points at master; migrations create and own the Ordering database.
        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = "Ordering" }.ConnectionString;

        await using var context = CreateContext();
        await DbInitializer.MigrateAsync(context, CancellationToken.None);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public OrderingDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<OrderingDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SqlServer";
}
