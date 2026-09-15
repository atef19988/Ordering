using Ordering.Application.Features.Orders;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Persistence;

internal sealed class OrderRepository(OrderingDbContext context) : IOrderRepository
{
    /// <summary>Lines ride along through the <c>Order.Lines</c> navigation; nothing hits the database until <c>SaveChanges</c>.</summary>
    public void Add(Order order) => context.Orders.Add(order);
}
