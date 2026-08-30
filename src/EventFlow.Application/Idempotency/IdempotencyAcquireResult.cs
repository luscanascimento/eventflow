namespace EventFlow.Application.Idempotency;

/// <summary>
/// Result of evaluating idempotency before executing a domain event handler.
/// </summary>
public sealed record IdempotencyAcquireResult(
    IdempotencyAcquireStatus Status,
    string? LockToken = null)
{
    public bool IsAcquired => Status == IdempotencyAcquireStatus.Acquired;
    public bool IsAlreadyProcessed => Status == IdempotencyAcquireStatus.AlreadyProcessed;
    public bool IsConflictInFlight => Status == IdempotencyAcquireStatus.ConflictInFlight;

    public static IdempotencyAcquireResult Acquired(string lockToken) =>
        new(IdempotencyAcquireStatus.Acquired, lockToken);

    public static IdempotencyAcquireResult AlreadyProcessed() =>
        new(IdempotencyAcquireStatus.AlreadyProcessed);

    public static IdempotencyAcquireResult ConflictInFlight() =>
        new(IdempotencyAcquireStatus.ConflictInFlight);
}
