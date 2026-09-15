namespace Ordering.Application.Idempotency;

/// <summary>
/// One row of <c>idempotency_keys</c>: the client's key, a fingerprint of what it submitted and
/// the order it produced. Written in the same <c>SaveChanges</c> as the order, so it exists if and
/// only if the order committed — there is no in-progress state to clean up.
/// </summary>
public sealed class IdempotencyKey(string key, string requestHash, long orderId, DateTimeOffset createdAt)
{
    public const int KeyMaxLength = 128;
    public const int RequestHashLength = 64;

    /// <summary>Names Infrastructure uses for the table and its primary key; the handler matches typed exceptions on them.</summary>
    public const string TableName = "idempotency_keys";
    public const string PrimaryKeyConstraint = "pk_idempotency_keys";

    /// <summary>The <c>Idempotency-Key</c> header as received (after validation).</summary>
    public string Key { get; } = key;

    /// <summary>SHA-256 hex from <see cref="RequestFingerprint.Compute"/>.</summary>
    public string RequestHash { get; } = requestHash;

    public long OrderId { get; } = orderId;

    public DateTimeOffset CreatedAt { get; } = createdAt;
}
