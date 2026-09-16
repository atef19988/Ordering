using Ordering.Infrastructure.Messaging;

namespace Ordering.Api.Sse;

/// <summary>
/// Feeds the broker's change hints into this instance's <see cref="IOrderChangeHub"/>. Like the
/// notification consumer, it reconnects after a delay when the broker is unreachable; while it
/// is down, open streams simply see no pushes and clients catch up on their next reconnect.
/// </summary>
public sealed partial class ChangeHintListener(IChangeHintSubscription hints, IOrderChangeHub hub, ILogger<ChangeHintListener> logger) : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await hints.ConsumeAsync(hub.Publish, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogListenFailed(logger, exception, ReconnectDelay.TotalSeconds);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Change hint listener stopped; reconnecting in {DelaySeconds} s (streams push nothing until then)")]
    private static partial void LogListenFailed(ILogger logger, Exception exception, double delaySeconds);
}
