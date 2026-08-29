using System.Collections.Concurrent;
using EventFlow.Application.Abstractions;
using EventFlow.Domain.Common;
using EventFlow.Domain.Events;
using EventFlow.Domain.Outbox;

namespace EventFlow.Api.Infrastructure;

/// <summary>
/// Phase 1 in-memory implementation of <see cref="IEventStore"/> for initial API testing and prototyping
/// prior to Phase 2 EF Core & PostgreSQL implementation.
/// </summary>
public sealed class InMemoryEventStore : IEventStore
{
    private readonly ConcurrentDictionary<string, EventRecord> _eventsByIdempotencyKey = new();
    private readonly ConcurrentBag<OutboxMessage> _outboxMessages = new();

    public Task<Result> SaveEventAndOutboxAsync(
        EventRecord eventRecord,
        OutboxMessage outboxMessage,
        CancellationToken cancellationToken = default)
    {
        string key = BuildCompositeKey(eventRecord.TenantId, eventRecord.IdempotencyKey);

        if (!_eventsByIdempotencyKey.TryAdd(key, eventRecord))
        {
            return Task.FromResult(Result.Failure(
                DomainError.Conflict(
                    "Event.DuplicateIdempotencyKey",
                    $"Event with idempotency key '{eventRecord.IdempotencyKey}' already exists for tenant '{eventRecord.TenantId}'.")));
        }

        _outboxMessages.Add(outboxMessage);
        return Task.FromResult(Result.Success());
    }

    public Task<bool> ExistsByIdempotencyKeyAsync(
        string tenantId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        string key = BuildCompositeKey(tenantId, idempotencyKey);
        return Task.FromResult(_eventsByIdempotencyKey.ContainsKey(key));
    }

    private static string BuildCompositeKey(string tenantId, string idempotencyKey) =>
        $"{tenantId}:{idempotencyKey}";
}
