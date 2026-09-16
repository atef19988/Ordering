using Ordering.IntegrationTests.Backend;
using Dapper;
using Ordering.Infrastructure.Persistence;

namespace Ordering.IntegrationTests.Persistence;

[Collection(BackendCollection.Name)]
public class DbInitializerTests(BackendFixture backend)
{
    [Fact]
    public async Task Seeding_twice_leaves_exactly_the_two_products()
    {
        await backend.Sql.ResetAsync();
        await using var connection = await backend.Sql.OpenConnectionAsync();
        await using var context = backend.Sql.CreateContext();

        await DbInitializer.SeedAsync(context, CancellationToken.None);
        await DbInitializer.SeedAsync(context, CancellationToken.None);

        var products = (await connection.QueryAsync<(string Code, string Name, decimal Price, int AvailableQuantity)>(
            "SELECT code, name, price, available_quantity FROM products ORDER BY code")).ToList();

        Assert.Equal(
            [("SKU-001", "Thermal label roll", 12.50m, 10), ("SKU-002", "Shipping tape", 4.00m, 40)],
            products);
    }

    [Fact]
    public async Task Reseeding_does_not_reset_stock_of_existing_products()
    {
        await backend.Sql.ResetAsync();
        await using var connection = await backend.Sql.OpenConnectionAsync();
        await using var context = backend.Sql.CreateContext();
        await connection.ExecuteAsync("UPDATE products SET available_quantity = 3 WHERE code = 'SKU-001'");

        await DbInitializer.SeedAsync(context, CancellationToken.None);

        Assert.Equal(3, await connection.ExecuteScalarAsync<int>("SELECT available_quantity FROM products WHERE code = 'SKU-001'"));
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM products"));

        await connection.ExecuteAsync("UPDATE products SET available_quantity = 10 WHERE code = 'SKU-001'");
    }
}
