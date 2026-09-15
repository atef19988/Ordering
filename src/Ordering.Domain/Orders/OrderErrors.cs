using Ordering.Domain.Common;

namespace Ordering.Domain.Orders;

/// <summary>
/// The expected failures of the ordering feature. Handlers return these inside a <c>Result</c>;
/// codes are part of the API contract (<c>extensions.code</c> on ProblemDetails).
/// </summary>
public static class OrderErrors
{
    /// <summary>The wait a client is told to observe after a lock timeout or an in-flight duplicate.</summary>
    public const int RetryAfterSeconds = 1;

    public static Error InsufficientStock(string productCode, int available) =>
        new InsufficientStockError(productCode, available);

    /// <summary>The product row stayed locked by another writer for the whole lock timeout. Nothing was committed.</summary>
    public static Error StockBusy(string productCode) =>
        new RetryableError("stock.busy", $"Product '{productCode}' is busy; retry the same request.", ErrorType.Unavailable, RetryAfterSeconds);

    public static Error NotFound(long orderId) =>
        Error.NotFound("order.not_found", $"Order '{orderId}' was not found.");

    public static Error AlreadyCancelled(long orderId) =>
        Error.Conflict("order.already_cancelled", $"Order '{orderId}' is already cancelled.");

    public static Error UnknownProduct(string productCode) =>
        Error.NotFound("product.unknown", $"Product '{productCode}' does not exist.");

    /// <summary>The key belongs to a committed order whose payload differs from this one.</summary>
    public static Error IdempotencyKeyReuse(string idempotencyKey) =>
        Error.Conflict("idempotency.key_reuse", $"Idempotency-Key '{idempotencyKey}' was already used with a different payload.");

    /// <summary>A request with the same key is still inside its transaction; the replay will be available shortly.</summary>
    public static Error IdempotencyInProgress(string idempotencyKey) =>
        new RetryableError("idempotency.in_progress", $"A request with Idempotency-Key '{idempotencyKey}' is still being processed.", ErrorType.Conflict, RetryAfterSeconds);
}
