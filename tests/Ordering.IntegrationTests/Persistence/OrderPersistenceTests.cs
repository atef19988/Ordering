using Ordering.IntegrationTests.Backend;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Ordering.Domain.Orders;
using Ordering.Infrastructure.Common;
using Ordering.Infrastructure.Persistence;

namespace Ordering.IntegrationTests.Persistence;

/// <summary>
/// Proves the EF mapping of the domain types: private constructors, the <c>_lines</c> backing field,
/// enum-as-text status, exact decimals, <c>datetimeoffset</c> and the <c>rowversion</c> token.
/// </summary>
[Collection(BackendCollection.Name)]
public class OrderPersistenceTests(BackendFixture backend)
{
    private static readonly SnowflakeIdGenerator Ids = new(new SystemClock(), workerId: 3);
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 16, 12, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public async Task Order_with_lines_round_trips_through_the_write_model()
    {
        var order = await InsertOrderAsync();

        try
        {
            await using var context = backend.Sql.CreateContext();
            var loaded = await context.Orders.Include(o => o.Lines).SingleAsync(o => o.Id == order.Id);

            Assert.Equal("CUST-42", loaded.CustomerReference);
            Assert.Equal(OrderStatus.Confirmed, loaded.Status);
            Assert.Equal(37.00m, loaded.Total);
            Assert.Equal(CreatedAt, loaded.CreatedAt);
            Assert.Equal(CreatedAt.Offset, loaded.CreatedAt.Offset);
            Assert.Null(loaded.CancelledAt);
            Assert.Equal(
                order.Lines.Select(Shape).OrderBy(l => l.Id),
                loaded.Lines.Select(Shape).OrderBy(l => l.Id));
            Assert.NotNull(context.Entry(loaded).Property<byte[]>("RowVersion").CurrentValue);

            await using var connection = await backend.Sql.OpenConnectionAsync();
            Assert.Equal("Confirmed", await connection.ExecuteScalarAsync<string>("SELECT status FROM orders WHERE id = @id", new { id = order.Id }));
        }
        finally
        {
            await DeleteOrderAsync(order.Id);
        }
    }

    [Fact]
    public async Task Cancel_persists_status_and_cancelled_at()
    {
        var order = await InsertOrderAsync();
        var cancelledAt = CreatedAt.AddMinutes(5);

        try
        {
            await using (var context = backend.Sql.CreateContext())
            {
                var loaded = await context.Orders.SingleAsync(o => o.Id == order.Id);
                loaded.Cancel(cancelledAt);
                await context.SaveChangesAsync();
            }

            await using var connection = await backend.Sql.OpenConnectionAsync();
            var row = await connection.QuerySingleAsync<(string Status, DateTimeOffset? CancelledAt)>(
                "SELECT status, cancelled_at FROM orders WHERE id = @id", new { id = order.Id });
            Assert.Equal(("Cancelled", cancelledAt), row);
        }
        finally
        {
            await DeleteOrderAsync(order.Id);
        }
    }

    [Fact]
    public async Task Row_version_detects_a_concurrent_update()
    {
        var order = await InsertOrderAsync();

        try
        {
            await using var first = backend.Sql.CreateContext();
            await using var second = backend.Sql.CreateContext();
            var firstCopy = await first.Orders.SingleAsync(o => o.Id == order.Id);
            var secondCopy = await second.Orders.SingleAsync(o => o.Id == order.Id);

            firstCopy.Cancel(CreatedAt.AddMinutes(1));
            await first.SaveChangesAsync();

            secondCopy.Cancel(CreatedAt.AddMinutes(2));
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        }
        finally
        {
            await DeleteOrderAsync(order.Id);
        }
    }

    private async Task<Order> InsertOrderAsync()
    {
        await using var context = backend.Sql.CreateContext();
        await DbInitializer.SeedAsync(context, CancellationToken.None);

        var orderId = Ids.NewId();
        var order = Order.Create(
            orderId,
            "CUST-42",
            [
                OrderLine.Create(Ids.NewId(), orderId, "SKU-002", quantity: 3, unitPrice: 4.00m),
                OrderLine.Create(Ids.NewId(), orderId, "SKU-001", quantity: 2, unitPrice: 12.50m),
            ],
            CreatedAt);

        context.Orders.Add(order);
        await context.SaveChangesAsync();
        return order;
    }

    private async Task DeleteOrderAsync(long id)
    {
        await using var connection = await backend.Sql.OpenConnectionAsync();
        await connection.ExecuteAsync("DELETE FROM orders WHERE id = @id", new { id }); // lines cascade
    }

    private static (long Id, string ProductCode, int Quantity, decimal UnitPrice, decimal LineTotal) Shape(OrderLine line) =>
        (line.Id, line.ProductCode, line.Quantity, line.UnitPrice, line.LineTotal);
}
