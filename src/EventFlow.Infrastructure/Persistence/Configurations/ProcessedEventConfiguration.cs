using EventFlow.Domain.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EventFlow.Infrastructure.Persistence.Configurations;

public sealed class ProcessedEventConfiguration : IEntityTypeConfiguration<ProcessedEvent>
{
    public void Configure(EntityTypeBuilder<ProcessedEvent> builder)
    {
        builder.ToTable("processed_events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(e => e.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(e => e.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(e => e.Scope)
            .HasColumnName("scope")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(e => e.EventId)
            .HasColumnName("event_id")
            .IsRequired();

        builder.Property(e => e.EventType)
            .HasColumnName("event_type")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(e => e.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .HasDefaultValue(ProcessedEventStatus.Completed)
            .IsRequired();

        builder.Property(e => e.LockToken)
            .HasColumnName("lock_token")
            .HasMaxLength(64)
            .IsConcurrencyToken();

        builder.Property(e => e.LockedUntilUtc)
            .HasColumnName("locked_until_utc");

        builder.Property(e => e.ResultSummary)
            .HasColumnName("result_summary")
            .HasMaxLength(512);

        builder.Property(e => e.ProcessedAt)
            .HasColumnName("processed_at")
            .IsRequired();

        // Enforce strict storage-level idempotency per (TenantId, Scope, IdempotencyKey)
        builder.HasIndex(e => new { e.TenantId, e.Scope, e.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ix_processed_events_tenant_scope_key");

        // Index for querying by EventId within a Tenant
        builder.HasIndex(e => new { e.TenantId, e.EventId })
            .HasDatabaseName("ix_processed_events_tenant_event_id");

        builder.HasIndex(e => new { e.Status, e.LockedUntilUtc })
            .HasDatabaseName("ix_processed_events_status_locked_until");
    }
}
