using EventFlow.Application.Abstractions;
using EventFlow.Domain.Common;
using EventFlow.Domain.Outbox;
using Microsoft.EntityFrameworkCore;

namespace EventFlow.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL / EF Core implementation of <see cref="IOutboxStore"/> with support for
/// atomic lease acquisition ('SELECT ... FOR UPDATE SKIP LOCKED') and optimistic concurrency tokens.
/// </summary>
public sealed class EfOutboxStore : IOutboxStore
{
    private readonly EventFlowDbContext _context;

    public EfOutboxStore(EventFlowDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimPendingBatchAsync(
        int batchSize,
        string lockToken,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset lockedUntilUtc = now.Add(leaseDuration);

        if (_context.Database.IsNpgsql())
        {
            // PostgreSQL atomic claim query: locks rows with SKIP LOCKED, updates status and lease token,
            // and returns the claimed rows without requiring long-lived open transactions.
            return await _context.OutboxMessages
                .FromSqlInterpolated($@"
                    WITH pending_batch AS (
                        SELECT id FROM outbox_messages
                        WHERE (status = 'Pending' AND (next_retry_at IS NULL OR next_retry_at <= {now}))
                           OR (status = 'Processing' AND locked_until_utc IS NOT NULL AND locked_until_utc < {now})
                        ORDER BY created_at ASC
                        LIMIT {batchSize}
                        FOR UPDATE SKIP LOCKED
                    )
                    UPDATE outbox_messages m
                    SET status = 'Processing',
                        lock_token = {lockToken},
                        locked_until_utc = {lockedUntilUtc}
                    FROM pending_batch pb
                    WHERE m.id = pb.id
                    RETURNING m.id, m.event_id, m.tenant_id, m.event_type, m.routing_key, m.payload, m.trace_parent, m.status, m.lock_token, m.locked_until_utc, m.created_at, m.processed_at, m.next_retry_at, m.retry_count, m.last_error")
                .ToListAsync(cancellationToken);
        }

        // Fallback for non-PostgreSQL providers (e.g. SQLite in unit / integration tests)
        var allPendingOrProcessing = await _context.OutboxMessages
            .Where(o => o.Status == OutboxStatus.Pending || o.Status == OutboxStatus.Processing)
            .ToListAsync(cancellationToken);

        var eligible = allPendingOrProcessing
            .Where(o => (o.Status == OutboxStatus.Pending && (o.NextRetryAt == null || o.NextRetryAt <= now))
                     || (o.Status == OutboxStatus.Processing && o.LockedUntilUtc != null && o.LockedUntilUtc < now))
            .OrderBy(o => o.CreatedAt)
            .Take(batchSize)
            .ToList();

        foreach (var message in eligible)
        {
            message.Claim(lockToken, lockedUntilUtc);
        }

        if (eligible.Count > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
        }

        return eligible;
    }

    public async Task<IReadOnlyList<OutboxMessage>> FetchPendingBatchAsync(
        int batchSize,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        string lockToken = Guid.NewGuid().ToString("N");
        return await ClaimPendingBatchAsync(batchSize, lockToken, TimeSpan.FromSeconds(30), now, cancellationToken);
    }

    public async Task<Result> MarkPublishedAsync(
        Guid id,
        string lockToken,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default)
    {
        var message = await _context.OutboxMessages.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (message == null)
        {
            return Result.Failure(DomainError.NotFound("OutboxMessage.NotFound", $"Outbox message with id '{id}' was not found."));
        }

        var result = message.MarkPublished(lockToken, publishedAt);
        if (result.IsFailure)
        {
            return result;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> MarkFailedAsync(
        Guid id,
        string lockToken,
        string errorMessage,
        int maxRetries,
        TimeSpan retryBackoff,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var message = await _context.OutboxMessages.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (message == null)
        {
            return Result.Failure(DomainError.NotFound("OutboxMessage.NotFound", $"Outbox message with id '{id}' was not found."));
        }

        var result = message.MarkFailed(lockToken, errorMessage, maxRetries, retryBackoff, now);
        if (result.IsFailure)
        {
            return result;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _context.SaveChangesAsync(cancellationToken);
    }
}
