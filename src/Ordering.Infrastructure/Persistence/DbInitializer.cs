using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ordering.Domain.Products;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// Applies migrations and seeds the catalogue. Runs on startup when <c>Database:InitializeOnStartup</c>
/// is true (Development) and from <c>dotnet run -- seed</c>; the test fixtures call it too.
/// </summary>
public static partial class DbInitializer
{
    public static readonly IReadOnlyList<Product> SeedProducts =
    [
        Product.Create("SKU-001", "Thermal label roll", 12.50m, 10),
        Product.Create("SKU-002", "Shipping tape", 4.00m, 40),
    ];

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        await MigrateAsync(context, cancellationToken);
        await SeedAsync(context, cancellationToken);

        LogInitialized(scope.ServiceProvider.GetRequiredService<ILogger<OrderingDbContext>>(), SeedProducts.Count);
    }

    public static Task MigrateAsync(OrderingDbContext context, CancellationToken cancellationToken) =>
        context.Database.MigrateAsync(cancellationToken);

    /// <summary>
    /// Idempotent: one guarded <c>INSERT</c> per product, no <c>MERGE</c>. Existing rows — including
    /// their current stock — are left untouched, so re-seeding never resets quantities.
    /// </summary>
    public static async Task SeedAsync(OrderingDbContext context, CancellationToken cancellationToken)
    {
        foreach (var product in SeedProducts)
        {
            await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO products (code, name, price, available_quantity)
                SELECT {product.Code}, {product.Name}, {product.Price}, {product.AvailableQuantity}
                WHERE NOT EXISTS (SELECT 1 FROM products WHERE code = {product.Code});
                """,
                cancellationToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrated and seeded ({ProductCount} products ensured)")]
    private static partial void LogInitialized(ILogger logger, int productCount);
}
