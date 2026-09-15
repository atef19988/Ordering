using Ordering.Application.Features.Orders.GetOrderById;
using Ordering.Domain.Orders;

namespace Ordering.Application.Features.Orders;

/// <summary>Write side (EF Core), inside the command's transaction.</summary>
public interface IOrderRepository
{
    /// <summary>The <c>LockTimeoutException.Resource</c> an order statement reports when the order row stays locked.</summary>
    const string LockedResource = "orders";

    /// <summary>Stages the order and its lines for the unit of work's next <c>SaveChanges</c>.</summary>
    void Add(Order order);

    /// <summary>
    /// The guarded transition — <c>UPDATE orders SET status = 'Cancelled', cancelled_at = @now WHERE id = @id AND status = 'Confirmed'</c>.
    /// Returns rows affected: <c>1</c> this call cancelled the order, <c>0</c> it was not
    /// <c>Confirmed</c> (already cancelled, or unknown). The status column is the lock: of N racing
    /// cancels exactly one sees <c>1</c>. Throws <c>LockTimeoutException</c> (<see cref="LockedResource"/>)
    /// when another writer held the order row past the lock timeout.
    /// </summary>
    Task<int> TryCancelAsync(long id, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// The order as <c>GET /api/orders/{id}</c> will report it once this transaction commits, read
    /// on the transaction's <em>own</em> connection so it sees the rows this transaction has written
    /// (a read on a second connection would block on them). Lines come in product-code order — the
    /// lock order every stock statement follows. Throws when the order does not exist.
    /// </summary>
    Task<OrderDetailDto> ReadBackAsync(long id, CancellationToken cancellationToken);
}
