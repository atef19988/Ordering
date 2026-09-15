using Dapper;
using Ordering.Application.Features.Orders;
using Ordering.Application.Features.Orders.GetOrderById;
using Ordering.Infrastructure.Persistence;

namespace Ordering.Infrastructure.Read;

internal sealed class OrderQueryRepository(IDbConnectionFactory connectionFactory)
    : BaseDapperRepository(connectionFactory), IOrderQueryRepository
{
    // Two result sets, one batch: header + notification state, then lines. Aliases = record parameters, in order.
    private const string GetByIdSql =
        """
        SELECT o.id                          AS Id,
               o.customer_reference          AS CustomerReference,
               o.status                      AS Status,
               o.total                       AS Total,
               o.created_at                  AS CreatedAt,
               o.cancelled_at                AS CancelledAt,
               COALESCE(ob.status, 'None')   AS OutboxStatus,
               COALESCE(ob.attempt_count, 0) AS NotificationAttempts
          FROM orders AS o
          LEFT JOIN outbox_messages AS ob
                 ON ob.aggregate_id = o.id AND ob.type = 'order.created'
         WHERE o.id = @id;

        SELECT product_code AS ProductCode,
               quantity     AS Quantity,
               unit_price   AS UnitPrice,
               line_total   AS LineTotal
          FROM order_lines
         WHERE order_id = @id
         ORDER BY product_code;
        """;

    public Task<OrderDetailDto?> GetByIdAsync(long id, CancellationToken cancellationToken) =>
        QueryMultipleAsync(GetByIdSql, new { id }, async grid =>
        {
            var header = await grid.ReadSingleOrDefaultAsync<OrderRow>();

            if (header is null)
            {
                return null;
            }

            var lines = (await grid.ReadAsync<OrderLineDto>()).AsList();
            return header.ToDto(lines);
        }, cancellationToken);

    private sealed record OrderRow(
        long Id,
        string CustomerReference,
        string Status,
        decimal Total,
        DateTimeOffset CreatedAt,
        DateTimeOffset? CancelledAt,
        string OutboxStatus,
        int NotificationAttempts)
    {
        public OrderDetailDto ToDto(IReadOnlyList<OrderLineDto> lines) => new(
            Id,
            CustomerReference,
            Status,
            Total,
            CreatedAt,
            CancelledAt,
            NotificationStatus.FromOutbox(OutboxStatus),
            NotificationAttempts,
            lines);
    }
}
