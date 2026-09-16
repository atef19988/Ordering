using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
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

    /// <summary>
    /// <c>dotnet run -- seed --products N</c>: the load-test catalogue <c>LOAD-000001 … LOAD-{N}</c>.
    /// Names come from a small word list so there are ties and shared prefixes, prices are spread
    /// over a range, stock is <see cref="LoadCatalogue.Quantity"/>. Guarded per code like the two
    /// brief products: a re-run adds nothing, a larger N adds only the missing tail.
    /// </summary>
    public static async Task SeedLoadCatalogueAsync(IServiceProvider services, int count, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        var started = Stopwatch.GetTimestamp();
        var inserted = await SeedLoadCatalogueAsync(context, count, cancellationToken);

        LogLoadCatalogue(scope.ServiceProvider.GetRequiredService<ILogger<OrderingDbContext>>(), inserted, count, Stopwatch.GetElapsedTime(started).TotalSeconds);
    }

    /// <summary>
    /// Bulk-copies each batch into a temp table on the context's connection, then inserts only
    /// the codes that do not exist yet — the guard is one set-based statement per batch, so
    /// 100,000 rows take seconds. Returns how many rows were actually added.
    /// </summary>
    public static async Task<int> SeedLoadCatalogueAsync(OrderingDbContext context, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var connection = (SqlConnection)context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using (var create = connection.CreateCommand())
            {
                create.CommandText = LoadCatalogue.CreateStaging;
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            var inserted = 0;

            for (var first = 1; first <= count; first += LoadCatalogue.BatchSize)
            {
                var last = Math.Min(count, first + LoadCatalogue.BatchSize - 1);

                using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = LoadCatalogue.StagingTable, BatchSize = LoadCatalogue.BatchSize })
                {
                    await bulk.WriteToServerAsync(LoadCatalogue.Rows(first, last), cancellationToken);
                }

                await using var insert = connection.CreateCommand();
                insert.CommandText = LoadCatalogue.InsertMissing;
                inserted += await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            return inserted;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrated and seeded ({ProductCount} products ensured)")]
    private static partial void LogInitialized(ILogger logger, int productCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Load catalogue seeded: {Inserted} of {Requested} products added in {Seconds:0.0} s")]
    private static partial void LogLoadCatalogue(ILogger logger, int inserted, int requested, double seconds);

    /// <summary>The generated rows and the statements around them.</summary>
    private static class LoadCatalogue
    {
        public const int BatchSize = 10_000;
        public const int Quantity = 1_000;
        public const string StagingTable = "#load_products";

        public const string CreateStaging =
            """
            CREATE TABLE #load_products (
                code               nvarchar(32)  NOT NULL PRIMARY KEY,
                name               nvarchar(200) NOT NULL,
                price              decimal(18,2) NOT NULL,
                available_quantity int           NOT NULL);
            """;

        // Guarded per code; the staging table is emptied for the next batch.
        public const string InsertMissing =
            """
            INSERT INTO products (code, name, price, available_quantity)
            SELECT s.code, s.name, s.price, s.available_quantity
              FROM #load_products s
             WHERE NOT EXISTS (SELECT 1 FROM products p WHERE p.code = s.code);
            TRUNCATE TABLE #load_products;
            """;

        private static readonly string[] Nouns = ["Widget", "Gadget", "Bracket", "Sprocket", "Gasket", "Flange", "Label", "Tape", "Roll", "Bolt"];
        private static readonly string[] Colours = ["Blue", "Red", "Green", "Black", "White", "Amber", "Grey", "Teal"];

        /// <summary>Deterministic: the same index always yields the same row, so re-runs agree with the first.</summary>
        public static DataTable Rows(int first, int last)
        {
            var table = new DataTable();
            table.Columns.Add("code", typeof(string));
            table.Columns.Add("name", typeof(string));
            table.Columns.Add("price", typeof(decimal));
            table.Columns.Add("available_quantity", typeof(int));

            for (var i = first; i <= last; i++)
            {
                // 80 names over N rows (ties + shared prefixes); prices 0.99 … 100.98 in a non-monotonic spread.
                var name = $"{Nouns[i % Nouns.Length]} {Colours[i / Nouns.Length % Colours.Length]}";
                var price = 0.99m + (i * 37 % 10_000) / 100m;
                table.Rows.Add($"LOAD-{i:D6}", name, price, Quantity);
            }

            return table;
        }
    }
}
