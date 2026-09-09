using EventFlow.Domain.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventFlow.Infrastructure.Persistence.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(o => o.EventId)
            .HasColumnName("event_id")
            .IsRequired();

        builder.Property(o => o.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(o => o.EventType)
            .HasColumnName("event_type")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(o => o.RoutingKey)
            .HasColumnName("routing_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(o => o.Payload)
            .HasColumnName("payload")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(o => o.TraceParent)
            .HasColumnName("trace_parent")
            .HasMaxLength(128);

        builder.Property(o => o.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(o => o.LockToken)
            .HasColumnName("lock_token")
            .HasMaxLength(64);

        builder.Property(o => o.LockedUntilUtc)
            .HasColumnName("locked_until_utc");

        builder.Property(o => o.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(o => o.ProcessedAt)
            .HasColumnName("processed_at");

        builder.Property(o => o.NextRetryAt)
            .HasColumnName("next_retry_at");

        builder.Property(o => o.RetryCount)
            .HasColumnName("retry_count")
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(o => o.LastError)
            .HasColumnName("last_error")
            .HasColumnType("text");

        // Index optimizing outbox processor leasing and batch polling queries
        builder.HasIndex(o => new { o.Status, o.LockedUntilUtc, o.NextRetryAt, o.CreatedAt })
            .HasDatabaseName("ix_outbox_messages_status_locked_retry_created");

        builder.HasIndex(o => o.EventId)
            .HasDatabaseName("ix_outbox_messages_event_id");
    }
}
