using Ordering.Application.Features.Products.GetProducts;

namespace Ordering.Application.Features.Products;

/// <summary>Read side (Dapper, hand-written SQL). Never opens a transaction, never loads entities.</summary>
public interface IProductQueryRepository
{
    Task<IReadOnlyList<ProductDto>> GetAllAsync(CancellationToken cancellationToken);
}
