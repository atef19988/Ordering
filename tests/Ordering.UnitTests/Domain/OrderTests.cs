using Ordering.Domain.Orders;

namespace Ordering.UnitTests.Domain;

public class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private const long OrderId = 7_336_899_183_492_280_321;

    [Fact]
    public void Create_totals_the_lines_rounded_to_two_decimals()
    {
        var lines = new[]
        {
            Line(1, "SKU-001", quantity: 3, unitPrice: 12.505m), // 37.515 -> 37.52
            Line(2, "SKU-002", quantity: 1, unitPrice: 0.125m),  // 0.125  -> 0.13 (half away from zero, not banker's 0.12)
            Line(3, "SKU-003", quantity: 2, unitPrice: 4.00m),   // 8.00
        };

        var order = Order.Create(OrderId, "CUST-42", lines, Now);

        Assert.Equal(37.52m, lines[0].LineTotal);
        Assert.Equal(0.13m, lines[1].LineTotal);
        Assert.Equal(8.00m, lines[2].LineTotal);
        Assert.Equal(45.65m, order.Total);
        Assert.Equal(order.Lines.Sum(l => l.LineTotal), order.Total);
    }

    [Fact]
    public void Create_starts_confirmed_with_the_given_id_reference_and_timestamp()
    {
        var order = Order.Create(OrderId, "CUST-42", [Line(1, "SKU-001", 2, 12.50m)], Now);

        Assert.Equal(OrderId, order.Id);
        Assert.Equal("CUST-42", order.CustomerReference);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(Now, order.CreatedAt);
        Assert.Null(order.CancelledAt);
        Assert.Single(order.Lines);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Line_quantity_must_be_positive(int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Line(1, "SKU-001", quantity, 12.50m));
    }

    [Fact]
    public void Line_unit_price_cannot_be_negative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Line(1, "SKU-001", 1, -0.01m));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Customer_reference_must_not_be_blank(string reference)
    {
        Assert.Throws<ArgumentException>(() => Order.Create(OrderId, reference, [Line(1, "SKU-001", 1, 1m)], Now));
    }

    [Fact]
    public void Customer_reference_is_at_most_64_characters()
    {
        var sixtyFour = new string('x', 64);
        var sixtyFive = new string('x', 65);

        Assert.Equal(sixtyFour, Order.Create(OrderId, sixtyFour, [Line(1, "SKU-001", 1, 1m)], Now).CustomerReference);
        Assert.Throws<ArgumentOutOfRangeException>(() => Order.Create(OrderId, sixtyFive, [Line(1, "SKU-001", 1, 1m)], Now));
    }

    [Fact]
    public void Order_needs_at_least_one_line()
    {
        Assert.Throws<ArgumentException>(() => Order.Create(OrderId, "CUST-42", [], Now));
    }

    [Fact]
    public void Lines_must_belong_to_the_order()
    {
        var foreign = OrderLine.Create(1, orderId: OrderId + 1, "SKU-001", 1, 1m);

        Assert.Throws<ArgumentException>(() => Order.Create(OrderId, "CUST-42", [foreign], Now));
    }

    [Fact]
    public void A_product_appears_at_most_once_per_order()
    {
        var lines = new[] { Line(1, "SKU-001", 1, 1m), Line(2, "SKU-001", 2, 1m) };

        Assert.Throws<ArgumentException>(() => Order.Create(OrderId, "CUST-42", lines, Now));
    }

    [Fact]
    public void Cancel_from_confirmed_marks_the_order_cancelled()
    {
        var order = Order.Create(OrderId, "CUST-42", [Line(1, "SKU-001", 1, 1m)], Now);
        var later = Now.AddMinutes(5);

        order.Cancel(later);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(later, order.CancelledAt);
    }

    [Fact]
    public void Cancel_from_cancelled_is_rejected()
    {
        var order = Order.Create(OrderId, "CUST-42", [Line(1, "SKU-001", 1, 1m)], Now);
        order.Cancel(Now.AddMinutes(5));

        var exception = Assert.Throws<InvalidOperationException>(() => order.Cancel(Now.AddMinutes(10)));

        Assert.Contains("Cancelled", exception.Message);
        Assert.Equal(Now.AddMinutes(5), order.CancelledAt); // the first cancellation stands
    }

    private static OrderLine Line(long id, string productCode, int quantity, decimal unitPrice) =>
        OrderLine.Create(id, OrderId, productCode, quantity, unitPrice);
}
