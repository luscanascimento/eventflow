using EventFlow.Application.Abstractions;
using EventFlow.Application.Idempotency;
using EventFlow.Domain.Idempotency;
using EventFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace EventFlow.Infrastructure.Idempotency;

/// <summary>
/// Hybrid idempotency manager combining distributed Redis fast-guard caching
/// with PostgreSQL persistent atomic leasing and unique constraint enforcement.
/// </summary>
public sealed partial class HybridIdempotencyManager : IIdempotencyManager
{
    private const string ReleaseLockScript = @"
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('del', KEYS[1])
        else
            return 0
        end";

    private readonly IRedisConnection _redisConnection;
    private readonly EventFlowDbContext _dbContext;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly RedisOptions _options;
    private readonly ILogger<HybridIdempotencyManager> _logger;

    public HybridIdempotencyManager(
        IRedisConnection redisConnection,
        EventFlowDbContext dbContext,
        IDateTimeProvider dateTimeProvider,
        IOptions<RedisOptions> options,
        ILogger<HybridIdempotencyManager> logger)
    {
        _redisConnection = redisConnection;
        _dbContext = dbContext;
        _dateTimeProvider = dateTimeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IdempotencyAcquireResult> TryAcquireAsync(
        string tenantId,
        string idempotencyKey,
        string scope,
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        string cacheKey = BuildCacheKey(tenantId, scope, idempotencyKey);
        string lockKey = BuildLockKey(tenantId, scope, idempotencyKey);
        string lockToken = Guid.NewGuid().ToString("N");
        DateTimeOffset now = _dateTimeProvider.UtcNow;
        DateTimeOffset lockExpiry = now.AddSeconds(_options.LockTtlSeconds);

        IDatabase? redis = _redisConnection.GetDatabase();

        // 1. Fast Guard: Check Redis completion cache
        if (redis != null)
        {
            try
            {
                bool isCached = await redis.KeyExistsAsync(cacheKey);
                if (isCached)
                {
                    LogFastGuardHit(_logger, eventId, tenantId, scope, idempotencyKey);
                    return IdempotencyAcquireResult.AlreadyProcessed();
                }
            }
            catch (Exception ex)
            {
                LogRedisError(_logger, "checking cache key", ex);
            }
        }

        // 2. Distributed In-Flight Lock Acquisition in Redis (Optimization / Fast Guard)
        bool redisLockAcquired = false;
        if (redis != null)
        {
            try
            {
                redisLockAcquired = await redis.StringSetAsync(
                    key: lockKey,
                    value: lockToken,
                    expiry: TimeSpan.FromSeconds(_options.LockTtlSeconds),
                    when: When.NotExists);

                if (!redisLockAcquired)
                {
                    LogLockConflict(_logger, eventId, tenantId, scope, idempotencyKey);
                    return IdempotencyAcquireResult.ConflictInFlight();
                }
            }
            catch (Exception ex)
            {
                LogRedisError(_logger, "acquiring lock", ex);
            }
        }

        // 3. PostgreSQL Permanent Atomic Lease & Single Source of Truth
        try
        {
            var existingRecord = await _dbContext.ProcessedEvents
                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Scope == scope && p.IdempotencyKey == idempotencyKey, cancellationToken);

            if (existingRecord != null)
            {
                if (existingRecord.Status == ProcessedEventStatus.Completed)
                {
                    LogPostgresHit(_logger, eventId, tenantId, scope, idempotencyKey);

                    if (redis != null)
                    {
                        try
                        {
                            await redis.StringSetAsync(cacheKey, "1", TimeSpan.FromHours(_options.CacheTtlHours));
                            if (redisLockAcquired)
                            {
                                await ReleaseRedisLockAsync(redis, lockKey, lockToken);
                            }
                        }
                        catch (Exception ex)
                        {
                            LogRedisError(_logger, "updating cache after postgres hit", ex);
                        }
                    }

                    return IdempotencyAcquireResult.AlreadyProcessed();
                }

                if (existingRecord.Status == ProcessedEventStatus.Processing &&
                    existingRecord.LockedUntilUtc.HasValue &&
                    existingRecord.LockedUntilUtc.Value > now)
                {
                    LogLockConflict(_logger, eventId, tenantId, scope, idempotencyKey);
                    return IdempotencyAcquireResult.ConflictInFlight();
                }

                // Reclaim expired lease or failed state
                var claimResult = existingRecord.Claim(lockToken, lockExpiry, eventId, string.Empty);
                if (claimResult.IsSuccess)
                {
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    LogLockAcquired(_logger, eventId, tenantId, scope, idempotencyKey);
                    return IdempotencyAcquireResult.Acquired(lockToken);
                }
            }

            // Create initial processing record
            var newRecordResult = ProcessedEvent.CreateProcessing(
                tenantId: tenantId,
                idempotencyKey: idempotencyKey,
                scope: scope,
                eventId: eventId,
                eventType: string.Empty,
                lockToken: lockToken,
                lockedUntilUtc: lockExpiry,
                createdAt: now);

            if (newRecordResult.IsFailure)
            {
                return IdempotencyAcquireResult.ConflictInFlight();
            }

            _dbContext.ProcessedEvents.Add(newRecordResult.Value);
            await _dbContext.SaveChangesAsync(cancellationToken);

            LogLockAcquired(_logger, eventId, tenantId, scope, idempotencyKey);
            return IdempotencyAcquireResult.Acquired(lockToken);
        }
        catch (DbUpdateException ex)
        {
            LogConcurrentDbConflict(_logger, tenantId, scope, idempotencyKey, ex.Message);

            // Detach tracked entity on conflict and re-evaluate DB state
            _dbContext.ChangeTracker.Clear();

            var recordAfterConflict = await _dbContext.ProcessedEvents
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Scope == scope && p.IdempotencyKey == idempotencyKey, cancellationToken);

            if (recordAfterConflict?.Status == ProcessedEventStatus.Completed)
            {
                return IdempotencyAcquireResult.AlreadyProcessed();
            }

            return IdempotencyAcquireResult.ConflictInFlight();
        }
    }

    public async Task MarkCompletedAsync(
        string tenantId,
        string idempotencyKey,
        string scope,
        Guid eventId,
        string eventType,
        string? resultSummary,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _dateTimeProvider.UtcNow;

        var record = await _dbContext.ProcessedEvents
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Scope == scope && p.IdempotencyKey == idempotencyKey, cancellationToken);

        if (record != null)
        {
            record.MarkCompleted(record.LockToken, resultSummary, now);
        }
        else
        {
            var created = ProcessedEvent.Create(tenantId, idempotencyKey, scope, eventId, eventType, resultSummary, now);
            if (created.IsSuccess)
            {
                _dbContext.ProcessedEvents.Add(created.Value);
            }
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            LogMarkedCompletedInPostgres(_logger, eventId, tenantId, scope, idempotencyKey);
        }
        catch (DbUpdateException ex)
        {
            LogConcurrentDbConflict(_logger, tenantId, scope, idempotencyKey, ex.Message);
        }

        IDatabase? redis = _redisConnection.GetDatabase();
        if (redis != null)
        {
            string cacheKey = BuildCacheKey(tenantId, scope, idempotencyKey);
            string lockKey = BuildLockKey(tenantId, scope, idempotencyKey);

            try
            {
                await redis.StringSetAsync(cacheKey, "1", TimeSpan.FromHours(_options.CacheTtlHours));
                await redis.KeyDeleteAsync(lockKey);
            }
            catch (Exception ex)
            {
                LogRedisError(_logger, "marking completed in Redis", ex);
            }
        }
    }

    public async Task ReleaseLockAsync(
        string tenantId,
        string idempotencyKey,
        string scope,
        string? lockToken,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var record = await _dbContext.ProcessedEvents
                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Scope == scope && p.IdempotencyKey == idempotencyKey, cancellationToken);

            if (record != null && (string.IsNullOrEmpty(lockToken) || record.LockToken == lockToken))
            {
                record.MarkFailed(lockToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            LogReleaseError(_logger, tenantId, scope, idempotencyKey, ex);
        }

        if (!string.IsNullOrWhiteSpace(lockToken))
        {
            IDatabase? redis = _redisConnection.GetDatabase();
            if (redis != null)
            {
                string lockKey = BuildLockKey(tenantId, scope, idempotencyKey);
                try
                {
                    await ReleaseRedisLockAsync(redis, lockKey, lockToken);
                    LogLockReleased(_logger, tenantId, scope, idempotencyKey);
                }
                catch (Exception ex)
                {
                    LogRedisError(_logger, "releasing lock in Redis", ex);
                }
            }
        }
    }

    private static async Task ReleaseRedisLockAsync(IDatabase redis, string lockKey, string lockToken)
    {
        await redis.ScriptEvaluateAsync(
            script: ReleaseLockScript,
            keys: new RedisKey[] { lockKey },
            values: new RedisValue[] { lockToken });
    }

    private string BuildCacheKey(string tenantId, string scope, string idempotencyKey) =>
        $"{_options.KeyPrefix}completed:{tenantId}:{scope}:{idempotencyKey}";

    private string BuildLockKey(string tenantId, string scope, string idempotencyKey) =>
        $"{_options.KeyPrefix}lock:{tenantId}:{scope}:{idempotencyKey}";

    [LoggerMessage(EventId = 9101, Level = LogLevel.Debug, Message = "Idempotency Fast Guard hit in Redis for Event {EventId} (Tenant: '{TenantId}', Scope: '{Scope}', Key: '{IdempotencyKey}')")]
    private static partial void LogFastGuardHit(ILogger logger, Guid eventId, string tenantId, string scope, string idempotencyKey);

    [LoggerMessage(EventId = 9102, Level = LogLevel.Warning, Message = "In-flight lock conflict for Event {EventId} (Tenant: '{TenantId}', Scope: '{Scope}', Key: '{IdempotencyKey}')")]
    private static partial void LogLockConflict(ILogger logger, Guid eventId, string tenantId, string scope, string idempotencyKey);

    [LoggerMessage(EventId = 9103, Level = LogLevel.Debug, Message = "Idempotency hit in PostgreSQL for Event {EventId} (Tenant: '{TenantId}', Scope: '{Scope}', Key: '{IdempotencyKey}')")]
    private static partial void LogPostgresHit(ILogger logger, Guid eventId, string tenantId, string scope, string idempotencyKey);

    [LoggerMessage(EventId = 9104, Level = LogLevel.Debug, Message = "Idempotency lease acquired for Event {EventId} (Tenant: '{TenantId}', Scope: '{Scope}', Key: '{IdempotencyKey}')")]
    private static partial void LogLockAcquired(ILogger logger, Guid eventId, string tenantId, string scope, string idempotencyKey);

    [LoggerMessage(EventId = 9105, Level = LogLevel.Information, Message = "Persisted completion in PostgreSQL for Event {EventId} (Tenant: '{TenantId}', Scope: '{Scope}', Key: '{IdempotencyKey}')")]
    private static partial void LogMarkedCompletedInPostgres(ILogger logger, Guid eventId, string tenantId, string scope, string idempotencyKey);

    [LoggerMessage(EventId = 9106, Level = LogLevel.Debug, Message = "Idempotency lock released for Tenant: '{TenantId}', Scope: '{Scope}', Key: '{IdempotencyKey}'")]
    private static partial void LogLockReleased(ILogger logger, string tenantId, string scope, string idempotencyKey);

    [LoggerMessage(EventId = 9107, Level = LogLevel.Warning, Message = "Non-fatal Redis exception occurred during {Operation}. Falling back to PostgreSQL.")]
    private static partial void LogRedisError(ILogger logger, string operation, Exception ex);

    [LoggerMessage(EventId = 9108, Level = LogLevel.Warning, Message = "Concurrent database conflict on idempotency key for Tenant: '{TenantId}', Scope: '{Scope}', Key: '{IdempotencyKey}': {Message}")]
    private static partial void LogConcurrentDbConflict(ILogger logger, string tenantId, string scope, string idempotencyKey, string message);

    [LoggerMessage(EventId = 9109, Level = LogLevel.Warning, Message = "Error occurred while releasing idempotency lock for Tenant: '{TenantId}', Scope: '{Scope}', Key: '{IdempotencyKey}'")]
    private static partial void LogReleaseError(ILogger logger, string tenantId, string scope, string idempotencyKey, Exception ex);
}
