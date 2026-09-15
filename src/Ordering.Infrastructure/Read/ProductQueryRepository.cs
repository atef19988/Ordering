using Ordering.Application.Features.Products;
using Ordering.Application.Features.Products.GetProducts;
using Ordering.Infrastructure.Persistence;

namespace Ordering.Infrastructure.Read;

internal sealed class ProductQueryRepository(IDbConnectionFactory connectionFactory)
    : BaseDapperRepository(connectionFactory), IProductQueryRepository
{
    // Aliases = ProductDto constructor parameters, in order.
    private const string GetAllSql =
        """
        SELECT code               AS Code,
               name               AS Name,
               price              AS Price,
               available_quantity AS AvailableQuantity
          FROM products
         ORDER BY code;
        """;

    public Task<IReadOnlyList<ProductDto>> GetAllAsync(CancellationToken cancellationToken) =>
        QueryAsync<ProductDto>(GetAllSql, cancellationToken: cancellationToken);
}
