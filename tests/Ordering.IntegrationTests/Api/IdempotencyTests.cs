using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Dapper;
using Ordering.IntegrationTests.Persistence;

namespace Ordering.IntegrationTests.Api;

/// <summary>
/// Task 5 — the idempotency lifecycle of <c>POST /api/orders</c>, end to end through the real host
/// and a real SQL Server. Invariants are asserted against the database, not the response. Every
/// test works on a product of its own, so the shared database is never reset.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class IdempotencyTests(SqlServerFixture fixture, OrderingApiFactory api) : IClassFixture<OrderingApiFactory>, IDisposable
{
    private readonly HttpClient _client = api.CreateClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task CreateOrder_WhenSameKeyAndEquivalentPayload_ReplaysTheOrderAndDeductsStockOnce()
    {
        var code = await AddProductAsync(stock: 10);
        var key = Guid.NewGuid().ToString();
        var customer = Customer();

        using var first = await PostAsync(key, $$"""{"customerReference":"{{customer}}","lines":[{"productCode":"{{code}}","quantity":2}]}""");
        // Same goods, same customer: other key order, extra whitespace, lower-case code, padded reference.
        using var second = await PostAsync(key, $$"""
            {
              "lines" : [ { "quantity" : 2 , "productCode" : "{{code.ToLowerInvariant()}}" } ],
              "customerReference" : "  {{customer}}  "
            }
            """);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var firstBody = await ReadJsonAsync(first);
        var secondBody = await ReadJsonAsync(second);
        Assert.Equal(firstBody.GetProperty("id").GetString(), secondBody.GetProperty("id").GetString());
        Assert.Equal("Confirmed", secondBody.GetProperty("status").GetString());
        Assert.Equal(8, await StockAsync(code));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM orders WHERE customer_reference = @customer", new { customer }));
    }

    [Fact]
    public async Task CreateOrder_WhenSameKeyAndChangedQuantity_Is409KeyReuseAndWritesNothing()
    {
        var code = await AddProductAsync(stock: 10);
        var key = Guid.NewGuid().ToString();
        var customer = Customer();
        using var first = await PostAsync(key, Body(customer, (code, 1)));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var before = await SnapshotAsync(code);

        using var reuse = await PostAsync(key, Body(customer, (code, 2)));

        Assert.Equal(HttpStatusCode.Conflict, reuse.StatusCode);
        Assert.Equal("idempotency.key_reuse", (await ReadJsonAsync(reuse)).GetProperty("code").GetString());
        Assert.Equal(before, await SnapshotAsync(code));
    }

    [Fact]
    public async Task CreateOrder_WhenTwentyConcurrentRequestsShareAKey_CreatesOneOrderAndDeductsOnce()
    {
        var code = await AddProductAsync(stock: 100);
        var key = Guid.NewGuid().ToString();
        var customer = Customer();
        var body = Body(customer, (code, 3));

        // Released together by one barrier, not trickled in by a loop.
        var gate = new TaskCompletionSource();
        var requests = Enumerable.Range(0, 20)
            .Select(async _ =>
            {
                await gate.Task;
                return await PostAsync(key, body);
            })
            .ToArray();
        gate.SetResult();
        var responses = await Task.WhenAll(requests);

        try
        {
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
            Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.OK));

            var ids = new HashSet<string>();
            foreach (var response in responses)
            {
                ids.Add((await ReadJsonAsync(response)).GetProperty("id").GetString()!);
            }

            var orderId = long.Parse(Assert.Single(ids));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM orders WHERE customer_reference = @customer", new { customer }));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM idempotency_keys WHERE idempotency_key = @key", new { key }));
            Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM outbox_messages WHERE type = 'order.created' AND aggregate_id = @orderId", new { orderId }));
            Assert.Equal(97, await StockAsync(code));
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

        using var response = await PostAsync(key: null, Body(Customer(), (code, 1)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
        Assert.Equal(1, await StockAsync(code));
    }

    [Fact]
    public async Task CreateOrder_WhenReplayedAfterCancellation_Returns200CancelledWithoutDeductingAgain()
    {
        var code = await AddProductAsync(stock: 10);
        var key = Guid.NewGuid().ToString();
        var body = Body(Customer(), (code, 4));
        using var first = await PostAsync(key, body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var orderId = long.Parse((await ReadJsonAsync(first)).GetProperty("id").GetString()!);

        // Task 6: replace with POST /api/orders/{id}/cancel once it exists; until then the guarded transition is applied by hand.
        await using (var connection = await fixture.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE orders SET status = 'Cancelled', cancelled_at = SYSDATETIMEOFFSET() WHERE id = @orderId AND status = 'Confirmed'",
                new { orderId });
        }

        using var replay = await PostAsync(key, body);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await ReadJsonAsync(replay);
        Assert.Equal(orderId, long.Parse(replayed.GetProperty("id").GetString()!));
        Assert.Equal("Cancelled", replayed.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, replayed.GetProperty("cancelledAt").ValueKind);
        Assert.Equal(6, await StockAsync(code));
    }

    [Fact]
    public async Task CreateOrder_WhenStockConflicts_DoesNotConsumeTheKey()
    {
        var code = await AddProductAsync(stock: 0);
        var key = Guid.NewGuid().ToString();
        var body = Body(Customer(), (code, 2));

        using var conflict = await PostAsync(key, body);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("stock.insufficient", (await ReadJsonAsync(conflict)).GetProperty("code").GetString());
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM idempotency_keys WHERE idempotency_key = @key", new { key }));

        await using (var connection = await fixture.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE products SET available_quantity = 5 WHERE code = @code", new { code });
        }

        using var retry = await PostAsync(key, body);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(3, await StockAsync(code));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM idempotency_keys WHERE idempotency_key = @key", new { key }));
    }

    [Fact]
    public async Task CreateOrder_WhenProductRowStaysLocked_Is503StockBusyAfterTheLockTimeoutAndWritesNothing()
    {
        var code = await AddProductAsync(stock: 10);
        var key = Guid.NewGuid().ToString();
        var customer = Customer();

        // An update lock blocks the conditional UPDATE (U/X) but not the catalogue SELECT (S), so
        // the request reaches the stock statement. Task 12's RCSI lets an exclusive lock do the same.
        await using var blocker = await fixture.OpenConnectionAsync();
        await using var holding = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("SELECT available_quantity FROM products WITH (UPDLOCK, ROWLOCK) WHERE code = @code", new { code }, holding);

        var stopwatch = Stopwatch.StartNew();
        using var response = await PostAsync(key, Body(customer, (code, 1)));
        stopwatch.Stop();
        await holding.RollbackAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
        Assert.Equal("stock.busy", (await ReadJsonAsync(response)).GetProperty("code").GetString());
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(15));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM orders WHERE customer_reference = @customer", new { customer }));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM idempotency_keys WHERE idempotency_key = @key", new { key }));
        Assert.Equal(10, await StockAsync(code));
    }

    [Fact]
    public async Task CreateOrder_RunsTheStockUpdateLastBeforeCommit()
    {
        var code = await AddProductAsync(stock: 10);
        api.Sql.Clear();

        using var response = await PostAsync(Guid.NewGuid().ToString(), Body(Customer(), (code, 1)));

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

    private static string Customer() => $"CUST-{Guid.NewGuid():N}"[..20];

    private static string Body(string customer, params (string Code, int Quantity)[] lines) =>
        JsonSerializer.Serialize(new { customerReference = customer, lines = lines.Select(l => new { productCode = l.Code, quantity = l.Quantity }) });

    private async Task<HttpResponseMessage> PostAsync(string? key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<string> AddProductAsync(int stock)
    {
        var code = $"IDEM-{Guid.NewGuid():N}"[..20];
        await using var connection = await fixture.OpenConnectionAsync();
        await connection.ExecuteAsync(
            "INSERT INTO products (code, name, price, available_quantity) VALUES (@code, 'idempotency probe', 12.50, @stock)",
            new { code, stock });
        return code;
    }

    private async Task<int> StockAsync(string code)
    {
        await using var connection = await fixture.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT available_quantity FROM products WHERE code = @code", new { code });
    }

    private async Task<int> CountAsync(string sql, object parameters)
    {
        await using var connection = await fixture.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(sql, parameters);
    }

    /// <summary>Row counts of every table the create path writes, plus the product's stock.</summary>
    private async Task<(int Orders, int Lines, int Outbox, int Keys, int Stock)> SnapshotAsync(string code)
    {
        await using var connection = await fixture.OpenConnectionAsync();
        return await connection.QuerySingleAsync<(int, int, int, int, int)>(
            """
            SELECT (SELECT COUNT(*) FROM orders),
                   (SELECT COUNT(*) FROM order_lines),
                   (SELECT COUNT(*) FROM outbox_messages),
                   (SELECT COUNT(*) FROM idempotency_keys),
                   (SELECT available_quantity FROM products WHERE code = @code)
            """,
            new { code });
    }
}
