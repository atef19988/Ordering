using Ordering.Domain.Orders;

namespace Ordering.Application.Features.Orders;

/// <summary>Write side (EF Core), inside the command's transaction.</summary>
public interface IOrderRepository
{
    /// <summary>Stages the order and its lines for the unit of work's next <c>SaveChanges</c>.</summary>
    void Add(Order order);

    // Task 6: the guarded transition — UPDATE orders SET status = 'Cancelled' ... WHERE id = @id AND status = 'Confirmed'.
}
