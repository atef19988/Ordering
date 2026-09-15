using Ordering.Application.Abstractions;
using Ordering.Application.Abstractions.Messaging;

namespace Ordering.Application.Features.Products.GetProducts;

public sealed record ProductDto(string Code, string Name, decimal Price, int AvailableQuantity);

/// <summary>The whole catalogue, ordered by code.</summary>
public sealed record GetProductsQuery : IQuery<IReadOnlyList<ProductDto>>;

internal sealed class GetProductsQueryHandler(IProductQueryRepository products)
    : IQueryHandler<GetProductsQuery, IReadOnlyList<ProductDto>>
{
    public async Task<Result<IReadOnlyList<ProductDto>>> Handle(GetProductsQuery query, CancellationToken cancellationToken) =>
        Result.Success(await products.GetAllAsync(cancellationToken));
}
