using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Application.Abstractions.Outbox;
using Ordering.Application.Features.Orders;

namespace Ordering.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages", table => table.HasCheckConstraint("ck_outbox_payload_json", "ISJSON([payload]) = 1"));

        // Snowflake event id assigned in code; stable across every retry.
        builder.HasKey(m => m.Id).HasName("pk_outbox_messages");
        builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(m => m.Type).HasColumnName("type").HasMaxLength(OutboxMessage.TypeMaxLength);
        builder.Property(m => m.AggregateId).HasColumnName("aggregate_id");
        builder.Property(m => m.Payload).HasColumnName("payload").HasColumnType("nvarchar(max)");
        builder.Property(m => m.OccurredAt).HasColumnName("occurred_at").HasColumnType("datetimeoffset");
        builder.Property(m => m.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(OutboxMessage.StatusMaxLength);
        builder.Property(m => m.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0);
        builder.Property(m => m.NextAttemptAt).HasColumnName("next_attempt_at").HasColumnType("datetimeoffset");
        builder.Property(m => m.ClaimedBy).HasColumnName("claimed_by").HasMaxLength(OutboxMessage.ClaimedByMaxLength);
        builder.Property(m => m.ClaimedUntil).HasColumnName("claimed_until").HasColumnType("datetimeoffset");
        builder.Property(m => m.PublishedAt).HasColumnName("published_at").HasColumnType("datetimeoffset");
        builder.Property(m => m.ProcessedAt).HasColumnName("processed_at").HasColumnType("datetimeoffset");
        builder.Property(m => m.LastError).HasColumnName("last_error").HasMaxLength(OutboxMessage.LastErrorMaxLength);

        // One created-notification per order, enforced by the database rather than by a lookup.
        builder.HasIndex(m => new { m.Type, m.AggregateId })
            .IsUnique()
            .HasDatabaseName("ux_outbox_order_created")
            .HasFilter($"[type] = '{OrderCreated.EventType}'");

        // What the relay's claim and the reaper scan: due rows that are not yet Sent/Failed.
        builder.HasIndex(m => new { m.Status, m.NextAttemptAt })
            .HasDatabaseName("ix_outbox_due")
            .HasFilter($"[status] IN ('{nameof(OutboxMessageStatus.Pending)}', '{nameof(OutboxMessageStatus.Processing)}')");
    }
}
