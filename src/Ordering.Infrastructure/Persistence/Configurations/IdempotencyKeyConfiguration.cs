using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Application.Idempotency;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Persistence.Configurations;

internal sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.ToTable(IdempotencyKey.TableName);

        // The primary key is the idempotency check: one order per key, enforced by the database.
        builder.HasKey(k => k.Key).HasName(IdempotencyKey.PrimaryKeyConstraint);
        builder.Property(k => k.Key).HasColumnName("idempotency_key").HasMaxLength(IdempotencyKey.KeyMaxLength);

        builder.Property(k => k.RequestHash)
            .HasColumnName("request_hash")
            .HasColumnType($"char({IdempotencyKey.RequestHashLength})");

        builder.Property(k => k.OrderId).HasColumnName("order_id");
        builder.Property(k => k.CreatedAt).HasColumnName("created_at").HasColumnType("datetimeoffset");

        // Same SaveChanges as the order, so the FK is satisfied inside one batch.
        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(k => k.OrderId)
            .HasConstraintName("fk_idempotency_keys_orders")
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(k => k.OrderId).HasDatabaseName("ix_idempotency_keys_order_id");
    }
}
