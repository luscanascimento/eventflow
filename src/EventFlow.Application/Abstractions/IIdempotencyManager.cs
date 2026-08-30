using EventFlow.Application.Idempotency;

namespace EventFlow.Application.Abstractions;

/// <summary>
/// Orchestrates distributed idempotency protection combining fast memory locking with persistent relational storage.
/// </summary>
public interface IIdempotencyManager
{
    /// <summary>
    /// Checks whether the event was already processed and attempts to acquire an exclusive in-flight lock.
    /// </summary>
    Task<IdempotencyAcquireResult> TryAcquireAsync(
        string tenantId,
        string idempotencyKey,
        string scope,
        Guid eventId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists completion of event processing and sets long-term cache.
    /// </summary>
    Task MarkCompletedAsync(
        string tenantId,
        string idempotencyKey,
        string scope,
        Guid eventId,
        string eventType,
        string? resultSummary,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases an in-flight lock after execution failure so that retries may proceed.
    /// </summary>
    Task ReleaseLockAsync(
        string tenantId,
        string idempotencyKey,
        string scope,
        string? lockToken,
        CancellationToken cancellationToken = default);
}
