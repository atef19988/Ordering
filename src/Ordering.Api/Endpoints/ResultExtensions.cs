using Ordering.Application.Abstractions;

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
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };

        return error switch
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
    }

    private static int StatusCodeOf(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError,
    };
}
