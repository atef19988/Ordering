using Ordering.Application.Abstractions.Messaging;
using Ordering.Application.Abstractions.Paging;
using Ordering.Application.Features.Products.GetProducts;

namespace Ordering.Api.Endpoints;

public static class ProductsEndpoints
{
    /// <summary>Name of the output-cache policy <c>Program</c> defines for the catalogue.</summary>
    public const string CataloguePolicy = "catalogue";

    public static RouteGroupBuilder MapProducts(this RouteGroupBuilder api)
    {
        var products = api.MapGroup("/products").WithTags("Products");

        // Served from the shared Redis output cache (policy in Program: 1 s, tag `catalogue`,
        // varies by every paging key); create and cancel evict the tag after commit.
        products.MapGet("/", async (
                string? search,
                bool? inStock,
                string? sort,
                int? pageSize,
                string? cursor,
                IDispatcher dispatcher,
                CancellationToken cancellationToken) =>
                (await dispatcher.Query(
                    new GetProductsQuery(
                        search,
                        inStock ?? false,
                        sort ?? GetProductsQuery.DefaultSort,
                        pageSize ?? Page<ProductDto>.DefaultPageSize,
                        cursor),
                    cancellationToken)).ToHttpResult())
            .CacheOutput(CataloguePolicy)
            .WithName("GetProducts")
            .WithSummary("One keyset page of the catalogue; never the whole table.")
            .Produces<Page<ProductDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return api;
    }
}
