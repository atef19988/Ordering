namespace Ordering.Infrastructure.Common;

/// <summary>
/// "Log at most once per <paramref name="interval"/>" for a failure that would otherwise repeat
/// on every request (Redis down, broker down). Lock-free: one <see cref="Interlocked"/> exchange
/// over the last-logged timestamp. This is bookkeeping for a log line, not business state.
/// </summary>
public sealed class LogThrottle(TimeSpan interval)
{
    private long _lastLoggedTicks = long.MinValue;

    /// <summary>True for the first call and for the first call after each <paramref name="interval"/>.</summary>
    public bool ShouldLog(DateTimeOffset now)
    {
        var last = Interlocked.Read(ref _lastLoggedTicks);

        if (last != long.MinValue && now.UtcTicks - last < interval.Ticks)
        {
            return false;
        }

        return Interlocked.CompareExchange(ref _lastLoggedTicks, now.UtcTicks, last) == last;
    }
}
