using EventFlow.Domain.Events;
using EventFlow.Domain.Idempotency;
using EventFlow.Domain.Outbox;
using Microsoft.EntityFrameworkCore;

namespace EventFlow.Infrastructure.Persistence;

/// <summary>
/// Entity Framework Core database context for the EventFlow event ingestion and outbox subsystem.
/// </summary>
public sealed class EventFlowDbContext : DbContext
{
    public EventFlowDbContext(DbContextOptions<EventFlowDbContext> options) : base(options)
    {
    }

    public DbSet<EventRecord> Events => Set<EventRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EventFlowDbContext).Assembly);
    }
}
