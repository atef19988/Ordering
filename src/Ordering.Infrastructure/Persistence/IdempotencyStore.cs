using Ordering.Application.Idempotency;
using Ordering.Infrastructure.Read;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// <c>Add</c> stages the row in the command's unit of work; <c>FindAsync</c> is a Dapper read on a
/// connection of its own, because the handler asks after it has rolled back.
/// </summary>
internal sealed class IdempotencyStore(OrderingDbContext context, IDbConnectionFactory connectionFactory)
    : BaseDapperRepository(connectionFactory), IIdempotencyStore
{
    private const string FindSql =
        """
        SELECT idempotency_key AS [Key],
               request_hash    AS RequestHash,
               order_id        AS OrderId,
               created_at      AS CreatedAt
          FROM idempotency_keys
         WHERE idempotency_key = @key;
        """;

    public void Add(IdempotencyKey key) => context.IdempotencyKeys.Add(key);

    public Task<IdempotencyKey?> FindAsync(string key, CancellationToken cancellationToken) =>
        QuerySingleOrDefaultAsync<IdempotencyKey>(FindSql, new { key }, cancellationToken);
}
