namespace Ordering.Application.Abstractions.Caching;

/// <summary>
/// The one cache seam <c>Application</c> sees (Task 13). Implemented over the Redis-backed output
/// cache in <c>Infrastructure/Redis</c>: every catalogue page is tagged <c>catalogue</c>, and
/// this evicts the tag. Registered through <see cref="IUnitOfWork.OnCommitted"/> by the handlers
/// that change stock, never called inside a transaction. Best-effort: the entries expire on
/// their own after one second, so a lost eviction costs at most one second of staleness.
/// </summary>
public interface ICatalogueCache
{
    Task InvalidateAsync(CancellationToken cancellationToken);
}
