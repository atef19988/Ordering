using Ordering.Domain.Common;
using Ordering.Domain.Orders;

namespace Ordering.UnitTests.Domain;

/// <summary>Error codes are part of the HTTP contract; this pins them.</summary>
public class OrderErrorsTests
{
    [Fact]
    public void Codes_and_types_are_stable()
    {
        Assert.Equal(("stock.insufficient", ErrorType.Conflict), Shape(OrderErrors.InsufficientStock("SKU-001", 0)));
        Assert.Equal(("order.not_found", ErrorType.NotFound), Shape(OrderErrors.NotFound(1)));
        Assert.Equal(("order.already_cancelled", ErrorType.Conflict), Shape(OrderErrors.AlreadyCancelled(1)));
        Assert.Equal(("product.unknown", ErrorType.NotFound), Shape(OrderErrors.UnknownProduct("SKU-404")));
    }

    [Fact]
    public void Messages_name_the_offending_thing()
    {
        Assert.Contains("SKU-001", OrderErrors.InsufficientStock("SKU-001", 3).Message);
        Assert.Contains("3", OrderErrors.InsufficientStock("SKU-001", 3).Message);
        Assert.Contains("42", OrderErrors.NotFound(42).Message);
    }

    private static (string Code, ErrorType Type) Shape(Error error) => (error.Code, error.Type);
}
