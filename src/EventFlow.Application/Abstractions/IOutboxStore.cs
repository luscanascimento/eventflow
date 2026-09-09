using EventFlow.Domain.Common;
using EventFlow.Domain.Outbox;

namespace EventFlow.Application.Abstractions;

/// <summary>
/// Abstraction for querying, leasing, and updating outbox messages with atomic concurrency control.
/// </summary>
public interface IOutboxStore
{
    /// <summary>
    /// Atomically claims a batch of pending or expired-lease outbox messages, transitioning their status
    /// to 'Processing' and assigning the worker's lock token and lease duration before returning.
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimPendingBatchAsync(
        int batchSize,
        string lockToken,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches pending messages for backward compatibility or simple polling.
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> FetchPendingBatchAsync(
        int batchSize,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an outbox message as Published, releasing the lease lock token.
    /// </summary>
    Task<Result> MarkPublishedAsync(
        Guid id,
        string lockToken,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a publication failure and schedules retry or DLQ transition, releasing the lease lock token.
    /// </summary>
    Task<Result> MarkFailedAsync(
        Guid id,
        string lockToken,
        string errorMessage,
        int maxRetries,
        TimeSpan retryBackoff,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
