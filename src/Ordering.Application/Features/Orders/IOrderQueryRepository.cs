using Ordering.Application.Features.Orders.GetOrderById;

namespace Ordering.Application.Features.Orders;

/// <summary>Read side (Dapper, hand-written SQL). Never opens a transaction, never loads entities.</summary>
public interface IOrderQueryRepository
{
    /// <summary>Header, lines (by product code) and notification state in one round trip; <c>null</c> if unknown.</summary>
    Task<OrderDetailDto?> GetByIdAsync(long id, CancellationToken cancellationToken);
}
