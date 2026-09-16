using System.Diagnostics;

namespace Ordering.IntegrationTests;

/// <summary>Real races and bounded waits: the two things a concurrency test is made of.</summary>
public static class Concurrency
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Starts <paramref name="count"/> calls that all wait on one barrier and are released
    /// together, so they hit the server as a burst, not as a loop that trickles requests in.
    /// </summary>
    public static async Task<T[]> BurstAsync<T>(int count, Func<int, Task<T>> action)
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var calls = Enumerable.Range(0, count)
            .Select(async i =>
            {
                await barrier.Task;
                return await action(i);
            })
            .ToArray();

        barrier.SetResult();
        return await Task.WhenAll(calls);
    }

    /// <summary>Polls <paramref name="probe"/> until <paramref name="until"/> holds; returns the last value, or throws with it after <paramref name="timeout"/>.</summary>
    public static async Task<T> WaitForAsync<T>(Func<Task<T>> probe, Func<T, bool> until, TimeSpan? timeout = null, string? what = null)
    {
        var elapsed = Stopwatch.StartNew();
        var limit = timeout ?? DefaultTimeout;

        while (true)
        {
            var value = await probe();

            if (until(value))
            {
                return value;
            }

            if (elapsed.Elapsed > limit)
            {
                throw new TimeoutException($"Waited {limit.TotalSeconds:0.#} s for {what ?? "the condition"}; last value: {value}");
            }

            await Task.Delay(PollInterval);
        }
    }

    /// <summary>A bounded wait for something that must <em>not</em> happen; the caller re-checks afterwards.</summary>
    public static Task SettleAsync(TimeSpan duration) => Task.Delay(duration);
}
