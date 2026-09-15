using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ordering.Application.Abstractions;
using Ordering.Application.Abstractions.Messaging;

namespace Ordering.Infrastructure.Outbox;

/// <summary>
/// Moves outbox rows to the broker and nothing else: claim a batch under a lease, publish each
/// row with confirms, mark it published. A full batch loops immediately; only an empty or partial
/// claim sleeps, so throughput is not capped by the poll interval. Safe to run N times — the
/// claim skips rows another relay holds. The reaper runs in the same loop and returns stale
/// <c>Processing</c> rows to <c>Pending</c>.
/// </summary>
internal sealed partial class OutboxRelay(
    OutboxStore store,
    IEventPublisher publisher,
    IClock clock,
    RelayOptions options,
    string instanceId,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    /// <summary>Back-off after the broker refused a publish, so a broker outage is not a claim/release hot loop.</summary>
    private static readonly TimeSpan BrokerRetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger, instanceId, options.BatchSize, options.PollIntervalMs);

        // Reap once on startup: claims left behind by a previous process are reclaimed right away.
        long? lastReap = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (lastReap is null || Stopwatch.GetElapsedTime(lastReap.Value) >= options.ReaperInterval)
                {
                    await ReapAsync(stoppingToken);
                    lastReap = Stopwatch.GetTimestamp();
                }

                var batch = await store.ClaimAsync(instanceId, options.BatchSize, options.Lease, clock.UtcNow, stoppingToken);
                var published = await PublishAsync(batch, stoppingToken);

                if (published < batch.Count)
                {
                    await Task.Delay(BrokerRetryDelay, stoppingToken);
                }
                else if (batch.Count < options.BatchSize)
                {
                    await Task.Delay(options.PollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogLoopFailed(logger, exception);
                await Task.Delay(options.PollInterval, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Publishes the whole batch concurrently on the one confirmed channel, so the broker's
    /// confirms pipeline instead of costing a round trip each, then marks every confirmed row in
    /// one statement and releases the rest. Returns how many rows were confirmed.
    /// </summary>
    private async Task<int> PublishAsync(IReadOnlyList<IntegrationEvent> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return 0;
        }

        var outcomes = await Task.WhenAll(batch.Select(@event => TryPublishAsync(@event, cancellationToken)));

        var published = outcomes.Where(o => o.Error is null).Select(o => o.Id).ToList();
        var failed = outcomes.Where(o => o.Error is not null).ToList();

        if (published.Count > 0)
        {
            await store.MarkPublishedAsync(published, clock.UtcNow, cancellationToken);
            LogPublished(logger, published.Count);
        }

        if (failed.Count > 0)
        {
            // Typically the broker is unreachable and every row failed the same way; one log line.
            LogPublishFailed(logger, failed[0].Error!, failed[0].Id, failed.Count);
            await store.ReleaseAsync(failed.Select(o => o.Id).ToList(), CancellationToken.None);
        }

        return published.Count;
    }

    private async Task<(long Id, Exception? Error)> TryPublishAsync(IntegrationEvent @event, CancellationToken cancellationToken)
    {
        try
        {
            await publisher.PublishAsync(@event, cancellationToken);
            return (@event.Id, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (@event.Id, exception);
        }
    }

    private async Task ReapAsync(CancellationToken cancellationToken)
    {
        var reaped = await store.ReapAsync(clock.UtcNow, options.PublishedTimeout, cancellationToken);

        if (reaped > 0)
        {
            LogReaped(logger, reaped);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbox relay {InstanceId} started (batch {BatchSize}, poll {PollIntervalMs} ms)")]
    private static partial void LogStarted(ILogger logger, string instanceId, int batchSize, int pollIntervalMs);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Published {Count} outbox messages")]
    private static partial void LogPublished(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Publishing outbox message {OutboxEventId} failed; released {Count} rows to Pending")]
    private static partial void LogPublishFailed(ILogger logger, Exception exception, long outboxEventId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reaper returned {Count} stale Processing rows to Pending")]
    private static partial void LogReaped(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox relay loop failed; retrying after the poll interval")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);
}
