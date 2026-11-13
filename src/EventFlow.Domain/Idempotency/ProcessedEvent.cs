using EventFlow.Domain.Common;

namespace EventFlow.Domain.Idempotency;

/// <summary>
/// Record of an event processing lifecycle by a specific consumer scope,
/// enforcing storage-level concurrency leasing and permanent idempotency guarantees.
/// </summary>
public sealed class ProcessedEvent
{
    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string Scope { get; private set; } = string.Empty;
    public Guid EventId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public ProcessedEventStatus Status { get; private set; }
    public string? LockToken { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }
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
        ProcessedEventStatus status,
        string? lockToken,
        DateTimeOffset? lockedUntilUtc,
        string? resultSummary,
        DateTimeOffset processedAt)
    {
        Id = id;
        TenantId = tenantId;
        IdempotencyKey = idempotencyKey;
        Scope = scope;
        EventId = eventId;
        EventType = eventType;
        Status = status;
        LockToken = lockToken;
        LockedUntilUtc = lockedUntilUtc;
        ResultSummary = resultSummary;
        ProcessedAt = processedAt;
    }

    public static Result<ProcessedEvent> CreateProcessing(
        string tenantId,
        string idempotencyKey,
        string scope,
        Guid eventId,
        string? eventType,
        string lockToken,
        DateTimeOffset lockedUntilUtc,
        DateTimeOffset createdAt)
    {
        var validation = Validate(tenantId, idempotencyKey, scope, eventId);
        if (validation.IsFailure)
        {
            return Result.Failure<ProcessedEvent>(validation.Error);
        }

        if (string.IsNullOrWhiteSpace(lockToken))
        {
            return Result.Failure<ProcessedEvent>(
                DomainError.Validation("ProcessedEvent.EmptyLockToken", "LockToken cannot be empty."));
        }

        return Result.Success(new ProcessedEvent(
            id: Guid.NewGuid(),
            tenantId: tenantId.Trim(),
            idempotencyKey: idempotencyKey.Trim(),
            scope: scope.Trim(),
            eventId: eventId,
            eventType: eventType?.Trim() ?? string.Empty,
            status: ProcessedEventStatus.Processing,
            lockToken: lockToken,
            lockedUntilUtc: lockedUntilUtc,
            resultSummary: null,
            processedAt: createdAt));
    }

    public static Result<ProcessedEvent> Create(
        string tenantId,
        string idempotencyKey,
        string scope,
        Guid eventId,
        string? eventType,
        string? resultSummary,
        DateTimeOffset processedAt)
    {
        var validation = Validate(tenantId, idempotencyKey, scope, eventId);
        if (validation.IsFailure)
        {
            return Result.Failure<ProcessedEvent>(validation.Error);
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
            status: ProcessedEventStatus.Completed,
            lockToken: null,
            lockedUntilUtc: null,
            resultSummary: resultSummary?.Trim(),
            processedAt: processedAt));
    }

    public Result Claim(string lockToken, DateTimeOffset lockedUntilUtc, Guid eventId, string? eventType)
    {
        if (Status == ProcessedEventStatus.Completed)
        {
            return Result.Failure(DomainError.Conflict("ProcessedEvent.AlreadyCompleted", "Cannot claim an already completed idempotency record."));
        }

        if (string.IsNullOrWhiteSpace(lockToken))
        {
            return Result.Failure(DomainError.Validation("ProcessedEvent.EmptyLockToken", "LockToken cannot be empty."));
        }

        if (eventId == Guid.Empty)
        {
            return Result.Failure(DomainError.Validation("ProcessedEvent.InvalidEventId", "EventId must not be an empty GUID."));
        }

        Status = ProcessedEventStatus.Processing;
        LockToken = lockToken;
        LockedUntilUtc = lockedUntilUtc;
        EventId = eventId;
        if (!string.IsNullOrWhiteSpace(eventType))
        {
            EventType = eventType.Trim();
        }

        return Result.Success();
    }

    public Result MarkCompleted(string? lockToken, string? resultSummary, DateTimeOffset completedAt, string? eventType = null)
    {
        if (Status == ProcessedEventStatus.Completed)
        {
            return Result.Failure(DomainError.Conflict("ProcessedEvent.AlreadyCompleted", "Idempotency record is already marked as completed."));
        }

        if (!string.IsNullOrEmpty(LockToken) && !string.Equals(LockToken, lockToken, StringComparison.Ordinal))
        {
            return Result.Failure(DomainError.Conflict("ProcessedEvent.LockTokenMismatch", "Lock token mismatch; cannot mark completed."));
        }

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            EventType = eventType.Trim();
        }

        Status = ProcessedEventStatus.Completed;
        ResultSummary = resultSummary?.Trim();
        ProcessedAt = completedAt;
        LockToken = null;
        LockedUntilUtc = null;
        return Result.Success();
    }

    public Result MarkFailed(string? lockToken)
    {
        if (Status == ProcessedEventStatus.Completed)
        {
            return Result.Failure(DomainError.Conflict("ProcessedEvent.AlreadyCompleted", "Cannot mark an already completed idempotency record as failed."));
        }

        if (!string.IsNullOrEmpty(LockToken) && !string.Equals(LockToken, lockToken, StringComparison.Ordinal))
        {
            return Result.Failure(DomainError.Conflict("ProcessedEvent.LockTokenMismatch", "Lock token mismatch; cannot mark failed."));
        }

        Status = ProcessedEventStatus.Failed;
        LockToken = null;
        LockedUntilUtc = null;
        return Result.Success();
    }

    private static Result Validate(string tenantId, string idempotencyKey, string scope, Guid eventId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return Result.Failure(DomainError.Validation("ProcessedEvent.EmptyTenantId", "TenantId cannot be empty."));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Result.Failure(DomainError.Validation("ProcessedEvent.EmptyIdempotencyKey", "IdempotencyKey cannot be empty."));
        }

        if (string.IsNullOrWhiteSpace(scope))
        {
            return Result.Failure(DomainError.Validation("ProcessedEvent.EmptyScope", "Scope cannot be empty."));
        }

        if (eventId == Guid.Empty)
        {
            return Result.Failure(DomainError.Validation("ProcessedEvent.InvalidEventId", "EventId must not be an empty GUID."));
        }

        return Result.Success();
    }
}
