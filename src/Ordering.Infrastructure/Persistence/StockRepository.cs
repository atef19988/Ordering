using Microsoft.EntityFrameworkCore;
using Ordering.Application.Features.Products;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// Raw, parameterized T-SQL on the context's connection, so every statement runs inside the
/// transaction <c>TransactionBehavior</c> opened. No entity is loaded and no lock is taken in code.
/// </summary>
internal sealed class StockRepository(OrderingDbContext context) : IStockRepository
{
    /// <summary>
    /// The UPDATE takes an exclusive row lock; a racing writer blocks until we commit or roll back,
    /// then re-evaluates the WHERE against the new value. <c>ck_products_qty_non_negative</c> is
    /// the backstop should this guard ever be bypassed. A wait longer than the transaction's
    /// <c>LOCK_TIMEOUT</c> is reported as a lock timeout on <see cref="IStockRepository.LockedResource"/>.
    /// </summary>
    public async Task<int> TryDeductAsync(string productCode, int quantity, CancellationToken cancellationToken)
    {
        try
        {
            return await context.Database.ExecuteSqlAsync(
                $"""
                UPDATE products
                   SET available_quantity = available_quantity - {quantity}
                 WHERE code = {productCode}
                   AND available_quantity >= {quantity};
                """,
                cancellationToken);
        }
        catch (Exception exception) when (SqlErrors.TryTranslate(exception, IStockRepository.LockedResource, out var translated))
        {
            throw translated;
        }
    }

    public Task<int> GetAvailableQuantityAsync(string productCode, CancellationToken cancellationToken) =>
        context.Database
            .SqlQuery<int>($"SELECT available_quantity AS [Value] FROM products WHERE code = {productCode}")
            .SingleAsync(cancellationToken);
}
