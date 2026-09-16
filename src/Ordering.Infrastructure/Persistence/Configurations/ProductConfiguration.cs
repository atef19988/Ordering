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
        builder.Property(p => p.Price).HasColumnName("price").HasPrecision(Money.Precision, Money.Scale);
        builder.Property(p => p.AvailableQuantity).HasColumnName("available_quantity");

        // Keyset paging (Task 15): one seek per page for sort=name / sort=price; sort=code uses the
        // clustered key. Nothing carries available_quantity — it is written by every order.
        builder.HasIndex(p => new { p.Name, p.Code }).HasDatabaseName("ix_products_name");
        builder.HasIndex(p => new { p.Price, p.Code }).HasDatabaseName("ix_products_price");
    }
}
