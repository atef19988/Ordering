namespace Ordering.Application.Abstractions.Persistence;

/// <summary>
/// A lock request exceeded the transaction's <c>LOCK_TIMEOUT</c>. <see cref="Resource"/> is
/// named by the Infrastructure call site (<c>"idempotency_keys"</c>, <c>"products"</c>), so the
/// handler can tell "a duplicate is still in flight" from "the product row is busy".
/// </summary>
public sealed class LockTimeoutException(string resource, Exception innerException)
    : Exception($"Timed out waiting for a lock on '{resource}'.", innerException)
{
    public string Resource { get; } = resource;
}
