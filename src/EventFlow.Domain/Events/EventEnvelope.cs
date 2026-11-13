using EventFlow.Domain.Common;

namespace EventFlow.Domain.Events;

/// <summary>
/// Immutable standardized envelope wrapping all system events with uniform metadata,
/// multi-tenancy isolation, tracing context, and idempotent keys.
/// </summary>
public sealed class EventEnvelope
{
    private EventEnvelope(
        Guid eventId,
        string eventType,
        int version,
        string source,
        DateTimeOffset occurredAt,
        string correlationId,
        string? traceParent,
        string tenantId,
        string idempotencyKey,
        string payload,
        IReadOnlyDictionary<string, string> metadata)
    {
        EventId = eventId;
        EventType = eventType;
        Version = version;
        Source = source;
        OccurredAt = occurredAt;
        CorrelationId = correlationId;
        TraceParent = traceParent;
        TenantId = tenantId;
        IdempotencyKey = idempotencyKey;
        Payload = payload;
        Metadata = metadata;
    }

    public Guid EventId { get; }
    public string EventType { get; }
    public int Version { get; }
    public string Source { get; }
    public DateTimeOffset OccurredAt { get; }
    public string CorrelationId { get; }
    public string? TraceParent { get; }
    public string TenantId { get; }
    public string IdempotencyKey { get; }
    public string Payload { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    public static Result<EventEnvelope> Create(
        Guid eventId,
        string eventType,
        int version,
        string source,
        DateTimeOffset occurredAt,
        string correlationId,
        string? traceParent,
        string tenantId,
        string idempotencyKey,
        string payload,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (eventId == Guid.Empty)
        {
            return Result.Failure<EventEnvelope>(
                DomainError.Validation("EventEnvelope.InvalidEventId", "EventId must not be an empty GUID."));
        }

        if (string.IsNullOrWhiteSpace(eventType))
        {
            return Result.Failure<EventEnvelope>(
                DomainError.Validation("EventEnvelope.EmptyEventType", "EventType cannot be empty."));
        }

        if (version < 1)
        {
            return Result.Failure<EventEnvelope>(
                DomainError.Validation("EventEnvelope.InvalidVersion", "Version must be greater than or equal to 1."));
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            return Result.Failure<EventEnvelope>(
                DomainError.Validation("EventEnvelope.EmptySource", "Source cannot be empty."));
        }

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Result.Failure<EventEnvelope>(
                DomainError.Validation("EventEnvelope.EmptyTenantId", "TenantId cannot be empty."));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<EventEnvelope>(
                DomainError.Validation("EventEnvelope.EmptyIdempotencyKey", "IdempotencyKey cannot be empty."));
        }

        if (string.IsNullOrWhiteSpace(correlationId))
        {
            return Result.Failure<EventEnvelope>(
                DomainError.Validation("EventEnvelope.EmptyCorrelationId", "CorrelationId cannot be empty."));
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return Result.Failure<EventEnvelope>(
                DomainError.Validation("EventEnvelope.EmptyPayload", "Payload cannot be empty."));
        }

        var meta = metadata is null
            ? new Dictionary<string, string>().AsReadOnly()
            : new Dictionary<string, string>(metadata).AsReadOnly();

        return Result.Success(new EventEnvelope(
            eventId,
            eventType.Trim(),
            version,
            source.Trim(),
            occurredAt,
            correlationId.Trim(),
            string.IsNullOrWhiteSpace(traceParent) ? null : traceParent.Trim(),
            tenantId.Trim(),
            idempotencyKey.Trim(),
            payload,
            meta));
    }
}
