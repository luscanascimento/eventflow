using EventFlow.Domain.Common;

namespace EventFlow.Domain.Idempotency;

/// <summary>
/// Immutable record of an event that has been successfully processed by a specific consumer scope,
/// ensuring permanent logical exactly-once execution.
/// </summary>
public sealed class ProcessedEvent
{
    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string Scope { get; private set; } = string.Empty;
    public Guid EventId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string? ResultSummary { get; private set; }
    public DateTimeOffset ProcessedAt { get; private set; }

    // Required for EF Core
    private ProcessedEvent() { }

    private ProcessedEvent(
        Guid id,
        string tenantId,
        string idempotencyKey,
        string scope,
        Guid eventId,
        string eventType,
        string? resultSummary,
        DateTimeOffset processedAt)
    {
        Id = id;
        TenantId = tenantId;
        IdempotencyKey = idempotencyKey;
        Scope = scope;
        EventId = eventId;
        EventType = eventType;
        ResultSummary = resultSummary;
        ProcessedAt = processedAt;
    }

    public static Result<ProcessedEvent> Create(
        string tenantId,
        string idempotencyKey,
        string scope,
        Guid eventId,
        string eventType,
        string? resultSummary,
        DateTimeOffset processedAt)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Result.Failure<ProcessedEvent>(
                DomainError.Validation("ProcessedEvent.EmptyTenantId", "TenantId cannot be empty."));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure<ProcessedEvent>(
                DomainError.Validation("ProcessedEvent.EmptyIdempotencyKey", "IdempotencyKey cannot be empty."));
        }

        if (string.IsNullOrWhiteSpace(scope))
        {
            return Result.Failure<ProcessedEvent>(
                DomainError.Validation("ProcessedEvent.EmptyScope", "Scope cannot be empty."));
        }

        if (eventId == Guid.Empty)
        {
            return Result.Failure<ProcessedEvent>(
                DomainError.Validation("ProcessedEvent.InvalidEventId", "EventId must not be an empty GUID."));
        }

        if (string.IsNullOrWhiteSpace(eventType))
        {
            return Result.Failure<ProcessedEvent>(
                DomainError.Validation("ProcessedEvent.EmptyEventType", "EventType cannot be empty."));
        }

        return Result.Success(new ProcessedEvent(
            id: Guid.NewGuid(),
            tenantId: tenantId.Trim(),
            idempotencyKey: idempotencyKey.Trim(),
            scope: scope.Trim(),
            eventId: eventId,
            eventType: eventType.Trim(),
            resultSummary: resultSummary?.Trim(),
            processedAt: processedAt));
    }
}
