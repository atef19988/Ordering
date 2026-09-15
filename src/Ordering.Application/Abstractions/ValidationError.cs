namespace Ordering.Application.Abstractions;

/// <summary>
/// A validation failure carrying the per-property messages that the API surfaces as
/// ProblemDetails <c>errors</c>.
/// </summary>
public sealed record ValidationError(IReadOnlyDictionary<string, string[]> Errors)
    : Error(ValidationErrorCode, "One or more validation errors occurred.", ErrorType.Validation)
{
    public const string ValidationErrorCode = "validation.failed";
}
