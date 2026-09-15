using Ordering.Application.Abstractions;

namespace Ordering.Infrastructure.Common;

/// <summary>
/// 64-bit time-ordered ids: 41 bits of milliseconds since <see cref="Epoch"/>, 10 bits of worker id,
/// 12 bits of per-millisecond sequence. Stored as <c>bigint</c>; ascending, so clustered inserts
/// append.
/// <para>
/// Lock-free. Racing callers compete on a single compare-and-swap over one packed
/// (timestamp, sequence) word, so no two calls can ever observe the same pair. When a millisecond's
/// 4096 ids are exhausted, or the wall clock steps backwards, the generator keeps counting on a
/// logical clock that never runs behind the last issued id: no spin-waiting for the clock, no
/// exceptions, ids stay unique and strictly increasing per worker.
/// </para>
/// <para>
/// Uniqueness across processes relies on every instance having its own <c>Snowflake:WorkerId</c>.
/// </para>
/// </summary>
public sealed class SnowflakeIdGenerator : IIdGenerator
{
    public static readonly DateTimeOffset Epoch = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public const int MaxWorkerId = (1 << WorkerIdBits) - 1;

    private const int WorkerIdBits = 10;
    private const int SequenceBits = 12;
    private const long SequenceMask = (1L << SequenceBits) - 1;
    private const int WorkerIdShift = SequenceBits;
    private const int TimestampShift = SequenceBits + WorkerIdBits;

    private readonly IClock _clock;
    private readonly long _workerId;

    // (last timestamp << SequenceBits) | last sequence — the only mutable state, updated by CAS.
    private long _state;

    public SnowflakeIdGenerator(IClock clock, int workerId)
    {
        if (workerId is < 0 or > MaxWorkerId)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId), workerId, $"Worker id must be between 0 and {MaxWorkerId}.");
        }

        _clock = clock;
        _workerId = workerId;
    }

    public long NewId()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _state);
            var lastTimestamp = observed >> SequenceBits;
            var now = MillisecondsSinceEpoch(_clock.UtcNow);

            long timestamp;
            long sequence;

            if (now > lastTimestamp)
            {
                timestamp = now;
                sequence = 0;
            }
            else
            {
                // Same millisecond, or the clock went backwards: stay on the logical clock and count on.
                sequence = ((observed & SequenceMask) + 1) & SequenceMask;
                timestamp = sequence == 0 ? lastTimestamp + 1 : lastTimestamp;
            }

            var next = (timestamp << SequenceBits) | sequence;

            if (Interlocked.CompareExchange(ref _state, next, observed) == observed)
            {
                return (timestamp << TimestampShift) | (_workerId << WorkerIdShift) | sequence;
            }
        }
    }

    public static (DateTimeOffset Timestamp, int WorkerId, int Sequence) Decode(long id) => (
        Epoch.AddMilliseconds(id >> TimestampShift),
        (int)((id >> WorkerIdShift) & MaxWorkerId),
        (int)(id & SequenceMask));

    private static long MillisecondsSinceEpoch(DateTimeOffset instant) =>
        (instant - Epoch).Ticks / TimeSpan.TicksPerMillisecond;
}
