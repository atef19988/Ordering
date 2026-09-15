namespace Ordering.Domain.Orders;

public sealed class Order
{
    public const int CustomerReferenceMaxLength = 64;

    private readonly List<OrderLine> _lines = [];

    private Order(long id, string customerReference, OrderStatus status, decimal total, DateTimeOffset createdAt, DateTimeOffset? cancelledAt)
    {
        Id = id;
        CustomerReference = customerReference;
        Status = status;
        Total = total;
        CreatedAt = createdAt;
        CancelledAt = cancelledAt;
    }

    public long Id { get; private set; }

    public string CustomerReference { get; private set; }

    public OrderStatus Status { get; private set; }

    /// <summary>Sum of the line totals; computed here, never taken from the request.</summary>
    public decimal Total { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public IReadOnlyList<OrderLine> Lines => _lines;

    /// <summary>
    /// Builds a confirmed order. Ids come from <c>IIdGenerator</c> (the order id first, so each
    /// line can carry it); unit prices come from the database.
    /// </summary>
    public static Order Create(long id, string customerReference, IReadOnlyCollection<OrderLine> lines, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerReference);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(customerReference.Length, CustomerReferenceMaxLength, nameof(customerReference));
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Count == 0)
        {
            throw new ArgumentException("An order needs at least one line.", nameof(lines));
        }

        if (lines.Any(line => line.OrderId != id))
        {
            throw new ArgumentException($"Every line must belong to order {id}.", nameof(lines));
        }

        if (lines.Select(line => line.ProductCode).Distinct(StringComparer.Ordinal).Count() != lines.Count)
        {
            throw new ArgumentException("A product may appear only once per order.", nameof(lines));
        }

        var order = new Order(id, customerReference, OrderStatus.Confirmed, lines.Sum(line => line.LineTotal), now, cancelledAt: null);
        order._lines.AddRange(lines);
        return order;
    }

    /// <summary>
    /// Only legal from <see cref="OrderStatus.Confirmed"/>. The persisted transition is the guarded
    /// <c>UPDATE ... WHERE status = 'Confirmed'</c> (Task 6); this is the same rule in memory.
    /// </summary>
    public void Cancel(DateTimeOffset now)
    {
        if (Status != OrderStatus.Confirmed)
        {
            throw new InvalidOperationException($"Order {Id} is {Status} and cannot be cancelled.");
        }

        Status = OrderStatus.Cancelled;
        CancelledAt = now;
    }
}
