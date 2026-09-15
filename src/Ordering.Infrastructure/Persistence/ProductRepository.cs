using Microsoft.EntityFrameworkCore;
using Ordering.Application.Features.Products;
using Ordering.Domain.Products;

namespace Ordering.Infrastructure.Persistence;

internal sealed class ProductRepository(OrderingDbContext context) : IProductRepository
{
    /// <summary>
    /// Untracked: the handler only reads code and price, and stock is changed by
    /// <see cref="StockRepository"/> in SQL, so there is nothing for the change tracker to do.
    /// <c>ORDER BY code</c> is the shared lock order (see <see cref="IProductRepository"/>).
    /// </summary>
    public async Task<IReadOnlyList<Product>> GetByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        await context.Products
            .AsNoTracking()
            .Where(p => codes.Contains(p.Code))
            .OrderBy(p => p.Code)
            .ToListAsync(cancellationToken);
}
