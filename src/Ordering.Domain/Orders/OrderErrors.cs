using Ordering.Domain.Common;

namespace Ordering.Domain.Orders;

/// <summary>
/// The expected failures of the ordering feature. Handlers return these inside a <c>Result</c>;
/// codes are part of the API contract (<c>extensions.code</c> on ProblemDetails).
/// </summary>
public static class OrderErrors
{
    public static Error InsufficientStock(string productCode, int available) =>
        new InsufficientStockError(productCode, available);

    public static Error NotFound(long orderId) =>
        Error.NotFound("order.not_found", $"Order '{orderId}' was not found.");

    public static Error AlreadyCancelled(long orderId) =>
        Error.Conflict("order.already_cancelled", $"Order '{orderId}' is already cancelled.");

    public static Error UnknownProduct(string productCode) =>
        Error.NotFound("product.unknown", $"Product '{productCode}' does not exist.");
}
