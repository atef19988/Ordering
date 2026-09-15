using Ordering.Application.Abstractions.Messaging;
using Ordering.Application.Features.Products.GetProducts;

namespace Ordering.Api.Endpoints;

public static class ProductsEndpoints
{
    public static RouteGroupBuilder MapProducts(this RouteGroupBuilder api)
    {
        var products = api.MapGroup("/products").WithTags("Products");

        products.MapGet("/", async (IDispatcher dispatcher, CancellationToken cancellationToken) =>
                (await dispatcher.Query(new GetProductsQuery(), cancellationToken)).ToHttpResult())
            .WithName("GetProducts")
            .Produces<IReadOnlyList<ProductDto>>();

        return api;
    }
}
