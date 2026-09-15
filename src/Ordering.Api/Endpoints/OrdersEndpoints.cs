using Microsoft.AspNetCore.Mvc;
using Ordering.Application.Abstractions.Messaging;
using Ordering.Application.Features.Orders.CreateOrder;
using Ordering.Application.Features.Orders.GetOrderById;

namespace Ordering.Api.Endpoints;

public static class OrdersEndpoints
{
    public static RouteGroupBuilder MapOrders(this RouteGroupBuilder api)
    {
        var orders = api.MapGroup("/orders").WithTags("Orders");

        orders.MapGet("/{id:long}", async (long id, IDispatcher dispatcher, CancellationToken cancellationToken) =>
                (await dispatcher.Query(new GetOrderByIdQuery(id), cancellationToken)).ToHttpResult())
            .WithName("GetOrderById")
            .Produces<OrderDetailDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        orders.MapPost("/", async (
                [FromHeader(Name = ApiHeaders.IdempotencyKey)] string? idempotencyKey,
                CreateOrderRequest request,
                IDispatcher dispatcher,
                CancellationToken cancellationToken) =>
            {
                var result = await dispatcher.Send(request.ToCommand(idempotencyKey), cancellationToken);

                // Task 5: a replay of a known key answers 200 OK with the stored order instead of 201.
                return result.ToHttpResult(order => TypedResults.Created($"/api/orders/{order.Id}", order));
            })
            .WithName("CreateOrder")
            .Produces<OrderDetailDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Task 6: POST /{id:long}/cancel

        return api;
    }
}
