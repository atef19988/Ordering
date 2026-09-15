namespace Ordering.Domain.Products;

/// <summary>
/// Catalogue item. Stock changes are conditional <c>UPDATE</c>s in the database (Tasks 4 and 6),
/// never in-memory mutations of this entity, so it exposes no <c>Deduct</c>/<c>Restore</c>.
/// </summary>
public sealed class Product
{
    public const int CodeMaxLength = 32;
    public const int NameMaxLength = 200;

    private Product(string code, string name, decimal price, int availableQuantity)
    {
        Code = code;
        Name = name;
        Price = price;
        AvailableQuantity = availableQuantity;
    }

    /// <summary>Natural key, e.g. <c>SKU-001</c>.</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }

    public decimal Price { get; private set; }

    public int AvailableQuantity { get; private set; }

    public static Product Create(string code, string name, decimal price, int availableQuantity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(code.Length, CodeMaxLength, nameof(code));
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, NameMaxLength, nameof(name));
        ArgumentOutOfRangeException.ThrowIfNegative(price);
        ArgumentOutOfRangeException.ThrowIfNegative(availableQuantity);

        return new Product(code, name, price, availableQuantity);
    }
}
