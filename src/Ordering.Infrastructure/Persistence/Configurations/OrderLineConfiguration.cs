using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;
using Ordering.Domain.Products;

namespace Ordering.Infrastructure.Persistence.Configurations;

internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable("order_lines", table => table.HasCheckConstraint("ck_order_lines_qty_positive", "[quantity] > 0"));

        builder.HasKey(line => line.Id).HasName("pk_order_lines");
        builder.Property(line => line.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(line => line.OrderId).HasColumnName("order_id");
        builder.Property(line => line.ProductCode).HasColumnName("product_code").HasMaxLength(Product.CodeMaxLength);
        builder.Property(line => line.Quantity).HasColumnName("quantity");
        builder.Property(line => line.UnitPrice).HasColumnName("unit_price").HasPrecision(18, Money.Scale);
        builder.Property(line => line.LineTotal).HasColumnName("line_total").HasPrecision(18, Money.Scale);

        builder.HasIndex(line => line.OrderId).HasDatabaseName("ix_order_lines_order_id");

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(line => line.ProductCode)
            .HasConstraintName("fk_order_lines_products")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(line => line.ProductCode).HasDatabaseName("ix_order_lines_product_code");
    }
}
