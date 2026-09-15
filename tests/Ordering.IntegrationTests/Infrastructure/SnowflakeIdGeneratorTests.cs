using Ordering.Application.Abstractions;
using Ordering.Infrastructure.Common;

namespace Ordering.IntegrationTests.Infrastructure;

public class SnowflakeIdGeneratorTests
{
    [Fact]
    public void Ids_are_unique_and_increasing_when_many_threads_race_from_one_barrier()
    {
        const int threads = 32;
        const int perThread = 20_000;
        var generator = new SnowflakeIdGenerator(new SystemClock(), workerId: 7);
        var perThreadIds = new long[threads][];
        using var barrier = new Barrier(threads);

        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            var ids = new long[perThread];
            barrier.SignalAndWait();
            for (var i = 0; i < perThread; i++)
            {
                ids[i] = generator.NewId();
            }

            perThreadIds[t] = ids;
        })).ToArray();

        foreach (var worker in workers)
        {
            worker.Start();
        }

        foreach (var worker in workers)
        {
            worker.Join();
        }

        var all = perThreadIds.SelectMany(ids => ids).ToArray();
        Assert.Equal(threads * perThread, all.Distinct().Count());

        // Every thread saw a strictly increasing stream — the generator never hands out an id below an earlier one.
        Assert.All(perThreadIds, ids => Assert.True(ids.Zip(ids.Skip(1), (a, b) => b > a).All(x => x)));
        Assert.All(all, id => Assert.Equal(7, SnowflakeIdGenerator.Decode(id).WorkerId));
    }

    [Fact]
    public void Id_encodes_timestamp_worker_and_sequence()
    {
        var instant = new DateTimeOffset(2026, 9, 16, 12, 0, 0, 123, TimeSpan.Zero);
        var generator = new SnowflakeIdGenerator(new FrozenClock(instant), workerId: 1023);

        var first = generator.NewId();
        var second = generator.NewId();

        var decoded = SnowflakeIdGenerator.Decode(first);
        Assert.Equal(instant, decoded.Timestamp);
        Assert.Equal(1023, decoded.WorkerId);
        Assert.Equal(0, decoded.Sequence);
        Assert.Equal(1, SnowflakeIdGenerator.Decode(second).Sequence);
        Assert.True(second > first);
    }

    [Fact]
    public void Exhausting_a_millisecond_borrows_the_next_one_instead_of_blocking()
    {
        var instant = new DateTimeOffset(2026, 9, 16, 12, 0, 0, 0, TimeSpan.Zero);
        var generator = new SnowflakeIdGenerator(new FrozenClock(instant), workerId: 1);

        var ids = Enumerable.Range(0, 4097).Select(_ => generator.NewId()).ToArray();

        Assert.Equal(4097, ids.Distinct().Count());
        Assert.True(ids.Zip(ids.Skip(1), (a, b) => b > a).All(x => x));
        Assert.Equal(instant, SnowflakeIdGenerator.Decode(ids[4095]).Timestamp);
        Assert.Equal(instant.AddMilliseconds(1), SnowflakeIdGenerator.Decode(ids[4096]).Timestamp);
        Assert.Equal(0, SnowflakeIdGenerator.Decode(ids[4096]).Sequence);
    }

    [Fact]
    public void A_clock_that_steps_backwards_never_produces_a_smaller_id()
    {
        var clock = new FrozenClock(new DateTimeOffset(2026, 9, 16, 12, 0, 1, 0, TimeSpan.Zero));
        var generator = new SnowflakeIdGenerator(clock, workerId: 1);

        var before = generator.NewId();
        clock.Now = clock.Now.AddSeconds(-5);
        var after = generator.NewId();

        Assert.True(after > before);
        Assert.Equal(SnowflakeIdGenerator.Decode(before).Timestamp, SnowflakeIdGenerator.Decode(after).Timestamp);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1024)]
    public void Worker_id_outside_ten_bits_is_rejected(int workerId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SnowflakeIdGenerator(new SystemClock(), workerId));
    }

    private sealed class FrozenClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; set; } = now;

        public DateTimeOffset UtcNow => Now;
    }
}
