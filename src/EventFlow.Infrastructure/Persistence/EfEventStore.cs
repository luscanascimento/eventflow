using EventFlow.Application.Abstractions;
using EventFlow.Domain.Common;
using EventFlow.Domain.Events;
using EventFlow.Domain.Outbox;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EventFlow.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL / EF Core implementation of <see cref="IEventStore"/> providing ACID transactional
/// persistence for business event records and their corresponding outbox messages.
/// </summary>
public sealed class EfEventStore : IEventStore
{
    private readonly EventFlowDbContext _context;

    public EfEventStore(EventFlowDbContext context)
    {
        _context = context;
    }

    public async Task<Result> SaveEventAndOutboxAsync(
        EventRecord eventRecord,
        OutboxMessage outboxMessage,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _context.Events.AddAsync(eventRecord, cancellationToken);
            await _context.OutboxMessages.AddAsync(outboxMessage, cancellationToken);

            await _context.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            _context.ChangeTracker.Clear();
            return Result.Failure(
                DomainError.Conflict(
                    "Event.DuplicateIdempotencyKey",
                    $"An event with idempotency key '{eventRecord.IdempotencyKey}' already exists for tenant '{eventRecord.TenantId}'."));
        }
        catch (Exception ex)
        {
            _context.ChangeTracker.Clear();
            return Result.Failure(
                DomainError.Failure("Database.SaveFailure", $"Failed to save event and outbox message: {ex.Message}"));
        }
    }

    public async Task<bool> ExistsByIdempotencyKeyAsync(
        string tenantId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        return await _context.Events
            .AsNoTracking()
            .AnyAsync(e => e.TenantId == tenantId && e.IdempotencyKey == idempotencyKey, cancellationToken);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        if (ex.InnerException is PostgresException pgEx && pgEx.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return true;
        }

        // Database-agnostic check for unique constraint violations across drivers (PostgreSQL, SQLite, etc.)
        if (ex.InnerException != null &&
            (ex.InnerException.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) ||
             ex.InnerException.Message.Contains("duplicate key value", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }
}
