using EventFlow.Domain.Common;
using EventFlow.Domain.Events;
using EventFlow.Domain.Outbox;

namespace EventFlow.Application.Abstractions;

/// <summary>
/// Transactional persistence boundary for events and outbox messages.
/// </summary>
public interface IEventStore
{
    Task<Result> SaveEventAndOutboxAsync(
        EventRecord eventRecord,
        OutboxMessage outboxMessage,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsByIdempotencyKeyAsync(
        string tenantId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);
}
