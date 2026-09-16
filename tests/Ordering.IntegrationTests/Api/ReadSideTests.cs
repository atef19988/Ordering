using System.Net;
using System.Text.Json;
using Ordering.IntegrationTests.Backend;
using static Ordering.IntegrationTests.Api.OrdersApi;

namespace Ordering.IntegrationTests.Api;

/// <summary>
/// The Task 3 tests deferred to this harness: the Dapper read side on the wire. Starts from a
/// reset so the catalogue is exactly the two seed products.
/// </summary>
[Collection(BackendCollection.Name)]
public sealed class ReadSideTests(BackendFixture backend, OrderingApiFactory api) : IClassFixture<OrderingApiFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = api.CreateClient();

    public Task InitializeAsync() => backend.ResetAsync();

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetProducts_ReturnsSeededProductsWithExactDecimalPrices()
    {
        using var response = await _client.GetAsync("/api/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();

        // The raw text is the proof: a float anywhere on the path would print 12.5 or 12.500000000000002.
        Assert.Contains("\"price\":12.50", json, StringComparison.Ordinal);
        Assert.Contains("\"price\":4.00", json, StringComparison.Ordinal);

        var products = JsonDocument.Parse(json).RootElement.EnumerateArray()
            .Select(p => (Code: p.GetProperty("code").GetString(), Price: p.GetProperty("price").GetDecimal(), Stock: p.GetProperty("availableQuantity").GetInt32()))
            .OrderBy(p => p.Code)
            .ToList();
        Assert.Equal([("SKU-001", 12.50m, 10), ("SKU-002", 4.00m, 40)], products);
    }

    [Fact]
    public async Task GetOrder_WhenUnknown_Is404ProblemDetailsWithOrderNotFoundCode()
    {
        using var response = await _client.GetAsync("/api/orders/1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.ReadJsonAsync();
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("order.not_found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetOrder_ReturnsLinesInProductCodeOrderWithExactLineTotals()
    {
        var orderId = await _client.CreateOrderAsync(OrderBody(Customer(), ("SKU-002", 3), ("SKU-001", 2)));

        var order = await _client.GetOrderAsync(orderId);

        Assert.Equal(37.00m, order.GetProperty("total").GetDecimal());
        Assert.Equal("Pending", order.GetProperty("notificationStatus").GetString());
        Assert.Equal(0, order.GetProperty("notificationAttempts").GetInt32());
        var lines = order.GetProperty("lines").EnumerateArray()
            .Select(l => (l.GetProperty("productCode").GetString(), l.GetProperty("quantity").GetInt32(), l.GetProperty("lineTotal").GetDecimal()))
            .ToList();
        Assert.Equal([("SKU-001", 2, 25.00m), ("SKU-002", 3, 12.00m)], lines);
    }
}
