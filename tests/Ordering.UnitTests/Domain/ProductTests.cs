using Ordering.Domain.Products;

namespace Ordering.UnitTests.Domain;

public class ProductTests
{
    [Fact]
    public void Create_keeps_price_as_decimal_and_quantity_as_given()
    {
        var product = Product.Create("SKU-001", "Thermal label roll", 12.50m, 10);

        Assert.Equal("SKU-001", product.Code);
        Assert.Equal("Thermal label roll", product.Name);
        Assert.Equal(12.50m, product.Price);
        Assert.Equal(10, product.AvailableQuantity);
    }

    [Theory]
    [InlineData(-0.01, 1)]
    [InlineData(1.00, -1)]
    public void Price_and_quantity_cannot_be_negative(double price, int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Product.Create("SKU-001", "x", (decimal)price, quantity));
    }

    [Theory]
    [InlineData("", "name")]
    [InlineData("SKU-001", " ")]
    public void Code_and_name_are_required(string code, string name)
    {
        Assert.Throws<ArgumentException>(() => Product.Create(code, name, 1m, 1));
    }

    [Fact]
    public void Code_and_name_respect_the_column_lengths()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Product.Create(new string('c', 33), "name", 1m, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Product.Create("SKU-001", new string('n', 201), 1m, 1));
    }
}
