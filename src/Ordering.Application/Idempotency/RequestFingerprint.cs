using System.Security.Cryptography;
using System.Text;
using Ordering.Application.Features.Orders.CreateOrder;

namespace Ordering.Application.Idempotency;

/// <summary>
/// Defines payload equivalence for idempotency. Two submissions are the same request when their
/// canonical form is the same:
/// <code>
/// customerReference.Trim() + "|" + join(",", lines ordered by CODE, each "CODE:QTY")
/// where CODE = productCode.Trim().ToUpperInvariant() and QTY is the integer quantity
/// </code>
/// JSON key order, whitespace, product-code casing and headers play no part; the customer
/// reference is compared case-sensitively (see README, "Payload equivalence").
/// </summary>
public static class RequestFingerprint
{
    public static string Compute(CreateOrderCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(command))));
    }

    /// <summary>The string that is hashed; exposed so tests and docs can show the definition.</summary>
    public static string Canonicalize(CreateOrderCommand command)
    {
        var lines = command.Lines
            .Select(line => (Code: line.ProductCode.Trim().ToUpperInvariant(), line.Quantity))
            .OrderBy(line => line.Code, StringComparer.Ordinal)
            .Select(line => $"{line.Code}:{line.Quantity}");

        return $"{command.CustomerReference.Trim()}|{string.Join(",", lines)}";
    }
}
