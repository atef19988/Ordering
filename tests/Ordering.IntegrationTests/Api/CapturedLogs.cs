using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Ordering.IntegrationTests.Api;

/// <summary>
/// A Serilog sink the host picks up from DI (<c>ReadFrom.Services</c>). Some consumer outcomes,
/// such as a duplicate acknowledged without a send, leave no row and no message behind; the log
/// line is the only witness, and this is how a test waits for it.
/// </summary>
public sealed class CapturedLogs : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyList<LogEvent> Events => _events.ToArray();

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    /// <summary>Events whose template contains <paramref name="templateFragment"/> and whose <c>OutboxEventId</c> property is <paramref name="eventId"/>.</summary>
    public IReadOnlyList<LogEvent> About(long eventId, string templateFragment) =>
        Events
            .Where(e => e.MessageTemplate.Text.Contains(templateFragment, StringComparison.Ordinal))
            .Where(e => e.Properties.TryGetValue("OutboxEventId", out var id) && id is ScalarValue { Value: long value } && value == eventId)
            .ToList();
}
