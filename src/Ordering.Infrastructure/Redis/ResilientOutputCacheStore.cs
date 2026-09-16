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
/// "every catalogue request goes to SQL Server", never a 500 (CLAUDE.md rule 9).
/// </summary>
/// <remarks>
/// A refused connection fails in microseconds, but a black-holed one (a paused container, a
/// dropped route) fails only after the operation timeout — and a request does a get and a set.
/// So one failure opens a breaker for <see cref="BreakFor"/>: while it is open every call is
/// answered locally as a miss/no-op with no Redis round trip, then one call probes again. A
/// skipped eviction is bounded by the entry's own one-second TTL.
/// </remarks>
internal sealed partial class ResilientOutputCacheStore(IOutputCacheStore inner, IClock clock, ILogger<ResilientOutputCacheStore> logger) : IOutputCacheStore
{
    public static readonly TimeSpan BreakFor = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(1);

    private readonly LogThrottle _failureLog = new(LogInterval);
    private long _openUntilTicks;

    /// <summary>True while a recent failure has the breaker open; the cache is then bypassed.</summary>
    public bool IsBypassed => Volatile.Read(ref _openUntilTicks) > clock.UtcNow.UtcTicks;

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (IsBypassed)
        {
            return null;
        }

        try
        {
            return await inner.GetAsync(key, cancellationToken);
        }
        catch (Exception exception) when (IsRedisFailure(exception))
        {
            Trip("get", exception);
            return null;
        }
    }

    public async ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken)
    {
        if (IsBypassed)
        {
            return;
        }

        try
        {
            await inner.SetAsync(key, value, tags, validFor, cancellationToken);
        }
        catch (Exception exception) when (IsRedisFailure(exception))
        {
            Trip("set", exception);
        }
    }

    public async ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
    {
        if (IsBypassed)
        {
            return;
        }

        try
        {
            await inner.EvictByTagAsync(tag, cancellationToken);
        }
        catch (Exception exception) when (IsRedisFailure(exception))
        {
            Trip("evict", exception);
        }
    }

    private static bool IsRedisFailure(Exception exception) => exception is RedisException or TimeoutException;

    private void Trip(string operation, Exception exception)
    {
        var now = clock.UtcNow;
        Volatile.Write(ref _openUntilTicks, (now + BreakFor).UtcTicks);

        if (_failureLog.ShouldLog(now))
        {
            LogFailedOpen(logger, exception, operation, BreakFor.TotalSeconds);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Output cache {Operation} failed; bypassing Redis for {BreakSeconds} s and serving uncached (logged at most once a minute)")]
    private static partial void LogFailedOpen(ILogger logger, Exception exception, string operation, double breakSeconds);
}
