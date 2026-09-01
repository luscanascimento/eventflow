using EventFlow.Application.Abstractions;
using EventFlow.Domain.Common;
using EventFlow.Infrastructure.Idempotency;
using EventFlow.Infrastructure.Persistence;
using EventFlow.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace EventFlow.IntegrationTests.Infrastructure;

[Collection("DockerContainers")]
public sealed class RedisHybridIdempotencyIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly DockerTestcontainersFixture _fixture;
    private EventFlowDbContext _dbContext = null!;
    private RedisConnection _redisConnection = null!;
    private IDateTimeProvider _dateTimeProvider = null!;
    private IOptions<RedisOptions> _redisOptions = null!;

    public RedisHybridIdempotencyIntegrationTests(DockerTestcontainersFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        if (!DockerTestcontainersFixture.IsDockerRunning)
        {
            return;
        }

        var dbOptions = new DbContextOptionsBuilder<EventFlowDbContext>()
            .UseNpgsql(_fixture.PostgreSqlConnectionString)
            .Options;

        _dbContext = new EventFlowDbContext(dbOptions);
        await _dbContext.Database.MigrateAsync();

        _redisOptions = Options.Create(new RedisOptions
        {
            ConnectionString = _fixture.RedisConnectionString,
            Enabled = true,
            LockTtlSeconds = 15,
            CacheTtlHours = 24
        });

        _redisConnection = new RedisConnection(_redisOptions, NullLogger<RedisConnection>.Instance);
        _dateTimeProvider = Substitute.For<IDateTimeProvider>();
        _dateTimeProvider.UtcNow.Returns(DateTimeOffset.UtcNow);
    }

    public async Task DisposeAsync()
    {
        if (_redisConnection != null)
        {
            await _redisConnection.DisposeAsync();
        }

        if (_dbContext != null)
        {
            await _dbContext.DisposeAsync();
        }
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
    }

    [DockerFact]
    public async Task TryAcquireAsync_AcquiresDistributedLockInRedis()
    {
        // Arrange
        var manager = new HybridIdempotencyManager(
            _redisConnection,
            _dbContext,
            _dateTimeProvider,
            _redisOptions,
            NullLogger<HybridIdempotencyManager>.Instance);

        string tenantId = "tenant_redis_test";
        string idempotencyKey = $"ik_redis_{Guid.NewGuid():N}";
        string scope = "TestPaymentHandler";
        var eventId = Guid.NewGuid();

        // Act 1: First acquire should succeed
        var result1 = await manager.TryAcquireAsync(tenantId, idempotencyKey, scope, eventId);

        // Act 2: Immediate second acquire with same key before release should conflict in flight
        var result2 = await manager.TryAcquireAsync(tenantId, idempotencyKey, scope, Guid.NewGuid());

        // Assert
        result1.IsAcquired.Should().BeTrue();
        result1.LockToken.Should().NotBeNullOrWhiteSpace();

        result2.IsConflictInFlight.Should().BeTrue();
        result2.IsAcquired.Should().BeFalse();
    }

    [DockerFact]
    public async Task MarkCompletedAsync_CachesInRedisAndPersistsInPostgres()
    {
        // Arrange
        var manager = new HybridIdempotencyManager(
            _redisConnection,
            _dbContext,
            _dateTimeProvider,
            _redisOptions,
            NullLogger<HybridIdempotencyManager>.Instance);

        string tenantId = "tenant_mark_test";
        string idempotencyKey = $"ik_comp_{Guid.NewGuid():N}";
        string scope = "TestSettlementHandler";
        var eventId = Guid.NewGuid();

        // Acquire lock
        var acquireResult = await manager.TryAcquireAsync(tenantId, idempotencyKey, scope, eventId);
        acquireResult.IsAcquired.Should().BeTrue();

        // Act: Mark completed
        await manager.MarkCompletedAsync(tenantId, idempotencyKey, scope, eventId, "payment.settled.v1", "Completed");

        // Act: Try acquire again after completion
        var subsequentAcquire = await manager.TryAcquireAsync(tenantId, idempotencyKey, scope, Guid.NewGuid());

        // Assert
        subsequentAcquire.IsAlreadyProcessed.Should().BeTrue();

        var recordInDb = await _dbContext.ProcessedEvents
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Scope == scope && p.IdempotencyKey == idempotencyKey);

        recordInDb.Should().NotBeNull();
        recordInDb!.EventId.Should().Be(eventId);
    }

    [DockerFact]
    public async Task ReleaseLockAsync_ExecutesAtomicLuaScriptAndReleasesLock()
    {
        // Arrange
        var manager = new HybridIdempotencyManager(
            _redisConnection,
            _dbContext,
            _dateTimeProvider,
            _redisOptions,
            NullLogger<HybridIdempotencyManager>.Instance);

        string tenantId = "tenant_release_test";
        string idempotencyKey = $"ik_rel_{Guid.NewGuid():N}";
        string scope = "TestOrderHandler";
        var eventId = Guid.NewGuid();

        // Act 1: Acquire lock
        var acquireResult = await manager.TryAcquireAsync(tenantId, idempotencyKey, scope, eventId);
        acquireResult.IsAcquired.Should().BeTrue();

        // Act 2: Release lock with token
        await manager.ReleaseLockAsync(tenantId, idempotencyKey, scope, acquireResult.LockToken);

        // Act 3: Subsequent acquire should succeed again
        var reacquireResult = await manager.TryAcquireAsync(tenantId, idempotencyKey, scope, Guid.NewGuid());

        // Assert
        reacquireResult.IsAcquired.Should().BeTrue();
    }
}
