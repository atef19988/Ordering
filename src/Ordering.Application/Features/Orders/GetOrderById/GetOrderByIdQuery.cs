using Ordering.Application.Abstractions;
using Ordering.Application.Abstractions.Messaging;
using Ordering.Application.Abstractions.Outbox;
using Ordering.Domain.Orders;

namespace Ordering.Application.Features.Orders.GetOrderById;

public sealed record OrderLineDto(string ProductCode, int Quantity, decimal UnitPrice, decimal LineTotal);

/// <param name="Id">Snowflake; a JSON string on the wire.</param>
/// <param name="NotificationStatus">One of <see cref="GetOrderById.NotificationStatus"/>.</param>
public sealed record OrderDetailDto(
    long Id,
    string CustomerReference,
    string Status,
    decimal Total,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CancelledAt,
    string NotificationStatus,
    int NotificationAttempts,
    IReadOnlyList<OrderLineDto> Lines);

/// <summary>
/// The API vocabulary for the <c>order.created</c> notification, derived from the outbox row in
/// exactly one place. <c>Processing</c> is an internal claim state, so it reads as <c>Pending</c>.
/// </summary>
public static class NotificationStatus
{
    public const string Pending = "Pending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";

    /// <summary>No outbox row exists for the order (only reachable through hand-inserted data).</summary>
    public const string None = "None";

    public static string FromOutbox(string? outboxStatus) => outboxStatus switch
    {
        nameof(OutboxMessageStatus.Pending) or nameof(OutboxMessageStatus.Processing) => Pending,
        nameof(OutboxMessageStatus.Sent) => Sent,
        nameof(OutboxMessageStatus.Failed) => Failed,
        _ => None,
    };
}

public sealed record GetOrderByIdQuery(long Id) : IQuery<OrderDetailDto>;

internal sealed class GetOrderByIdQueryHandler(IOrderQueryRepository orders) : IQueryHandler<GetOrderByIdQuery, OrderDetailDto>
{
    public async Task<Result<OrderDetailDto>> Handle(GetOrderByIdQuery query, CancellationToken cancellationToken) =>
        await orders.GetByIdAsync(query.Id, cancellationToken) is { } order
            ? Result.Success(order)
            : Result.Failure<OrderDetailDto>(OrderErrors.NotFound(query.Id));
}
