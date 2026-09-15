using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");

        // Snowflake id assigned in code; clustered PK appends because ids are time-ordered.
        builder.HasKey(o => o.Id).HasName("pk_orders");
        builder.Property(o => o.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(o => o.CustomerReference).HasColumnName("customer_reference").HasMaxLength(Order.CustomerReferenceMaxLength);
        builder.Property(o => o.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(o => o.Total).HasColumnName("total").HasPrecision(18, Money.Scale);
        builder.Property(o => o.CreatedAt).HasColumnName("created_at").HasColumnType("datetimeoffset");
        builder.Property(o => o.CancelledAt).HasColumnName("cancelled_at").HasColumnType("datetimeoffset");

        // Concurrency token kept out of the domain type: a shadow property EF reads and compares.
        builder.Property<byte[]>("RowVersion").HasColumnName("row_version").IsRowVersion().IsRequired();

        builder.HasMany(o => o.Lines)
            .WithOne()
            .HasForeignKey(line => line.OrderId)
            .HasConstraintName("fk_order_lines_orders")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(o => o.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
