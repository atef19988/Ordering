using Ordering.Domain.Common;

namespace Ordering.Domain.Orders;

/// <summary>
/// <c>stock.insufficient</c> together with the facts the 409 body must carry: the product that
/// ran short and how many units it actually has left.
/// </summary>
public sealed record InsufficientStockError(string ProductCode, int Available)
    : Error(ErrorCode, $"Product '{ProductCode}' has only {Available} unit(s) available.", ErrorType.Conflict)
{
    public const string ErrorCode = "stock.insufficient";

    public override IReadOnlyDictionary<string, object?> Details => new Dictionary<string, object?>
    {
        ["productCode"] = ProductCode,
        ["available"] = Available,
    };
}
