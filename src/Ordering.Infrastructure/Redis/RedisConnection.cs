using Microsoft.Extensions.Logging;
using Ordering.Application.Abstractions;
using Ordering.Infrastructure.Common;
using StackExchange.Redis;

namespace Ordering.Infrastructure.Redis;

/// <summary>
/// The one <see cref="IConnectionMultiplexer"/> per process, opened on first use. It never
/// throws for an unreachable server (<c>AbortOnConnectFail = false</c>): the multiplexer comes
/// back disconnected, every operation on it fails fast with a <see cref="RedisConnectionException"/>
/// that the callers turn into "uncached" or "gate open", and it reconnects on its own in the
/// background. Connection loss is logged at most once a minute. Nothing outside
/// <c>Infrastructure/Redis</c> ever sees a Redis type; <c>/health</c> calls <see cref="IsConnectedAsync"/>.
/// </summary>
public sealed partial class RedisConnection(RedisOptions options, IClock clock, ILogger<RedisConnection> logger) : IAsyncDisposable
{
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(1);

    // Serialises multiplexer creation only; it protects no business state (CLAUDE.md rule 2).
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LogThrottle _failureLog = new(LogInterval);
    private IConnectionMultiplexer? _multiplexer;

    /// <summary>For <c>/health</c>: connects if needed (never throws) and reports whether Redis is reachable right now.</summary>
    public async Task<bool> IsConnectedAsync(CancellationToken cancellationToken)
    {
        var multiplexer = await GetAsync(cancellationToken);
        return multiplexer.IsConnected;
    }

    internal async Task<IConnectionMultiplexer> GetAsync(CancellationToken cancellationToken)
    {
        if (_multiplexer is { } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_multiplexer is { } created)
            {
                return created;
            }

            var configuration = ConfigurationOptions.Parse(options.Configuration);
            configuration.AbortOnConnectFail = false;
            configuration.ConnectTimeout = options.ConnectTimeoutMs;
            configuration.SyncTimeout = options.OperationTimeoutMs;
            configuration.AsyncTimeout = options.OperationTimeoutMs;
            configuration.ClientName = "ordering-api";
            var endpoint = options.Configuration.Split(',')[0];

            var multiplexer = await ConnectionMultiplexer.ConnectAsync(configuration);
            multiplexer.ConnectionFailed += (_, args) =>
            {
                if (_failureLog.ShouldLog(clock.UtcNow))
                {
                    LogUnreachable(logger, args.Exception, args.EndPoint?.ToString() ?? endpoint, args.FailureType.ToString());
                }
            };
            multiplexer.ConnectionRestored += (_, args) => LogRestored(logger, args.EndPoint?.ToString() ?? endpoint);

            if (multiplexer.IsConnected)
            {
                LogConnected(logger, endpoint);
            }
            else if (_failureLog.ShouldLog(clock.UtcNow))
            {
                LogUnreachable(logger, exception: null, endpoint, "InitialConnect");
            }

            _multiplexer = multiplexer;
            return multiplexer;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_multiplexer is not null)
        {
            await _multiplexer.DisposeAsync();
        }

        _gate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to Redis at {Endpoint}")]
    private static partial void LogConnected(ILogger logger, string endpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis at {Endpoint} is unreachable ({FailureType}); serving uncached until it is back")]
    private static partial void LogUnreachable(ILogger logger, Exception? exception, string endpoint, string failureType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Redis connection to {Endpoint} restored")]
    private static partial void LogRestored(ILogger logger, string endpoint);
}
