using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Domain.Common;
using Ordering.Domain.Products;

namespace Ordering.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products", table =>
        {
            table.HasCheckConstraint("ck_products_price_non_negative", "[price] >= 0");
            // The backstop for stock: a conditional UPDATE that would go negative fails here instead.
            table.HasCheckConstraint("ck_products_qty_non_negative", "[available_quantity] >= 0");
        });

        builder.HasKey(p => p.Code).HasName("pk_products");

        builder.Property(p => p.Code).HasColumnName("code").HasMaxLength(Product.CodeMaxLength);
        builder.Property(p => p.Name).HasColumnName("name").HasMaxLength(Product.NameMaxLength);
        builder.Property(p => p.Price).HasColumnName("price").HasPrecision(18, Money.Scale);
        builder.Property(p => p.AvailableQuantity).HasColumnName("available_quantity");
    }
}
