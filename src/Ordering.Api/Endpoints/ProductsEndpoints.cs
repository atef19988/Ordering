using Ordering.Application.Abstractions.Messaging;
using Ordering.Application.Abstractions.Paging;
using Ordering.Application.Features.Products.GetProducts;

namespace Ordering.Api.Endpoints;

public static class ProductsEndpoints
{
    public static RouteGroupBuilder MapProducts(this RouteGroupBuilder api)
    {
        var products = api.MapGroup("/products").WithTags("Products");

        // Task 13: output-cache policy `catalogue` — must vary by all five query keys:
        // p.Expire(TimeSpan.FromSeconds(1)).Tag("catalogue").SetVaryByQuery("search", "inStock", "sort", "pageSize", "cursor")
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
            .WithName("GetProducts")
            .WithSummary("One keyset page of the catalogue; never the whole table.")
            .Produces<Page<ProductDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return api;
    }
}
