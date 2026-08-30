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
/// Hybrid idempotency manager combining distributed Redis locking (fast in-flight guard)
/// with PostgreSQL persistent storage (immutable single source of truth).
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

        // 2. Distributed In-Flight Lock Acquisition
        bool lockAcquired = false;
        if (redis != null)
        {
            try
            {
                lockAcquired = await redis.StringSetAsync(
                    key: lockKey,
                    value: lockToken,
                    expiry: TimeSpan.FromSeconds(_options.LockTtlSeconds),
                    when: When.NotExists);

                if (!lockAcquired)
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

        // 3. PostgreSQL Permanent Verification (Single Source of Truth)
        bool alreadyInPostgres = await _dbContext.ProcessedEvents
            .AsNoTracking()
            .AnyAsync(p => p.TenantId == tenantId && p.Scope == scope && p.IdempotencyKey == idempotencyKey, cancellationToken);

        if (alreadyInPostgres)
        {
            LogPostgresHit(_logger, eventId, tenantId, scope, idempotencyKey);

            // Populate Redis cache to avoid future database queries
            if (redis != null)
            {
                try
                {
                    await redis.StringSetAsync(cacheKey, "1", TimeSpan.FromHours(_options.CacheTtlHours));
                    if (lockAcquired)
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

        LogLockAcquired(_logger, eventId, tenantId, scope, idempotencyKey);
        return IdempotencyAcquireResult.Acquired(lockToken);
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

        var processedEventResult = ProcessedEvent.Create(
            tenantId: tenantId,
            idempotencyKey: idempotencyKey,
            scope: scope,
            eventId: eventId,
            eventType: eventType,
            resultSummary: resultSummary,
            processedAt: now);

        if (processedEventResult.IsSuccess)
        {
            _dbContext.ProcessedEvents.Add(processedEventResult.Value);
            await _dbContext.SaveChangesAsync(cancellationToken);
            LogMarkedCompletedInPostgres(_logger, eventId, tenantId, scope, idempotencyKey);
        }

        IDatabase? redis = _redisConnection.GetDatabase();
        if (redis != null)
        {
            string cacheKey = BuildCacheKey(tenantId, scope, idempotencyKey);
            string lockKey = BuildLockKey(tenantId, scope, idempotencyKey);

            try
            {
                // Cache completion for fast subsequent guards
                await redis.StringSetAsync(cacheKey, "1", TimeSpan.FromHours(_options.CacheTtlHours));
                // Delete lock
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
        if (string.IsNullOrWhiteSpace(lockToken))
        {
            return;
        }

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
                LogRedisError(_logger, "releasing lock", ex);
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

    [LoggerMessage(EventId = 9102, Level = LogLevel.Warning, Message = "In-flight lock conflict in Redis for Event {EventId} (Tenant: '{TenantId}', Scope: '{Scope}', Key: '{IdempotencyKey}')")]
    private static partial void LogLockConflict(ILogger logger, Guid eventId, string tenantId, string scope, string idempotencyKey);

    [LoggerMessage(EventId = 9103, Level = LogLevel.Debug, Message = "Idempotency hit in PostgreSQL for Event {EventId} (Tenant: '{TenantId}', Scope: '{Scope}', Key: '{IdempotencyKey}')")]
    private static partial void LogPostgresHit(ILogger logger, Guid eventId, string tenantId, string scope, string idempotencyKey);

    [LoggerMessage(EventId = 9104, Level = LogLevel.Debug, Message = "Idempotency lock acquired for Event {EventId} (Tenant: '{TenantId}', Scope: '{Scope}', Key: '{IdempotencyKey}')")]
    private static partial void LogLockAcquired(ILogger logger, Guid eventId, string tenantId, string scope, string idempotencyKey);

    [LoggerMessage(EventId = 9105, Level = LogLevel.Information, Message = "Persisted completion in PostgreSQL for Event {EventId} (Tenant: '{TenantId}', Scope: '{Scope}', Key: '{IdempotencyKey}')")]
    private static partial void LogMarkedCompletedInPostgres(ILogger logger, Guid eventId, string tenantId, string scope, string idempotencyKey);

    [LoggerMessage(EventId = 9106, Level = LogLevel.Debug, Message = "Idempotency lock released for Tenant: '{TenantId}', Scope: '{Scope}', Key: '{IdempotencyKey}'")]
    private static partial void LogLockReleased(ILogger logger, string tenantId, string scope, string idempotencyKey);

    [LoggerMessage(EventId = 9107, Level = LogLevel.Warning, Message = "Non-fatal Redis exception occurred during {Operation}. Falling back to PostgreSQL.")]
    private static partial void LogRedisError(ILogger logger, string operation, Exception ex);
}
