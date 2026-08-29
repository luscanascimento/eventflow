using EventFlow.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventFlow.Infrastructure.Persistence.Configurations;

public sealed class EventRecordConfiguration : IEntityTypeConfiguration<EventRecord>
{
    public void Configure(EntityTypeBuilder<EventRecord> builder)
    {
        builder.ToTable("events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(e => e.EventId)
            .HasColumnName("event_id")
            .IsRequired();

        builder.Property(e => e.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.EventType)
            .HasColumnName("event_type")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(e => e.Version)
            .HasColumnName("version")
            .IsRequired();

        builder.Property(e => e.Source)
            .HasColumnName("source")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(e => e.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(e => e.CorrelationId)
            .HasColumnName("correlation_id")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(e => e.TraceParent)
            .HasColumnName("trace_parent")
            .HasMaxLength(128);

        builder.Property(e => e.Payload)
            .HasColumnName("payload")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(e => e.MetadataJson)
            .HasColumnName("metadata_json")
            .HasColumnType("text")
            .IsRequired();

        builder.Property(e => e.OccurredAt)
            .HasColumnName("occurred_at")
            .IsRequired();

        builder.Property(e => e.IngestedAt)
            .HasColumnName("ingested_at")
            .IsRequired();

        // Unique constraint on (TenantId, IdempotencyKey) to enforce storage-level idempotency
        builder.HasIndex(e => new { e.TenantId, e.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ix_events_tenant_id_idempotency_key");

        // Composite index for fast tenant event timeline querying
        builder.HasIndex(e => new { e.TenantId, e.EventType, e.OccurredAt })
            .HasDatabaseName("ix_events_tenant_id_event_type_occurred_at");
    }
}
