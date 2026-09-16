using System.Net;
using Ordering.IntegrationTests.Backend;
using Ordering.IntegrationTests.Persistence;
using static Ordering.IntegrationTests.Api.OrdersApi;

namespace Ordering.IntegrationTests.Api;

/// <summary>
/// Task 8 required test 4 and the Task 6 tests deferred to this harness: of any number of racing
/// cancels exactly one restores stock, because the guarded <c>UPDATE … WHERE status =
/// 'Confirmed'</c> is the lock. The SQL log shows the guard ran for every cancel and the restore
/// ran once.
/// </summary>
[Collection(BackendCollection.Name)]
public sealed class CancelOrderTests(BackendFixture backend, OrderingApiFactory api) : IClassFixture<OrderingApiFactory>, IDisposable
{
    private readonly SqlServerFixture _db = backend.Sql;
    private readonly HttpClient _client = api.CreateClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task CancelOrder_WhenTwoCancelsRace_RestoresStockExactlyOnce()
    {
        var code = await _db.AddProductAsync("CANCEL", stock: 10);
        var orderId = await _client.CreateOrderAsync(OrderBody(Customer(), (code, 4)));
        Assert.Equal(6, await _db.StockAsync(code));
        api.Sql.Clear();

        var responses = await Concurrency.BurstAsync(2, _ => _client.CancelOrderAsync(orderId));

        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            foreach (var response in responses)
            {
                Assert.Equal("Cancelled", (await response.ReadJsonAsync()).GetProperty("status").GetString());
            }

            Assert.Equal("Cancelled", await _db.ScalarAsync<string>("SELECT status FROM orders WHERE id = @orderId", new { orderId }));
            Assert.Equal(10, await _db.StockAsync(code)); // back to the pre-order value: not 14, not 6

            // The guard is the mechanism: both cancels ran it, only the winner restored.
            var log = api.Sql.Entries.ToList();
            var cancels = log.Where(s => s.StartsWith("UPDATE orders", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, cancels.Count);
            Assert.All(cancels, s => Assert.Contains("AND status = @p", s, StringComparison.Ordinal));
            Assert.Equal(1, log.Count(s => s.StartsWith("UPDATE products", StringComparison.Ordinal) && s.Contains("available_quantity + @p", StringComparison.Ordinal)));
            Assert.Equal(2, log.Count(s => s == SqlStatementLog.Commit));
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
    public async Task CancelOrder_WhenTenCancelsRace_RestoresStockExactlyOnce()
    {
        var a = await _db.AddProductAsync("CANA", stock: 10);
        var b = await _db.AddProductAsync("CANB", stock: 20);
        var orderId = await _client.CreateOrderAsync(OrderBody(Customer(), (a, 3), (b, 5)));
        Assert.Equal((7, 15), (await _db.StockAsync(a), await _db.StockAsync(b)));

        var responses = await Concurrency.BurstAsync(10, _ => _client.CancelOrderAsync(orderId));

        try
        {
            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            Assert.Equal((10, 20), (await _db.StockAsync(a), await _db.StockAsync(b)));
            Assert.Equal(1, await _db.CountAsync("SELECT COUNT(*) FROM orders WHERE id = @orderId AND status = 'Cancelled' AND cancelled_at IS NOT NULL", new { orderId }));
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
    public async Task CancelOrder_WhenAlreadyCancelled_Is200WithTheSameCancelledAtAndNoSecondRestore()
    {
        var code = await _db.AddProductAsync("CANCEL", stock: 10);
        var orderId = await _client.CreateOrderAsync(OrderBody(Customer(), (code, 2)));
        using var first = await _client.CancelOrderAsync(orderId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await _client.CancelOrderAsync(orderId);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            (await first.ReadJsonAsync()).GetProperty("cancelledAt").GetString(),
            (await second.ReadJsonAsync()).GetProperty("cancelledAt").GetString());
        Assert.Equal(10, await _db.StockAsync(code));
    }

    [Fact]
    public async Task CancelOrder_WhenUnknown_Is404WithOrderNotFoundCode()
    {
        using var response = await _client.CancelOrderAsync(orderId: 1);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("order.not_found", await response.CodeAsync());
    }
}
