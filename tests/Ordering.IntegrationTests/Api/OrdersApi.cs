using System.Net;
using System.Text;
using System.Text.Json;

namespace Ordering.IntegrationTests.Api;

/// <summary>
/// The three calls every test makes, on a plain <see cref="HttpClient"/>: no client library, the
/// wire format is the contract. Ids are JSON strings (Snowflakes exceed 2^53) and are parsed
/// with <see cref="long.Parse(string)"/> before they meet the database.
/// </summary>
public static class OrdersApi
{
    public static string Customer() => $"CUST-{Guid.NewGuid():N}"[..20];

    public static string OrderBody(string customer, params (string Code, int Quantity)[] lines) =>
        JsonSerializer.Serialize(new { customerReference = customer, lines = lines.Select(l => new { productCode = l.Code, quantity = l.Quantity }) });

    public static async Task<HttpResponseMessage> PostOrderAsync(this HttpClient client, string? idempotencyKey, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Submits with a fresh key and returns the new order's id; fails the test on anything but 201.</summary>
    public static async Task<long> CreateOrderAsync(this HttpClient client, string json)
    {
        using var response = await client.PostOrderAsync(Guid.NewGuid().ToString(), json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return OrderId(await response.ReadJsonAsync());
    }

    public static Task<HttpResponseMessage> CancelOrderAsync(this HttpClient client, long orderId) =>
        client.PostAsync($"/api/orders/{orderId}/cancel", content: null);

    public static async Task<JsonElement> GetOrderAsync(this HttpClient client, long orderId)
    {
        using var response = await client.GetAsync($"/api/orders/{orderId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public static long OrderId(JsonElement order) => long.Parse(order.GetProperty("id").GetString()!);

    /// <summary>The <c>code</c> extension of a ProblemDetails body.</summary>
    public static async Task<string?> CodeAsync(this HttpResponseMessage response) =>
        (await response.ReadJsonAsync()).GetProperty("code").GetString();
}
