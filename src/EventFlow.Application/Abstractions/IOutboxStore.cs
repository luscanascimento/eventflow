using EventFlow.Domain.Outbox;

namespace EventFlow.Application.Abstractions;

/// <summary>
/// Abstraction for querying and updating outbox messages with concurrency control.
/// </summary>
public interface IOutboxStore
{
    Task<IReadOnlyList<OutboxMessage>> FetchPendingBatchAsync(
        int batchSize,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
