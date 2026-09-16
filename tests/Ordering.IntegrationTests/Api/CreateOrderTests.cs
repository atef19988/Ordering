using System.Net;
using Ordering.Application.Abstractions;
using Ordering.IntegrationTests.Backend;
using Ordering.IntegrationTests.Persistence;
using static Ordering.IntegrationTests.Api.OrdersApi;

namespace Ordering.IntegrationTests.Api;

/// <summary>
/// Task 8 required tests 1 and 5, and the Task 4 tests deferred to this harness: stock guarded by
/// the database under a real race, and a real transaction rolled back after every statement ran.
/// Each test works on products of its own; invariants are read back with Dapper.
/// </summary>
[Collection(BackendCollection.Name)]
public sealed class CreateOrderTests(BackendFixture backend, OrderingApiFactory api) : IClassFixture<OrderingApiFactory>, IDisposable
{
    private readonly SqlServerFixture _db = backend.Sql;
    private readonly HttpClient _client = api.CreateClient();

    public void Dispose() => _client.Dispose();

    /// <summary>Required test 1.</summary>
    [Fact]
    public async Task CreateOrder_WhenTwoOrdersRaceForLastUnit_OnlyOneSucceeds()
    {
        var code = await _db.AddProductAsync("RACE", stock: 1);
        var customers = new[] { Customer(), Customer() };

        var responses = await Concurrency.BurstAsync(2, i => _client.PostOrderAsync(Guid.NewGuid().ToString(), OrderBody(customers[i], (code, 1))));

        try
        {
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
            var conflict = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
            var problem = await conflict.ReadJsonAsync();
            Assert.Equal("stock.insufficient", problem.GetProperty("code").GetString());
            Assert.Equal(code, problem.GetProperty("productCode").GetString());
            Assert.Equal(0, problem.GetProperty("available").GetInt32());

            Assert.Equal(0, await _db.StockAsync(code));
            Assert.Equal(1, await _db.CountAsync("SELECT COUNT(*) FROM orders WHERE customer_reference IN @customers", new { customers }));
            Assert.Equal(1, await _db.CountAsync("SELECT COUNT(*) FROM order_lines WHERE product_code = @code", new { code }));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>The same guard under a wider burst: fifty orders, one unit, one winner, and the count never dips below zero.</summary>
    [Fact]
    public async Task CreateOrder_WhenFiftyOrdersRaceForLastUnit_ExactlyOneSucceedsAndStockIsZero()
    {
        var code = await _db.AddProductAsync("RACE", stock: 1);

        var responses = await Concurrency.BurstAsync(50, _ => _client.PostOrderAsync(Guid.NewGuid().ToString(), OrderBody(Customer(), (code, 1))));

        try
        {
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.Equal(49, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
            Assert.Equal(0, await _db.StockAsync(code));
            Assert.Equal(1, await _db.CountAsync("SELECT COUNT(*) FROM order_lines WHERE product_code = @code", new { code }));
            Assert.Equal(1, await _db.CountAsync(
                "SELECT COUNT(*) FROM outbox_messages m JOIN order_lines l ON l.order_id = m.aggregate_id WHERE m.type = 'order.created' AND l.product_code = @code",
                new { code }));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    /// <summary>
    /// Required test 5. The host really runs every statement — order, lines, outbox, key, the
    /// stock decrement — and then dies before <c>COMMIT</c>; the SQL log proves the decrement ran
    /// and a <c>ROLLBACK</c> followed it. Nothing is mocked: the rollback is SQL Server's.
    /// </summary>
    [Fact]
    public async Task CreateOrder_WhenTheProcessFailsAfterTheStockUpdateBeforeCommit_LeavesNoTrace()
    {
        var code = await _db.AddProductAsync("FAIL", stock: 10);
        var key = Guid.NewGuid().ToString();
        var before = await _db.SnapshotAsync(code);
        api.Failures.ThrowAt(FailurePoints.BeforeCommit);
        api.Sql.Clear();

        using var response = await _client.PostOrderAsync(key, OrderBody(Customer(), (code, 3)));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(before, await _db.SnapshotAsync(code));
        Assert.Equal(0, await _db.CountAsync("SELECT COUNT(*) FROM idempotency_keys WHERE idempotency_key = @key", new { key }));

        var log = api.Sql.Entries.ToList();
        Assert.DoesNotContain(SqlStatementLog.Commit, log);
        var rollback = log.IndexOf(SqlStatementLog.Rollback);
        Assert.True(rollback > 0, string.Join(Environment.NewLine, log));
        Assert.StartsWith("UPDATE products", log[rollback - 1], StringComparison.Ordinal);
        Assert.Contains(log.Take(rollback), statement => statement.Contains("INSERT INTO [outbox_messages]", StringComparison.Ordinal));
    }

    /// <summary>After the injected failure the host is healthy: the next request with the same key commits normally.</summary>
    [Fact]
    public async Task CreateOrder_AfterAFailureBeforeCommit_TheSameKeyCanBeSubmittedAgain()
    {
        var code = await _db.AddProductAsync("FAIL", stock: 10);
        var key = Guid.NewGuid().ToString();
        var body = OrderBody(Customer(), (code, 3));
        api.Failures.ThrowAt(FailurePoints.BeforeCommit);
        using var failed = await _client.PostOrderAsync(key, body);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        using var retry = await _client.PostOrderAsync(key, body);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(7, await _db.StockAsync(code));
    }

    /// <summary>
    /// Task 4, deferred: a two-line order whose second line is short. The first line's decrement
    /// really ran (it is in the log, before the rollback) and was undone with the transaction.
    /// </summary>
    [Fact]
    public async Task CreateOrder_WhenTheSecondLineIsShort_LeavesEveryProductUntouched()
    {
        // Prefixes keep code order = lock order: the in-stock product is decremented first.
        var plenty = await _db.AddProductAsync("MLA", stock: 10);
        var empty = await _db.AddProductAsync("MLB", stock: 0);
        var before = (await _db.SnapshotAsync(plenty), await _db.StockAsync(empty));
        api.Sql.Clear();

        using var response = await _client.PostOrderAsync(Guid.NewGuid().ToString(), OrderBody(Customer(), (plenty, 2), (empty, 1)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.ReadJsonAsync();
        Assert.Equal("stock.insufficient", problem.GetProperty("code").GetString());
        Assert.Equal(empty, problem.GetProperty("productCode").GetString());
        Assert.Equal(before, (await _db.SnapshotAsync(plenty), await _db.StockAsync(empty)));

        var log = api.Sql.Entries.ToList();
        var rollback = log.IndexOf(SqlStatementLog.Rollback);
        Assert.True(rollback > 0, string.Join(Environment.NewLine, log));
        Assert.DoesNotContain(SqlStatementLog.Commit, log);
        Assert.Equal(2, log.Take(rollback).Count(statement => statement.StartsWith("UPDATE products", StringComparison.Ordinal)));
    }
}
