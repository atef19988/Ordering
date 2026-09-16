namespace Ordering.Api.Sse;

/// <summary>
/// Open streams on this instance, one <see cref="Interlocked"/> counter. This bounds capacity
/// (sockets, threads) and nothing else — there is no shared truth here, so per-instance is right.
/// </summary>
public sealed class SseConnections
{
    private int _open;

    public int Open => Volatile.Read(ref _open);

    /// <summary>Takes a slot, or refuses without side effects when <paramref name="max"/> are already open.</summary>
    public bool TryAcquire(int max)
    {
        if (Interlocked.Increment(ref _open) <= max)
        {
            return true;
        }

        Interlocked.Decrement(ref _open);
        return false;
    }

    public void Release() => Interlocked.Decrement(ref _open);
}
