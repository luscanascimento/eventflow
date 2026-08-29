using EventFlow.Application.Abstractions;
using EventFlow.Domain.Outbox;
using Microsoft.EntityFrameworkCore;

namespace EventFlow.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL / EF Core implementation of <see cref="IOutboxStore"/> with support for
/// 'SELECT ... FOR UPDATE SKIP LOCKED' to enable conflict-free concurrent outbox worker replicas.
/// </summary>
public sealed class EfOutboxStore : IOutboxStore
{
    private readonly EventFlowDbContext _context;

    public EfOutboxStore(EventFlowDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<OutboxMessage>> FetchPendingBatchAsync(
        int batchSize,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (_context.Database.IsNpgsql())
        {
            // PostgreSQL-specific concurrency lock: SKIP LOCKED skips rows already locked by other worker instances
            return await _context.OutboxMessages
                .FromSqlInterpolated($@"
                    SELECT * FROM outbox_messages
                    WHERE status = 'Pending'
                      AND (next_retry_at IS NULL OR next_retry_at <= {now})
                    ORDER BY created_at ASC
                    LIMIT {batchSize}
                    FOR UPDATE SKIP LOCKED")
                .ToListAsync(cancellationToken);
        }

        // Fallback for non-PostgreSQL providers (e.g. SQLite in unit tests)
        var pending = await _context.OutboxMessages
            .Where(o => o.Status == OutboxStatus.Pending)
            .ToListAsync(cancellationToken);

        return pending
            .Where(o => o.NextRetryAt == null || o.NextRetryAt <= now)
            .OrderBy(o => o.CreatedAt)
            .Take(batchSize)
            .ToList();
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await _context.SaveChangesAsync(cancellationToken);
    }
}
