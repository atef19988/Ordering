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

                // The one branch this endpoint has, and it is on a result flag: a replay of a known
                // key answers 200 OK with the stored order (in its current state) instead of 201.
                return result.ToHttpResult(response => response.Replayed
                    ? TypedResults.Ok(response.Order)
                    : TypedResults.Created($"/api/orders/{response.Order.Id}", response.Order));
            })
            .WithName("CreateOrder")
            .Produces<OrderDetailDto>(StatusCodes.Status201Created)
            .Produces<OrderDetailDto>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Task 6: POST /{id:long}/cancel

        return api;
    }
}
