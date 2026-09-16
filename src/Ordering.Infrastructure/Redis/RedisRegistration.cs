using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.OutputCaching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ordering.Application.Abstractions;
using Ordering.Application.Abstractions.Caching;

namespace Ordering.Infrastructure.Redis;

/// <summary>
/// Everything Redis in one place: the process-wide connection, the output cache store shared by
/// every API instance (wrapped fail-open) and the catalogue eviction seam. The cache <em>policy</em>
/// (TTL, tag, vary-by) is an HTTP concern and stays in <c>Program</c>. Task 14 adds the stock
/// gate and the idempotency cache here, on the same connection.
/// </summary>
internal static class RedisRegistration
{
    public static IServiceCollection AddRedis(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(RedisOptions.SectionName).Get<RedisOptions>() ?? new RedisOptions();

        services.AddSingleton(options);
        services.AddSingleton<RedisConnection>();

        // The Microsoft store, on our multiplexer (one per process) and under our key prefix …
        services.AddStackExchangeRedisOutputCache(cache => cache.InstanceName = options.InstanceName);
        services.AddOptions<RedisOutputCacheOptions>().Configure<RedisConnection>((cache, connection) =>
            cache.ConnectionMultiplexerFactory = () => connection.GetAsync(CancellationToken.None));

        // … wrapped fail-open. The store type is internal to the package, so the descriptor it
        // just registered is taken over and instantiated inside the wrapper.
        var redisStore = services.Last(descriptor => descriptor.ServiceType == typeof(IOutputCacheStore));
        services.Remove(redisStore);
        services.AddSingleton<IOutputCacheStore>(sp => new ResilientOutputCacheStore(
            Instantiate(sp, redisStore),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<ResilientOutputCacheStore>>()));

        services.AddSingleton<ICatalogueCache, OutputCacheCatalogueCache>();

        return services;
    }

    private static IOutputCacheStore Instantiate(IServiceProvider provider, ServiceDescriptor descriptor) => descriptor switch
    {
        { ImplementationInstance: IOutputCacheStore instance } => instance,
        { ImplementationFactory: { } factory } => (IOutputCacheStore)factory(provider),
        { ImplementationType: { } type } => (IOutputCacheStore)ActivatorUtilities.CreateInstance(provider, type),
        _ => throw new InvalidOperationException("The Redis output cache store registration has no implementation."),
    };
}
