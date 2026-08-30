using EventFlow.Application.Abstractions;
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

namespace EventFlow.UnitTests.Infrastructure;

public sealed class HybridIdempotencyManagerTests : IDisposable
{
    private readonly SqliteConnection _sqliteConnection;
    private readonly EventFlowDbContext _dbContext;
    private readonly IRedisConnection _redisConnection = Substitute.For<IRedisConnection>();
    private readonly IDatabase _redisDb = Substitute.For<IDatabase>();
    private readonly IDateTimeProvider _dateTimeProvider = Substitute.For<IDateTimeProvider>();
    private readonly ILogger<HybridIdempotencyManager> _logger = Substitute.For<ILogger<HybridIdempotencyManager>>();
    private readonly RedisOptions _options = new()
    {
        Enabled = true,
        KeyPrefix = "eventflow:",
        LockTtlSeconds = 30,
        CacheTtlHours = 24
    };
    private readonly HybridIdempotencyManager _sut;

    public HybridIdempotencyManagerTests()
    {
        _sqliteConnection = new SqliteConnection("Filename=:memory:");
        _sqliteConnection.Open();

        var dbOptions = new DbContextOptionsBuilder<EventFlowDbContext>()
            .UseSqlite(_sqliteConnection)
            .Options;

        _dbContext = new EventFlowDbContext(dbOptions);
        _dbContext.Database.EnsureCreated();

        _dateTimeProvider.UtcNow.Returns(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
        _redisConnection.GetDatabase().Returns(_redisDb);

        _sut = new HybridIdempotencyManager(
            _redisConnection,
            _dbContext,
            _dateTimeProvider,
            Options.Create(_options),
            _logger);
    }

    [Fact]
    public async Task TryAcquireAsync_WhenFastGuardCachedInRedis_ShouldReturnAlreadyProcessed()
    {
        _redisDb.KeyExistsAsync("eventflow:completed:tenant-1:scope-1:key-1")
            .Returns(Task.FromResult(true));

        var result = await _sut.TryAcquireAsync("tenant-1", "key-1", "scope-1", Guid.NewGuid());

        result.IsAlreadyProcessed.Should().BeTrue();
        result.IsAcquired.Should().BeFalse();
        await _redisDb.DidNotReceive().StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>());
    }

    [Fact]
    public async Task TryAcquireAsync_WhenLockHeldInRedis_ShouldReturnConflictInFlight()
    {
        _redisDb.KeyExistsAsync(Arg.Any<RedisKey>()).Returns(Task.FromResult(false));
        _redisDb.StringSetAsync(
            "eventflow:lock:tenant-1:scope-1:key-1",
            Arg.Any<RedisValue>(),
            TimeSpan.FromSeconds(30),
            When.NotExists).Returns(Task.FromResult(false));

        var result = await _sut.TryAcquireAsync("tenant-1", "key-1", "scope-1", Guid.NewGuid());

        result.IsConflictInFlight.Should().BeTrue();
        result.IsAcquired.Should().BeFalse();
    }

    [Fact]
    public async Task TryAcquireAsync_WhenAlreadyInPostgres_ShouldPopulateRedisCacheAndReturnAlreadyProcessed()
    {
        // Pre-insert into SQLite/Postgres DB
        var processed = ProcessedEvent.Create(
            "tenant-1",
            "key-1",
            "scope-1",
            Guid.NewGuid(),
            "payment.settled.v1",
            "OK",
            _dateTimeProvider.UtcNow).Value;
        _dbContext.ProcessedEvents.Add(processed);
        await _dbContext.SaveChangesAsync();

        _redisDb.KeyExistsAsync(Arg.Any<RedisKey>()).Returns(Task.FromResult(false));
        _redisDb.StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), When.NotExists)
            .Returns(Task.FromResult(true));

        var result = await _sut.TryAcquireAsync("tenant-1", "key-1", "scope-1", Guid.NewGuid());

        result.IsAlreadyProcessed.Should().BeTrue();
        result.IsAcquired.Should().BeFalse();

        // Verify Redis completion cache was populated
        await _redisDb.Received(1).StringSetAsync(
            "eventflow:completed:tenant-1:scope-1:key-1",
            "1",
            TimeSpan.FromHours(24));
    }

    [Fact]
    public async Task TryAcquireAsync_WhenNewEventAndLockAcquired_ShouldReturnAcquired()
    {
        _redisDb.KeyExistsAsync(Arg.Any<RedisKey>()).Returns(Task.FromResult(false));
        _redisDb.StringSetAsync(
            "eventflow:lock:tenant-1:scope-1:key-1",
            Arg.Any<RedisValue>(),
            TimeSpan.FromSeconds(30),
            When.NotExists).Returns(Task.FromResult(true));

        var result = await _sut.TryAcquireAsync("tenant-1", "key-1", "scope-1", Guid.NewGuid());

        result.IsAcquired.Should().BeTrue();
        result.LockToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task TryAcquireAsync_WhenRedisFails_ShouldFallbackToPostgresAndReturnAcquired()
    {
        _redisDb.KeyExistsAsync(Arg.Any<RedisKey>())
            .ThrowsAsync(new RedisException("Redis down"));
        _redisDb.StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .ThrowsAsync(new RedisException("Redis down"));

        var result = await _sut.TryAcquireAsync("tenant-1", "key-1", "scope-1", Guid.NewGuid());

        result.IsAcquired.Should().BeTrue();
    }

    [Fact]
    public async Task TryAcquireAsync_WhenRedisConnectionIsNull_ShouldFallbackToPostgresAndReturnAcquired()
    {
        _redisConnection.GetDatabase().Returns((IDatabase?)null);

        var result = await _sut.TryAcquireAsync("tenant-1", "key-1", "scope-1", Guid.NewGuid());

        result.IsAcquired.Should().BeTrue();
    }

    [Fact]
    public async Task MarkCompletedAsync_ShouldPersistRecordInPostgresAndCacheInRedis()
    {
        var eventId = Guid.NewGuid();

        await _sut.MarkCompletedAsync(
            "tenant-1",
            "key-1",
            "scope-1",
            eventId,
            "payment.settled.v1",
            "Approved");

        var persisted = await _dbContext.ProcessedEvents
            .FirstOrDefaultAsync(p => p.TenantId == "tenant-1" && p.IdempotencyKey == "key-1" && p.Scope == "scope-1");

        persisted.Should().NotBeNull();
        persisted!.EventId.Should().Be(eventId);
        persisted.EventType.Should().Be("payment.settled.v1");
        persisted.ResultSummary.Should().Be("Approved");

        await _redisDb.Received(1).StringSetAsync(
            "eventflow:completed:tenant-1:scope-1:key-1",
            "1",
            TimeSpan.FromHours(24));
        await _redisDb.Received(1).KeyDeleteAsync("eventflow:lock:tenant-1:scope-1:key-1");
    }

    [Fact]
    public async Task ReleaseLockAsync_WhenTokenProvided_ShouldExecuteLuaScript()
    {
        await _sut.ReleaseLockAsync("tenant-1", "key-1", "scope-1", "token-abc");

        await _redisDb.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]>(k => k[0] == "eventflow:lock:tenant-1:scope-1:key-1"),
            Arg.Is<RedisValue[]>(v => v[0] == "token-abc"));
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _sqliteConnection.Dispose();
    }
}
