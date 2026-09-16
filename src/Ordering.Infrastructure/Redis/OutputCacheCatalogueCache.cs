using Microsoft.AspNetCore.OutputCaching;
using Ordering.Application.Abstractions.Caching;

namespace Ordering.Infrastructure.Redis;

/// <summary>
/// <see cref="ICatalogueCache"/> = "evict the <see cref="Tag"/> tag" on the (fail-open) output
/// cache store, so every instance's next <c>GET /api/products</c> reads the database.
/// </summary>
internal sealed class OutputCacheCatalogueCache(IOutputCacheStore store) : ICatalogueCache
{
    /// <summary>The tag every cached catalogue page carries; the policy in <c>Program</c> uses the same name.</summary>
    public const string Tag = "catalogue";

    public Task InvalidateAsync(CancellationToken cancellationToken) => store.EvictByTagAsync(Tag, cancellationToken).AsTask();
}
