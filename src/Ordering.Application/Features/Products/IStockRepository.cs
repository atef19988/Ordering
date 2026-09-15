namespace Ordering.Application.Features.Products;

/// <summary>
/// Stock changes are conditional <c>UPDATE</c>s executed inside the command's transaction — the
/// database, not the process, decides who gets the last unit.
/// </summary>
public interface IStockRepository
{
    /// <summary>The <c>LockTimeoutException.Resource</c> a stock statement reports when the product row stays locked.</summary>
    const string LockedResource = "products";

    /// <summary>
    /// <c>UPDATE products SET available_quantity -= @quantity WHERE code = @code AND available_quantity >= @quantity</c>.
    /// Returns rows affected: <c>1</c> deducted, <c>0</c> insufficient stock (nothing changed).
    /// Throws <c>LockTimeoutException</c> (<see cref="LockedResource"/>) when another writer held the row past the lock timeout.
    /// </summary>
    Task<int> TryDeductAsync(string productCode, int quantity, CancellationToken cancellationToken);

    /// <summary>The committed quantity, read after a failed deduction to tell the caller what is left.</summary>
    Task<int> GetAvailableQuantityAsync(string productCode, CancellationToken cancellationToken);

    // Task 6: Task<int> RestoreAsync(string productCode, int quantity, CancellationToken cancellationToken);
}
