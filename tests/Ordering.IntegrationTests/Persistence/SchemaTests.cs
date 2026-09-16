using Ordering.IntegrationTests.Backend;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ordering.IntegrationTests.Persistence;

[Collection(BackendCollection.Name)]
public class SchemaTests(BackendFixture backend)
{
    private const int CheckConstraintViolation = 547;

    [Fact]
    public async Task Initial_migration_is_applied_and_nothing_is_pending()
    {
        await using var context = backend.Sql.CreateContext();

        var applied = await context.Database.GetAppliedMigrationsAsync();
        var pending = await context.Database.GetPendingMigrationsAsync();

        Assert.Contains(applied, m => m.EndsWith("_Initial", StringComparison.Ordinal));
        Assert.Empty(pending);
    }

    [Fact]
    public async Task Stock_cannot_go_negative_even_with_a_direct_update()
    {
        await using var connection = await backend.Sql.OpenConnectionAsync();
        const string code = "SCHEMA-NEG";
        await connection.ExecuteAsync("INSERT INTO products (code, name, price, available_quantity) VALUES (@code, 'probe', 1.00, 1)", new { code });

        try
        {
            var exception = await Assert.ThrowsAsync<SqlException>(() =>
                connection.ExecuteAsync("UPDATE products SET available_quantity = -1 WHERE code = @code", new { code }));

            Assert.Equal(CheckConstraintViolation, exception.Number);
            Assert.Contains("ck_products_qty_non_negative", exception.Message);
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT available_quantity FROM products WHERE code = @code", new { code }));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM products WHERE code = @code", new { code });
        }
    }

    [Fact]
    public async Task Order_line_quantity_must_be_positive_in_the_database()
    {
        await using var connection = await backend.Sql.OpenConnectionAsync();
        const string code = "SCHEMA-QTY";
        await connection.ExecuteAsync("INSERT INTO products (code, name, price, available_quantity) VALUES (@code, 'probe', 1.00, 1)", new { code });
        await connection.ExecuteAsync(
            "INSERT INTO orders (id, customer_reference, status, total, created_at) VALUES (1, 'probe', 'Confirmed', 0, SYSDATETIMEOFFSET())");

        try
        {
            var exception = await Assert.ThrowsAsync<SqlException>(() => connection.ExecuteAsync(
                "INSERT INTO order_lines (id, order_id, product_code, quantity, unit_price, line_total) VALUES (1, 1, @code, 0, 1.00, 0.00)",
                new { code }));

            Assert.Equal(CheckConstraintViolation, exception.Number);
            Assert.Contains("ck_order_lines_qty_positive", exception.Message);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM orders WHERE id = 1; DELETE FROM products WHERE code = @code", new { code });
        }
    }

    [Fact]
    public async Task Money_columns_are_decimal_18_2()
    {
        await using var connection = await backend.Sql.OpenConnectionAsync();

        var columns = (await connection.QueryAsync<(string Table, string Column, string Type, byte Precision, byte Scale)>(
            """
            SELECT t.name, c.name, ty.name, c.precision, c.scale
              FROM sys.columns AS c
              JOIN sys.tables AS t ON t.object_id = c.object_id
              JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
             WHERE c.name IN ('price', 'total', 'unit_price', 'line_total')
            """)).ToList();

        Assert.Equal(4, columns.Count);
        Assert.All(columns, c => Assert.Equal(("decimal", (byte)18, (byte)2), (c.Type, c.Precision, c.Scale)));
    }
}
