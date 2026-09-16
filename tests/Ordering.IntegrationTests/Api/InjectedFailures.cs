using System.Collections.Concurrent;
using Ordering.Application.Abstractions;

namespace Ordering.IntegrationTests.Api;

/// <summary>
/// The test host's <see cref="IFailurePoint"/>. A test arms one named point, one shot, with the
/// way the process should fail there: <see cref="ThrowAt"/> makes the caller throw (an
/// unhandled exception on the request path, rolled back by <c>TransactionBehavior</c>);
/// <see cref="HoldAt"/> parks the caller until the host is stopped, which is how a test kills
/// a process mid-flight — nothing is faked, the host really goes down holding whatever it holds.
/// </summary>
public sealed class InjectedFailures : IFailurePoint
{
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task>> _armed = new();

    public void ThrowAt(string point) =>
        _armed[point] = _ => throw new FailurePointException(point);

    /// <summary>Returns a task that completes once the code under test is parked at <paramref name="point"/>.</summary>
    public Task HoldAt(string point)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _armed[point] = async stopping =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, stopping);
        };

        return reached.Task;
    }

    public ValueTask ReachedAsync(string point, CancellationToken cancellationToken) =>
        _armed.TryRemove(point, out var fail) ? new ValueTask(fail(cancellationToken)) : ValueTask.CompletedTask;
}

public sealed class FailurePointException(string point) : Exception($"Injected failure at '{point}'.")
{
    public string Point { get; } = point;
}
