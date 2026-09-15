using System.Globalization;
using Ordering.Application.Abstractions;
using Ordering.Domain.Common;

namespace Ordering.Api.Endpoints;

/// <summary>
/// The single Result → HTTP mapping in the codebase. Endpoints bind, dispatch and call one of these.
/// </summary>
public static class ResultExtensions
{
    public static IResult ToHttpResult(this Result result) =>
        result.IsSuccess ? TypedResults.NoContent() : ToProblem(result.Error);

    public static IResult ToHttpResult<TValue>(this Result<TValue> result) =>
        result.IsSuccess ? TypedResults.Ok(result.Value) : ToProblem(result.Error);

    public static IResult ToHttpResult<TValue>(this Result<TValue> result, Func<TValue, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : ToProblem(result.Error);

    private static IResult ToProblem(Error error)
    {
        // `code` always; structured facts (e.g. productCode/available for stock.insufficient) beside it.
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };

        foreach (var (key, value) in error.Details)
        {
            extensions[key] = value;
        }

        IResult problem = error switch
        {
            ValidationError validation => TypedResults.ValidationProblem(
                new Dictionary<string, string[]>(validation.Errors),
                detail: error.Message,
                extensions: extensions),

            _ => TypedResults.Problem(
                detail: error.Message,
                statusCode: StatusCodeOf(error.Type),
                extensions: extensions),
        };

        // A retryable failure tells the client when to come back, whatever its status code.
        return error.Details.TryGetValue(Error.RetryAfterSecondsKey, out var retryAfter) && retryAfter is int seconds
            ? new WithRetryAfter(problem, seconds)
            : problem;
    }

    private static int StatusCodeOf(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>Adds <c>Retry-After: &lt;seconds&gt;</c> to an otherwise unchanged problem response.</summary>
    public sealed class WithRetryAfter(IResult inner, int seconds) : IResult
    {
        public IResult Inner { get; } = inner;

        public int Seconds { get; } = seconds;

        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = Seconds.ToString(CultureInfo.InvariantCulture);
            return Inner.ExecuteAsync(httpContext);
        }
    }
}
