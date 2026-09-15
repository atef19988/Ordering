using Ordering.Application.Abstractions.Messaging;
using Ordering.Application.Abstractions.Outbox;
using Ordering.Infrastructure.Persistence;
using Ordering.Infrastructure.Read;

namespace Ordering.Infrastructure.Outbox;

/// <summary>
/// Outbox bookkeeping: claim, publish mark, attempt count, verdicts, reaper. Every method is one
/// parameterised, autocommitted T-SQL statement on its own connection — no transaction, no EF
/// entity, no CQRS pipeline; the guards in the <c>WHERE</c> clauses are the whole concurrency
/// story. <c>@now</c> always comes from <c>IClock</c> so tests can steer time.
/// </summary>
public sealed class OutboxStore(IDbConnectionFactory connectionFactory) : BaseDapperRepository(connectionFactory)
{
    private const string Pending = nameof(OutboxMessageStatus.Pending);
    private const string Processing = nameof(OutboxMessageStatus.Processing);
    private const string Sent = nameof(OutboxMessageStatus.Sent);
    private const string Failed = nameof(OutboxMessageStatus.Failed);

    /// <summary>
    /// Atomically moves up to <paramref name="batchSize"/> due <c>Pending</c> rows to <c>Processing</c>
    /// under a lease. <c>UPDLOCK, READPAST</c> skips rows another relay is claiming instead of
    /// waiting on them, which is what lets N relays share one table. <c>attempt_count</c> is not
    /// touched: it counts deliveries, and the consumer owns it.
    /// </summary>
    public Task<IReadOnlyList<IntegrationEvent>> ClaimAsync(string worker, int batchSize, TimeSpan lease, DateTimeOffset now, CancellationToken cancellationToken) =>
        QueryAsync<IntegrationEvent>(
            $"""
            UPDATE m
               SET status = '{Processing}',
                   claimed_by = @worker,
                   claimed_until = @claimedUntil
            OUTPUT inserted.id           AS Id,
                   inserted.type         AS Type,
                   inserted.aggregate_id AS AggregateId,
                   inserted.payload      AS Payload
              FROM outbox_messages AS m WITH (UPDLOCK, READPAST, ROWLOCK)
             WHERE m.id IN (SELECT TOP (@batchSize) id
                              FROM outbox_messages WITH (UPDLOCK, READPAST, ROWLOCK)
                             WHERE status = '{Pending}'
                               AND next_attempt_at <= @now
                             ORDER BY occurred_at);
            """,
            new { worker, batchSize, now, claimedUntil = now + lease },
            cancellationToken);

    /// <summary>
    /// The broker confirmed these messages: the lease is over, the rows now wait for a consumer's
    /// verdict. Not guarded by status on purpose — a fast consumer can write <c>Sent</c> before
    /// this runs, and the row was published either way.
    /// </summary>
    public Task<int> MarkPublishedAsync(IReadOnlyList<long> ids, DateTimeOffset now, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? Task.FromResult(0)
            : ExecuteAsync(
                """
                UPDATE outbox_messages
                   SET published_at = @now, claimed_by = NULL, claimed_until = NULL
                 WHERE id IN @ids AND published_at IS NULL;
                """,
                new { ids, now },
                cancellationToken);

    /// <summary>Publishing failed: hand the rows back so any relay can claim them again.</summary>
    public Task<int> ReleaseAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken) =>
        ids.Count == 0
            ? Task.FromResult(0)
            : ExecuteAsync(
                $"""
                UPDATE outbox_messages
                   SET status = '{Pending}', claimed_by = NULL, claimed_until = NULL
                 WHERE id IN @ids AND status = '{Processing}' AND published_at IS NULL;
                """,
                new { ids },
                cancellationToken);

    /// <summary>
    /// Returns to <c>Pending</c> every claim whose relay died before publishing, and every
    /// published row that got no verdict within <paramref name="publishedTimeout"/>. Re-publishing
    /// is how a lost message or a dead consumer is recovered; the consumer's guards absorb the duplicate.
    /// </summary>
    public Task<int> ReapAsync(DateTimeOffset now, TimeSpan publishedTimeout, CancellationToken cancellationToken) =>
        ExecuteAsync(
            $"""
            UPDATE outbox_messages
               SET status = '{Pending}', claimed_by = NULL, claimed_until = NULL, published_at = NULL
             WHERE status = '{Processing}'
               AND ( (published_at IS NULL AND claimed_until < @now)
                  OR (published_at IS NOT NULL AND published_at < @publishedBefore) );
            """,
            new { now, publishedBefore = now - publishedTimeout },
            cancellationToken);

    /// <summary>
    /// Counts a delivery attempt and, in the same statement, checks the row is still live. This is
    /// the consumer's dedupe: <c>null</c> means the row is already <c>Sent</c>/<c>Failed</c>, so
    /// the delivery is a duplicate and must be acknowledged without delivering.
    /// </summary>
    public Task<int?> CountAttemptAsync(long id, CancellationToken cancellationToken) =>
        QuerySingleOrDefaultAsync<int?>(
            $"""
            UPDATE outbox_messages
               SET attempt_count = attempt_count + 1
            OUTPUT inserted.attempt_count
             WHERE id = @id AND status = '{Processing}';
            """,
            new { id },
            cancellationToken);

    public Task<int> MarkSentAsync(long id, DateTimeOffset now, CancellationToken cancellationToken) =>
        ExecuteAsync(
            $"""
            UPDATE outbox_messages
               SET status = '{Sent}', processed_at = @now, last_error = NULL, claimed_by = NULL, claimed_until = NULL
             WHERE id = @id AND status = '{Processing}';
            """,
            new { id, now },
            cancellationToken);

    /// <summary>
    /// Records the failure and the tier's due time. <c>published_at</c> is refreshed because the
    /// message is about to be re-published to the tier, which restarts the reaper's verdict timeout.
    /// </summary>
    public Task<int> ScheduleRetryAsync(long id, DateTimeOffset nextAttemptAt, DateTimeOffset now, string error, CancellationToken cancellationToken) =>
        ExecuteAsync(
            $"""
            UPDATE outbox_messages
               SET next_attempt_at = @nextAttemptAt, published_at = @now, last_error = @error
             WHERE id = @id AND status = '{Processing}';
            """,
            new { id, nextAttemptAt, now, error = Truncate(error) },
            cancellationToken);

    public Task<int> MarkFailedAsync(long id, DateTimeOffset now, string error, CancellationToken cancellationToken) =>
        ExecuteAsync(
            $"""
            UPDATE outbox_messages
               SET status = '{Failed}', processed_at = @now, last_error = @error, claimed_by = NULL, claimed_until = NULL
             WHERE id = @id AND status = '{Processing}';
            """,
            new { id, now, error = Truncate(error) },
            cancellationToken);

    private static string Truncate(string error) =>
        error.Length <= OutboxMessage.LastErrorMaxLength ? error : error[..OutboxMessage.LastErrorMaxLength];
}
