namespace EventFlow.Domain.Events;

/// <summary>
/// Represents the persisted domain event entity in the database.
/// </summary>
public sealed class EventRecord
{
    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public string TenantId { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public int Version { get; private set; }
    public string Source { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string CorrelationId { get; private set; } = string.Empty;
    public string? TraceParent { get; private set; }
    public string Payload { get; private set; } = string.Empty;
    public string MetadataJson { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset IngestedAt { get; private set; }

    // Required for EF Core
    private EventRecord() { }

    public static EventRecord FromEnvelope(EventEnvelope envelope, DateTimeOffset ingestedAt)
    {
        return new EventRecord
        {
            Id = Guid.NewGuid(),
            EventId = envelope.EventId,
            TenantId = envelope.TenantId,
            EventType = envelope.EventType,
            Version = envelope.Version,
            Source = envelope.Source,
            IdempotencyKey = envelope.IdempotencyKey,
            CorrelationId = envelope.CorrelationId,
            TraceParent = envelope.TraceParent,
            Payload = envelope.Payload,
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(envelope.Metadata),
            OccurredAt = envelope.OccurredAt,
            IngestedAt = ingestedAt
        };
    }
}
