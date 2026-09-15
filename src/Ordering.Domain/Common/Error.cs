namespace Ordering.Domain.Common;

public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    Failure,
}

/// <summary>
/// An expected failure. Codes are stable, dot-separated identifiers (e.g. <c>stock.insufficient</c>)
/// that clients may switch on; messages are for humans. Lives in Domain so that domain error
/// catalogues (<c>OrderErrors</c>) can build them; <c>Result</c> in Application carries them.
/// </summary>
public record Error(string Code, string Message, ErrorType Type)
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Failure);

    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);
}
