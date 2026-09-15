namespace Ordering.Domain.Common;

/// <summary>
/// A failure the caller should simply retry after a short wait — a lock that did not clear in
/// time, a duplicate that is still being processed. <see cref="RetryAfterSeconds"/> travels on
/// <see cref="Error.Details"/> so the API can emit a <c>Retry-After</c> header for any status.
/// </summary>
public sealed record RetryableError(string Code, string Message, ErrorType Type, int RetryAfterSeconds)
    : Error(Code, Message, Type)
{
    public override IReadOnlyDictionary<string, object?> Details => new Dictionary<string, object?>
    {
        [RetryAfterSecondsKey] = RetryAfterSeconds,
    };
}
