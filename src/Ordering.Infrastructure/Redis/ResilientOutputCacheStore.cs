using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging;
using Ordering.Application.Abstractions;
using Ordering.Infrastructure.Common;
using StackExchange.Redis;

namespace Ordering.Infrastructure.Redis;

/// <summary>
/// Fail-open wrapper around the Redis output cache store: any <see cref="RedisException"/>
/// (connection, timeout, server) or plain <see cref="TimeoutException"/> becomes a miss on read
/// and a no-op on write and evict, logged at most once a minute. Redis down therefore means
/// "every catalogue request goes to SQL Server", never a 500 (CLAUDE.md rule 9). A lost
/// eviction is bounded by the entry's own one-second TTL.
/// </summary>
internal sealed partial class ResilientOutputCacheStore(IOutputCacheStore inner, IClock clock, ILogger<ResilientOutputCacheStore> logger) : IOutputCacheStore
{
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(1);

    private readonly LogThrottle _failureLog = new(LogInterval);

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await inner.GetAsync(key, cancellationToken);
        }
        catch (Exception exception) when (IsRedisFailure(exception))
        {
            Log("get", exception);
            return null;
        }
    }

    public async ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken)
    {
        try
        {
            await inner.SetAsync(key, value, tags, validFor, cancellationToken);
        }
        catch (Exception exception) when (IsRedisFailure(exception))
        {
            Log("set", exception);
        }
    }

    public async ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
    {
        try
        {
            await inner.EvictByTagAsync(tag, cancellationToken);
        }
        catch (Exception exception) when (IsRedisFailure(exception))
        {
            Log("evict", exception);
        }
    }

    private static bool IsRedisFailure(Exception exception) => exception is RedisException or TimeoutException;

    private void Log(string operation, Exception exception)
    {
        if (_failureLog.ShouldLog(clock.UtcNow))
        {
            LogFailedOpen(logger, exception, operation);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Output cache {Operation} failed; serving uncached (logged at most once a minute)")]
    private static partial void LogFailedOpen(ILogger logger, Exception exception, string operation);
}
