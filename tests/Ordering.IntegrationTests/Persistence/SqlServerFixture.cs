using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Ordering.Infrastructure.Persistence;
using Testcontainers.MsSql;

namespace Ordering.IntegrationTests.Persistence;

/// <summary>
/// One SQL Server 2022 container per test collection, migrated once, owned by
/// <c>BackendFixture</c>. Tests share the database and either leave it as they found it or start
/// from <see cref="ResetAsync"/>. The query helpers below are how a test reads an invariant
/// straight from the database with Dapper, never through the API it is testing.
/// </summary>
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

    /// <summary>
    /// Empties every table the order paths write (FK order — SQL Server refuses TRUNCATE on a
    /// referenced table) and re-seeds the catalogue, so a test can start from the known two products.
    /// </summary>
    public async Task ResetAsync()
    {
        await ExecuteAsync(
            """
            DELETE FROM order_lines;
            DELETE FROM idempotency_keys;
            DELETE FROM outbox_messages;
            DELETE FROM orders;
            DELETE FROM products;
            """);

        await using var context = CreateContext();
        await DbInitializer.SeedAsync(context, CancellationToken.None);
    }

    public OrderingDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<OrderingDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task<SqlConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = await OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<T>(sql, parameters)
            ?? throw new InvalidOperationException($"Scalar query returned no value: {sql}");
    }

    public async Task<int> ExecuteAsync(string sql, object? parameters = null)
    {
        await using var connection = await OpenConnectionAsync();
        return await connection.ExecuteAsync(sql, parameters);
    }

    public Task<int> CountAsync(string sql, object? parameters = null) => ScalarAsync<int>(sql, parameters);

    public Task<int> StockAsync(string code) =>
        ScalarAsync<int>("SELECT available_quantity FROM products WHERE code = @code", new { code });

    /// <summary>A product of the test's own, so tests that do not reset the database cannot see each other.</summary>
    public async Task<string> AddProductAsync(string prefix, int stock, decimal price = 12.50m)
    {
        var code = $"{prefix}-{Guid.NewGuid():N}"[..20];
        await ExecuteAsync(
            "INSERT INTO products (code, name, price, available_quantity) VALUES (@code, 'test product', @price, @stock)",
            new { code, price, stock });
        return code;
    }

    /// <summary>Row counts of every table the create path writes, plus one product's stock: what must not move when a request is refused.</summary>
    public async Task<WriteSnapshot> SnapshotAsync(string code)
    {
        await using var connection = await OpenConnectionAsync();
        return await connection.QuerySingleAsync<WriteSnapshot>(
            """
            SELECT (SELECT COUNT(*) FROM orders)                                AS Orders,
                   (SELECT COUNT(*) FROM order_lines)                           AS Lines,
                   (SELECT COUNT(*) FROM outbox_messages)                       AS Outbox,
                   (SELECT COUNT(*) FROM idempotency_keys)                      AS Keys,
                   (SELECT available_quantity FROM products WHERE code = @code) AS Stock;
            """,
            new { code });
    }

    /// <summary>The <c>order.created</c> outbox row of an order, as the relay and consumer leave it.</summary>
    public async Task<OutboxRow> OutboxRowAsync(long orderId)
    {
        await using var connection = await OpenConnectionAsync();
        return await connection.QuerySingleAsync<OutboxRow>(
            """
            SELECT id AS Id, status AS Status, attempt_count AS AttemptCount, last_error AS LastError
              FROM outbox_messages
             WHERE type = 'order.created' AND aggregate_id = @orderId;
            """,
            new { orderId });
    }
}

public sealed record WriteSnapshot(int Orders, int Lines, int Outbox, int Keys, int Stock);

public sealed record OutboxRow(long Id, string Status, int AttemptCount, string? LastError);
