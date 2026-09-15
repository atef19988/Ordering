namespace Ordering.Domain.Common;

public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    Failure,

    /// <summary>Nothing is wrong with the request; the service could not serve it right now. Safe to retry.</summary>
    Unavailable,
}

/// <summary>
/// An expected failure. Codes are stable, dot-separated identifiers (e.g. <c>stock.insufficient</c>)
/// that clients may switch on; messages are for humans. Lives in Domain so that domain error
/// catalogues (<c>OrderErrors</c>) can build them; <c>Result</c> in Application carries them.
/// </summary>
public record Error(string Code, string Message, ErrorType Type)
{
    /// <summary>
    /// <see cref="Details"/> key whose <c>int</c> value the API turns into a <c>Retry-After</c> header.
    /// </summary>
    public const string RetryAfterSecondsKey = "retryAfterSeconds";

    private static readonly IReadOnlyDictionary<string, object?> NoDetails = new Dictionary<string, object?>();

    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.Failure);

    /// <summary>
    /// Structured facts a client can act on (for <c>stock.insufficient</c>: which product and how
    /// many units are left). The API copies them into the ProblemDetails extensions next to
    /// <c>code</c>. A computed property, so it takes no part in record equality.
    /// </summary>
    public virtual IReadOnlyDictionary<string, object?> Details => NoDetails;

    public static Error Validation(string code, string message) => new(code, message, ErrorType.Validation);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error Failure(string code, string message) => new(code, message, ErrorType.Failure);

    public static Error Unavailable(string code, string message) => new(code, message, ErrorType.Unavailable);
}
