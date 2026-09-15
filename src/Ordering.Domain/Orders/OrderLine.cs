using Ordering.Domain.Common;

namespace Ordering.Domain.Orders;

public sealed class OrderLine
{
    private OrderLine(long id, long orderId, string productCode, int quantity, decimal unitPrice, decimal lineTotal)
    {
        Id = id;
        OrderId = orderId;
        ProductCode = productCode;
        Quantity = quantity;
        UnitPrice = unitPrice;
        LineTotal = lineTotal;
    }

    public long Id { get; private set; }

    public long OrderId { get; private set; }

    public string ProductCode { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>Snapshot of the catalogue price at order time; read from the database, never from the request.</summary>
    public decimal UnitPrice { get; private set; }

    /// <summary><c>Quantity * UnitPrice</c> rounded to 2 dp.</summary>
    public decimal LineTotal { get; private set; }

    /// <param name="id">Snowflake id from <c>IIdGenerator</c>.</param>
    /// <param name="orderId">Snowflake id of the owning order, generated before the order itself.</param>
    public static OrderLine Create(long id, long orderId, string productCode, int quantity, decimal unitPrice)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productCode);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentOutOfRangeException.ThrowIfNegative(unitPrice);

        return new OrderLine(id, orderId, productCode, quantity, unitPrice, Money.Round(quantity * unitPrice));
    }
}
