using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Ordering.Api.Middleware;

/// <summary>
/// Last line of defence: anything a handler did not turn into a <c>Result</c> becomes a logged
/// 500 ProblemDetails instead of a raw stack trace. Request-binding failures (malformed JSON,
/// missing body) keep their own 4xx status — a client error is never reported as a 500.
/// </summary>
public sealed partial class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            // Minimal APIs throw this for a body they cannot bind when ThrowOnBadRequest is on
            // (the Development default); elsewhere they write an empty 4xx. Same shape both ways.
            LogBadRequest(logger, exception.Message, context.Request.Method, context.Request.Path);

            if (context.Response.HasStarted)
            {
                throw;
            }

            await WriteProblemAsync(context, exception.StatusCode, "The request could not be read.", exception.Message,
                "https://tools.ietf.org/html/rfc9110#section-15.5.1");
        }
        catch (Exception exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            LogUnhandled(logger, exception, context.Request.Method, context.Request.Path);

            if (context.Response.HasStarted)
            {
                throw;
            }

            await WriteProblemAsync(context, StatusCodes.Status500InternalServerError, "An unexpected error occurred.", detail: null,
                "https://tools.ietf.org/html/rfc9110#section-15.6.1");
        }
    }

    private static Task WriteProblemAsync(HttpContext context, int statusCode, string title, string? detail, string type)
    {
        context.Response.Clear();
        context.Response.StatusCode = statusCode;

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Type = type,
            Extensions = { ["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier },
        };

        return context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", context.RequestAborted);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Request could not be read ({Reason}) for {Method} {Path}")]
    private static partial void LogBadRequest(ILogger logger, string reason, string method, PathString path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, PathString path);
}
