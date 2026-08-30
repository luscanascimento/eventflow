namespace EventFlow.Application.Idempotency;

/// <summary>
/// Status of an idempotency lock acquisition attempt.
/// </summary>
public enum IdempotencyAcquireStatus
{
    /// <summary>
    /// Lock was successfully acquired; worker is clear to execute the handler.
    /// </summary>
    Acquired,

    /// <summary>
    /// The event has already been successfully processed by this scope; skip execution safely.
    /// </summary>
    AlreadyProcessed,

    /// <summary>
    /// Another worker currently holds the in-flight processing lock for this key.
    /// </summary>
    ConflictInFlight
}
