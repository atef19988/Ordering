using Ordering.Application.Features.Orders.CreateOrder;
using Ordering.Application.Idempotency;

namespace Ordering.UnitTests.Idempotency;

/// <summary>Pins the payload-equivalence definition documented in the README.</summary>
public class RequestFingerprintTests
{
    private static readonly CreateOrderCommand Reference = Command("CUST-1", ("SKU-001", 2), ("SKU-002", 1));

    [Fact]
    public void Canonical_form_is_trimmed_reference_then_lines_by_upper_cased_code()
    {
        Assert.Equal("CUST-1|SKU-001:2,SKU-002:1", RequestFingerprint.Canonicalize(Reference));
    }

    [Fact]
    public void Hash_is_64_lower_case_hex_characters()
    {
        var hash = RequestFingerprint.Compute(Reference);

        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c)));
    }

    [Theory]
    [InlineData("  CUST-1 ", "sku-002", 1, "SKU-001 ", 2)] // padded reference, lower case, trailing space, other line order
    [InlineData("CUST-1", "SKU-001", 2, "SKU-002", 1)]
    public void Equivalent_payloads_hash_the_same(string customer, string code1, int qty1, string code2, int qty2)
    {
        var other = Command(customer, (code1, qty1), (code2, qty2));

        Assert.Equal(RequestFingerprint.Compute(Reference), RequestFingerprint.Compute(other));
    }

    [Theory]
    [InlineData("CUST-1", "SKU-001", 3, "SKU-002", 1)] // quantity
    [InlineData("CUST-1", "SKU-001", 2, "SKU-003", 1)] // product
    [InlineData("CUST-2", "SKU-001", 2, "SKU-002", 1)] // customer
    [InlineData("cust-1", "SKU-001", 2, "SKU-002", 1)] // customer reference is case-sensitive
    public void Different_payloads_hash_differently(string customer, string code1, int qty1, string code2, int qty2)
    {
        var other = Command(customer, (code1, qty1), (code2, qty2));

        Assert.NotEqual(RequestFingerprint.Compute(Reference), RequestFingerprint.Compute(other));
    }

    [Fact]
    public void Key_and_headers_play_no_part()
    {
        var otherKey = Reference with { IdempotencyKey = Guid.NewGuid().ToString() };

        Assert.Equal(RequestFingerprint.Compute(Reference), RequestFingerprint.Compute(otherKey));
    }

    private static CreateOrderCommand Command(string customer, params (string Code, int Quantity)[] lines) =>
        new("01ARZ3NDEKTSV4RRFFQ69G5FAV", customer, lines.Select(l => new CreateOrderLine(l.Code, l.Quantity)).ToList());
}
