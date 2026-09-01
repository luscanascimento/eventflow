using System.Collections.Concurrent;
using EventFlow.Application.Abstractions;
using EventFlow.Application.Events.Consumers;
using EventFlow.Application.Idempotency;
using EventFlow.Domain.Common;
using EventFlow.Domain.Idempotency;
using EventFlow.Infrastructure.Idempotency;
using EventFlow.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using Xunit;

namespace EventFlow.UnitTests.Chaos;

/// <summary>
/// Chaos and high-concurrency stress tests verifying that the Hybrid Idempotency architecture
/// upholds exact-once logical processing semantics under severe race conditions, Redis partitions,
/// and concurrent worker contention.
/// </summary>
public sealed class ConcurrencyChaosTests : IDisposable
{
    private readonly SqliteConnection _sqliteConnection;
    private readonly EventFlowDbContext _dbContext;
    private readonly IRedisConnection _redisConnection = Substitute.For<IRedisConnection>();
    private readonly IDatabase _redisDb = Substitute.For<IDatabase>();
    private readonly IDateTimeProvider _dateTimeProvider = Substitute.For<IDateTimeProvider>();
    private readonly ILogger<HybridIdempotencyManager> _idempotencyLogger = Substitute.For<ILogger<HybridIdempotencyManager>>();
    private readonly ILogger<EventProcessor> _processorLogger = Substitute.For<ILogger<EventProcessor>>();

    private readonly RedisOptions _redisOptions = new()
    {
        Enabled = true,
        KeyPrefix = "eventflow:",
        LockTtlSeconds = 30,
        CacheTtlHours = 24
    };

    public ConcurrencyChaosTests()
    {
        _sqliteConnection = new SqliteConnection("Filename=:memory:");
        _sqliteConnection.Open();

        var dbOptions = new DbContextOptionsBuilder<EventFlowDbContext>()
            .UseSqlite(_sqliteConnection)
            .Options;

        _dbContext = new EventFlowDbContext(dbOptions);
        _dbContext.Database.EnsureCreated();

        _dateTimeProvider.UtcNow.Returns(DateTimeOffset.UtcNow);
        _redisConnection.GetDatabase().Returns(_redisDb);
    }

    [Fact]
    public async Task Chaos_HighConcurrencyRaceCondition_Simulate50ParallelRequests_ExactlyOneAcquiresLock()
    {
        // ARRANGE: In-memory atomic state simulator for Redis SET NX EX
        var redisLockStore = new ConcurrentDictionary<string, string>();
        var redisCompletedStore = new ConcurrentDictionary<string, byte>();

        _redisDb.KeyExistsAsync(Arg.Any<RedisKey>()).Returns(callInfo =>
        {
            var key = callInfo.Arg<RedisKey>().ToString();
            return Task.FromResult(redisCompletedStore.ContainsKey(key));
        });

        _redisDb.StringSetAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(),
            Arg.Is<When>(w => w == When.NotExists)).Returns(callInfo =>
        {
            var key = callInfo.Arg<RedisKey>().ToString();
            var val = callInfo.Arg<RedisValue>().ToString();
            bool added = redisLockStore.TryAdd(key, val);
            return Task.FromResult(added);
        });

        var sut = new HybridIdempotencyManager(
            _redisConnection,
            _dbContext,
            _dateTimeProvider,
            Options.Create(_redisOptions),
            _idempotencyLogger);

        const int totalTasks = 50;
        const string tenantId = "tenant-chaos";
        const string idempotencyKey = "tx-race-50-threads";
        const string scope = "PaymentSettledHandler";
        var eventId = Guid.NewGuid();

        var results = new ConcurrentBag<IdempotencyAcquireResult>();

        // ACT: Fire 50 concurrent tasks simultaneously
        await Parallel.ForEachAsync(Enumerable.Range(0, totalTasks), async (_, ct) =>
        {
            // Inject minor random jitter to maximize thread interleaving
            await Task.Yield();
            var res = await sut.TryAcquireAsync(tenantId, idempotencyKey, scope, eventId, ct);
            results.Add(res);
        });

        // ASSERT: Exactly 1 must have acquired the lock, and 49 must be conflict in-flight
        int acquiredCount = results.Count(r => r.IsAcquired);
        int conflictCount = results.Count(r => r.IsConflictInFlight);

        acquiredCount.Should().Be(1, "Only one worker thread can successfully acquire the in-flight distributed lock");
        conflictCount.Should().Be(totalTasks - 1, "All concurrent overlapping requests must receive ConflictInFlight");
    }

    [Fact]
    public async Task Chaos_RedisSuddenCrash_GracefulFallbackToPostgreSql_AllowsProcessingWithoutDataLoss()
    {
        // ARRANGE: Redis throws catastrophic connection and timeout exceptions
        _redisDb.KeyExistsAsync(Arg.Any<RedisKey>())
            .ThrowsAsync(new RedisException("Redis node unavailable / network partition"));
        _redisDb.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .ThrowsAsync(new RedisException("Redis socket dropped"));

        var sut = new HybridIdempotencyManager(
            _redisConnection,
            _dbContext,
            _dateTimeProvider,
            Options.Create(_redisOptions),
            _idempotencyLogger);

        // ACT: Attempt to acquire lock during Redis outage
        var acquireResult = await sut.TryAcquireAsync("tenant-chaos", "key-redis-down", "ScopeA", Guid.NewGuid());

        // ASSERT: System degrades gracefully to PostgreSQL and allows the new event to proceed
        acquireResult.IsAcquired.Should().BeTrue("System must gracefully fallback to PostgreSQL without throwing or blocking operations");

        // ACT: Complete the event in PostgreSQL
        await sut.MarkCompletedAsync("tenant-chaos", "key-redis-down", "ScopeA", Guid.NewGuid(), "payment.settled.v1", "Approved");

        // ACT: Subsequent acquire for the same event during Redis outage
        var secondAcquire = await sut.TryAcquireAsync("tenant-chaos", "key-redis-down", "ScopeA", Guid.NewGuid());

        // ASSERT: Second acquire detects previous record in PostgreSQL and returns AlreadyProcessed
        secondAcquire.IsAlreadyProcessed.Should().BeTrue("PostgreSQL fallback must enforce Single Source of Truth even with Redis completely offline");
    }

    [Fact]
    public async Task Chaos_LockTokenStealingDefense_WorkerCannotReleaseAnotherWorkersLock()
    {
        // ARRANGE: Verify that Lua script prevents accidental release of renewed/stolen locks
        var sut = new HybridIdempotencyManager(
            _redisConnection,
            _dbContext,
            _dateTimeProvider,
            Options.Create(_redisOptions),
            _idempotencyLogger);

        // ACT: Worker A attempts to release with token "token-worker-A"
        await sut.ReleaseLockAsync("tenant-x", "key-y", "scope-z", "token-worker-A");

        // ASSERT: Script evaluation must pass exact token to Lua script for atomic comparison
        await _redisDb.Received(1).ScriptEvaluateAsync(
            Arg.Is<string>(script => script.Contains("KEYS[1]") && script.Contains("ARGV[1]")),
            Arg.Is<RedisKey[]>(keys => keys.Length == 1 && keys[0] == "eventflow:lock:tenant-x:scope-z:key-y"),
            Arg.Is<RedisValue[]>(values => values.Length == 1 && values[0] == "token-worker-A"));
    }

    [Fact]
    public async Task Chaos_EventProcessor_WhenHandlerThrowsUnexpectedException_LockIsSafelyReleased()
    {
        // ARRANGE: EventProcessor orchestrator with failing handler
        var handler = Substitute.For<IEventConsumerHandler>();
        handler.CanHandle("payment.settled.v1").Returns(true);
        handler.HandleAsync(Arg.Any<ConsumedEventContext>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Simulated worker resource spike"));

        var idempotencyManager = Substitute.For<IIdempotencyManager>();
        idempotencyManager.TryAcquireAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(IdempotencyAcquireResult.Acquired("token-chaos-999"));

        var sut = new EventProcessor(new[] { handler }, idempotencyManager, _processorLogger);

        var context = new ConsumedEventContext(
            EventId: Guid.NewGuid(),
            EventType: "payment.settled.v1",
            TenantId: "tenant-chaos",
            TraceParent: null,
            OutboxId: null,
            RoutingKey: "payment.settled.v1",
            Payload: "{}",
            ConsumedAt: DateTimeOffset.UtcNow,
            Headers: new Dictionary<string, string> { ["idempotency_key"] = "chaos-key-1" },
            Redelivered: false);

        // ACT
        var result = await sut.ProcessAsync(context);

        // ASSERT
        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EventProcessor.UnhandledException");

        // Verify the lock was explicitly and safely released so future retries can succeed
        await idempotencyManager.Received(1).ReleaseLockAsync(
            "tenant-chaos",
            "chaos-key-1",
            handler.GetType().Name,
            "token-chaos-999",
            Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _sqliteConnection.Dispose();
    }
}
