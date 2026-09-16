using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Dapper;
using Ordering.IntegrationTests.Backend;
using Ordering.IntegrationTests.Persistence;
using static Ordering.IntegrationTests.Api.OrdersApi;

namespace Ordering.IntegrationTests.Api;

/// <summary>
/// Task 5 — the idempotency lifecycle of <c>POST /api/orders</c>, end to end through the real host
/// and a real SQL Server. Invariants are asserted against the database, not the response. Every
/// test works on a product of its own, so the shared database is never reset. Task 8's required
/// tests 2 (same key, concurrent) and 3 (key reuse, changed quantity) live here.
/// </summary>
[Collection(BackendCollection.Name)]
public sealed class IdempotencyTests(BackendFixture backend, OrderingApiFactory api) : IClassFixture<OrderingApiFactory>, IDisposable
{
    private readonly SqlServerFixture _db = backend.Sql;
    private readonly HttpClient _client = api.CreateClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task CreateOrder_WhenSameKeyAndEquivalentPayload_ReplaysTheOrderAndDeductsStockOnce()
    {
        var code = await AddProductAsync(stock: 10);
        var key = Guid.NewGuid().ToString();
        var customer = Customer();

        using var first = await _client.PostOrderAsync(key, $$"""{"customerReference":"{{customer}}","lines":[{"productCode":"{{code}}","quantity":2}]}""");
        // Same goods, same customer: other key order, extra whitespace, lower-case code, padded reference.
        using var second = await _client.PostOrderAsync(key, $$"""
            {
              "lines" : [ { "quantity" : 2 , "productCode" : "{{code.ToLowerInvariant()}}" } ],
              "customerReference" : "  {{customer}}  "
            }
            """);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var firstBody = await first.ReadJsonAsync();
        var secondBody = await second.ReadJsonAsync();
        Assert.Equal(firstBody.GetProperty("id").GetString(), secondBody.GetProperty("id").GetString());
        Assert.Equal("Confirmed", secondBody.GetProperty("status").GetString());
        Assert.Equal(8, await _db.StockAsync(code));
        Assert.Equal(1, await _db.CountAsync("SELECT COUNT(*) FROM orders WHERE customer_reference = @customer", new { customer }));
    }

    /// <summary>Task 8, required test 3.</summary>
    [Fact]
    public async Task CreateOrder_WhenSameKeyAndChangedQuantity_Is409KeyReuseAndWritesNothing()
    {
        var code = await AddProductAsync(stock: 10);
        var key = Guid.NewGuid().ToString();
        var customer = Customer();
        using var first = await _client.PostOrderAsync(key, OrderBody(customer, (code, 1)));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var before = await _db.SnapshotAsync(code);

        using var reuse = await _client.PostOrderAsync(key, OrderBody(customer, (code, 2)));

        Assert.Equal(HttpStatusCode.Conflict, reuse.StatusCode);
        Assert.Equal("idempotency.key_reuse", await reuse.CodeAsync());
        Assert.Equal(before, await _db.SnapshotAsync(code));
    }

    /// <summary>Task 8, required test 2. None of the waiters may hit the 3 s lock timeout (that would be a 409 idempotency.in_progress).</summary>
    [Fact]
    public async Task CreateOrder_WhenTwentyConcurrentRequestsShareAKey_CreatesOneOrderAndDeductsOnce()
    {
        var code = await AddProductAsync(stock: 100);
        var key = Guid.NewGuid().ToString();
        var customer = Customer();
        var body = OrderBody(customer, (code, 3));

        var responses = await Concurrency.BurstAsync(20, _ => _client.PostOrderAsync(key, body));

        try
        {
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.OK));

            var ids = new HashSet<string>();
            foreach (var response in responses)
            {
                ids.Add((await response.ReadJsonAsync()).GetProperty("id").GetString()!);
            }

            var orderId = long.Parse(Assert.Single(ids));
            Assert.Equal(1, await _db.CountAsync("SELECT COUNT(*) FROM orders WHERE customer_reference = @customer", new { customer }));
            Assert.Equal(1, await _db.CountAsync("SELECT COUNT(*) FROM idempotency_keys WHERE idempotency_key = @key", new { key }));
            Assert.Equal(1, await _db.CountAsync("SELECT COUNT(*) FROM outbox_messages WHERE type = 'order.created' AND aggregate_id = @orderId", new { orderId }));
            Assert.Equal(97, await _db.StockAsync(code));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task CreateOrder_WithoutIdempotencyKey_Is400()
    {
        var code = await AddProductAsync(stock: 1);

        using var response = await _client.PostOrderAsync(idempotencyKey: null, OrderBody(Customer(), (code, 1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.True(body.GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
        Assert.Equal(1, await _db.StockAsync(code));
    }

    [Fact]
    public async Task CreateOrder_WhenReplayedAfterCancellation_Returns200CancelledWithoutDeductingAgain()
    {
        var code = await AddProductAsync(stock: 10);
        var key = Guid.NewGuid().ToString();
        var body = OrderBody(Customer(), (code, 4));
        using var first = await _client.PostOrderAsync(key, body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var orderId = OrderId(await first.ReadJsonAsync());

        using var cancel = await _client.CancelOrderAsync(orderId);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(10, await _db.StockAsync(code));

        using var replay = await _client.PostOrderAsync(key, body);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await replay.ReadJsonAsync();
        Assert.Equal(orderId, OrderId(replayed));
        Assert.Equal("Cancelled", replayed.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, replayed.GetProperty("cancelledAt").ValueKind);
        Assert.Equal(10, await _db.StockAsync(code));
        Assert.Equal(1, await _db.CountAsync("SELECT COUNT(*) FROM idempotency_keys WHERE idempotency_key = @key", new { key }));
    }

    [Fact]
    public async Task CreateOrder_WhenStockConflicts_DoesNotConsumeTheKey()
    {
        var code = await AddProductAsync(stock: 0);
        var key = Guid.NewGuid().ToString();
        var body = OrderBody(Customer(), (code, 2));

        using var conflict = await _client.PostOrderAsync(key, body);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("stock.insufficient", await conflict.CodeAsync());
        Assert.Equal(0, await _db.CountAsync("SELECT COUNT(*) FROM idempotency_keys WHERE idempotency_key = @key", new { key }));

        await _db.ExecuteAsync("UPDATE products SET available_quantity = 5 WHERE code = @code", new { code });

        using var retry = await _client.PostOrderAsync(key, body);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(3, await _db.StockAsync(code));
        Assert.Equal(1, await _db.CountAsync("SELECT COUNT(*) FROM idempotency_keys WHERE idempotency_key = @key", new { key }));
    }

    [Fact]
    public async Task CreateOrder_WhenProductRowStaysLocked_Is503StockBusyAfterTheLockTimeoutAndWritesNothing()
    {
        var code = await AddProductAsync(stock: 10);
        var key = Guid.NewGuid().ToString();
        var customer = Customer();

        // An update lock blocks the conditional UPDATE (U/X) but not the catalogue SELECT (S), so
        // the request reaches the stock statement. Task 12's RCSI lets an exclusive lock do the same.
        await using var blocker = await _db.OpenConnectionAsync();
        await using var holding = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("SELECT available_quantity FROM products WITH (UPDLOCK, ROWLOCK) WHERE code = @code", new { code }, holding);

        var stopwatch = Stopwatch.StartNew();
        using var response = await _client.PostOrderAsync(key, OrderBody(customer, (code, 1)));
        stopwatch.Stop();
        await holding.RollbackAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        Assert.Equal("stock.busy", await response.CodeAsync());
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(15));
        Assert.Equal(0, await _db.CountAsync("SELECT COUNT(*) FROM orders WHERE customer_reference = @customer", new { customer }));
        Assert.Equal(0, await _db.CountAsync("SELECT COUNT(*) FROM idempotency_keys WHERE idempotency_key = @key", new { key }));
        Assert.Equal(10, await _db.StockAsync(code));
    }

    [Fact]
    public async Task CreateOrder_RunsTheStockUpdateLastBeforeCommit()
    {
        var code = await AddProductAsync(stock: 10);
        api.Sql.Clear();

        using var response = await _client.PostOrderAsync(Guid.NewGuid().ToString(), OrderBody(Customer(), (code, 1)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var log = api.Sql.Entries.ToList();
        var commit = log.IndexOf(SqlStatementLog.Commit);
        Assert.True(commit > 0, string.Join(Environment.NewLine, log));
        Assert.StartsWith("SET LOCK_TIMEOUT 3000", log[0], StringComparison.Ordinal);
        Assert.StartsWith("UPDATE products", log[commit - 1], StringComparison.Ordinal);
        var batch = Assert.Single(log.Take(commit - 1), statement => statement.Contains("INSERT INTO [idempotency_keys]", StringComparison.Ordinal));
        Assert.Contains("INSERT INTO [orders]", batch, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO [outbox_messages]", batch, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Take(log.IndexOf(batch)), statement => statement.StartsWith("UPDATE products", StringComparison.Ordinal));
    }

    private Task<string> AddProductAsync(int stock) => _db.AddProductAsync("IDEM", stock);
}
