using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Ordering.Application.Abstractions.Messaging;

namespace Ordering.Application.Abstractions.Behaviors;

public sealed partial class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result, IResultFactory<TResponse>
{
    private static readonly string RequestName = typeof(TRequest).Name;

    public async Task<TResponse> Handle(TRequest request, Func<Task<TResponse>> next, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            var response = await next();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            if (response.IsSuccess)
            {
                LogSucceeded(logger, RequestName, elapsed);
            }
            else
            {
                LogFailed(logger, RequestName, response.Error.Code, elapsed);
            }

            return response;
        }
        catch (Exception exception)
        {
            LogThrew(logger, exception, RequestName, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Request} succeeded in {ElapsedMs:0.0} ms")]
    private static partial void LogSucceeded(ILogger logger, string request, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Request} failed with {ErrorCode} in {ElapsedMs:0.0} ms")]
    private static partial void LogFailed(ILogger logger, string request, string errorCode, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Request} threw after {ElapsedMs:0.0} ms")]
    private static partial void LogThrew(ILogger logger, Exception exception, string request, double elapsedMs);
}
