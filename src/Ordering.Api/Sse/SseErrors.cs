using Ordering.Domain.Common;

namespace Ordering.Api.Sse;

/// <summary>The stream's own expected failures; the order ones (<c>order.not_found</c>) come from the query.</summary>
public static class SseErrors
{
    public const int RetryAfterSeconds = 5;

    /// <summary>This instance has <c>Sse:MaxConnections</c> streams open. Capacity, not correctness: retry, or poll.</summary>
    public static Error Full(int maxConnections) =>
        new RetryableError("sse.full", $"This instance already serves {maxConnections} event streams; retry later or poll GET /api/orders/{{id}}.", ErrorType.Unavailable, RetryAfterSeconds);
}
