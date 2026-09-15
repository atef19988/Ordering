using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Ordering.Application.Features.Orders;
using Ordering.Application.Features.Orders.GetOrderById;
using Ordering.Domain.Orders;
using Ordering.Infrastructure.Read;

namespace Ordering.Infrastructure.Persistence;

internal sealed class OrderRepository(OrderingDbContext context) : IOrderRepository
{
    /// <summary>Lines ride along through the <c>Order.Lines</c> navigation; nothing hits the database until <c>SaveChanges</c>.</summary>
    public void Add(Order order) => context.Orders.Add(order);

    /// <summary>
    /// Raw, parameterized T-SQL on the context's connection, so it runs inside the transaction
    /// <c>TransactionBehavior</c> opened. The UPDATE takes an exclusive lock on the order row and
    /// re-evaluates <c>status = 'Confirmed'</c> against the current row, which is what makes N
    /// concurrent cancels collapse into one. <c>row_version</c> advances on its own.
    /// </summary>
    public async Task<int> TryCancelAsync(long id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            return await context.Database.ExecuteSqlAsync(
                $"""
                UPDATE orders
                   SET status = {nameof(OrderStatus.Cancelled)},
                       cancelled_at = {now}
                 WHERE id = {id}
                   AND status = {nameof(OrderStatus.Confirmed)};
                """,
                cancellationToken);
        }
        catch (Exception exception) when (SqlErrors.TryTranslate(exception, IOrderRepository.LockedResource, out var translated))
        {
            throw translated;
        }
    }

    /// <summary>
    /// The Task 3 projection, run through Dapper on the EF connection and transaction: same SQL
    /// as <c>GET /api/orders/{id}</c>, but it sees this transaction's own uncommitted rows instead
    /// of blocking on them from a second connection.
    /// </summary>
    public async Task<OrderDetailDto> ReadBackAsync(long id, CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        var transaction = context.Database.CurrentTransaction?.GetDbTransaction();

        return await OrderQueryRepository.GetByIdAsync(connection, transaction, id, cancellationToken)
            ?? throw new InvalidOperationException($"Order {id} was just updated in this transaction but cannot be read back.");
    }
}
