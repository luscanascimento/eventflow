using EventFlow.Domain.Events;
using EventFlow.Domain.Outbox;
using EventFlow.Infrastructure.Persistence;
using EventFlow.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EventFlow.IntegrationTests.Infrastructure;

[Collection("DockerContainers")]
public sealed class PostgreSqlOutboxStoreIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly DockerTestcontainersFixture _fixture;
    private EventFlowDbContext _dbContext = null!;

    public PostgreSqlOutboxStoreIntegrationTests(DockerTestcontainersFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        if (!DockerTestcontainersFixture.IsDockerRunning)
        {
            return;
        }

        var options = new DbContextOptionsBuilder<EventFlowDbContext>()
            .UseNpgsql(_fixture.PostgreSqlConnectionString)
            .Options;

        _dbContext = new EventFlowDbContext(options);
        await _dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
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
    public async Task FetchPendingBatch_ReturnsPendingMessagesInCreationOrder()
    {
        // Arrange
        var store = new EfOutboxStore(_dbContext);
        string tenantId = $"tenant_outbox_{Guid.NewGuid():N}";

        for (int i = 0; i < 5; i++)
        {
            var envelope = EventEnvelope.Create(
                eventId: Guid.NewGuid(),
                eventType: "order.created.v1",
                version: 1,
                source: "order-service",
                occurredAt: DateTimeOffset.UtcNow,
                correlationId: $"corr_{Guid.NewGuid():N}",
                traceParent: null,
                tenantId: tenantId,
                idempotencyKey: $"ik_{Guid.NewGuid():N}",
                payload: "{\"test\":\"batch\"}").Value;

            var msg = OutboxMessage.FromEnvelope(envelope, DateTimeOffset.UtcNow);
            _dbContext.OutboxMessages.Add(msg);
        }

        await _dbContext.SaveChangesAsync(CancellationToken.None);

        // Act
        var batch = await store.FetchPendingBatchAsync(10, DateTimeOffset.UtcNow, CancellationToken.None);

        // Assert
        batch.Should().NotBeEmpty();
    }

    [DockerFact]
    public async Task ClaimPendingBatchAsync_ConcurrentWorkers_DoNotClaimSameMessages()
    {
        // Arrange: Insert 10 messages
        string tenantId = $"tenant_concurrent_{Guid.NewGuid():N}";
        for (int i = 0; i < 10; i++)
        {
            var envelope = EventEnvelope.Create(
                eventId: Guid.NewGuid(),
                eventType: "payment.settled.v1",
                version: 1,
                source: "payment-gateway",
                occurredAt: DateTimeOffset.UtcNow,
                correlationId: $"corr_{Guid.NewGuid():N}",
                traceParent: null,
                tenantId: tenantId,
                idempotencyKey: $"ik_{Guid.NewGuid():N}",
                payload: "{\"amount\":100}").Value;

            var msg = OutboxMessage.FromEnvelope(envelope, DateTimeOffset.UtcNow);
            _dbContext.OutboxMessages.Add(msg);
        }

        await _dbContext.SaveChangesAsync(CancellationToken.None);

        // Two independent worker stores with separate DbContext instances
        var options = new DbContextOptionsBuilder<EventFlowDbContext>()
            .UseNpgsql(_fixture.PostgreSqlConnectionString)
            .Options;

        await using var context1 = new EventFlowDbContext(options);
        await using var context2 = new EventFlowDbContext(options);

        var store1 = new EfOutboxStore(context1);
        var store2 = new EfOutboxStore(context2);

        string worker1Token = $"worker_1_{Guid.NewGuid():N}";
        string worker2Token = $"worker_2_{Guid.NewGuid():N}";

        // Act: Concurrent batch claims
        var task1 = store1.ClaimPendingBatchAsync(5, worker1Token, TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow);
        var task2 = store2.ClaimPendingBatchAsync(5, worker2Token, TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow);

        var results = await Task.WhenAll(task1, task2);
        var batch1 = results[0];
        var batch2 = results[1];

        // Assert: Distinct sets of messages claimed with zero overlap
        var ids1 = batch1.Select(m => m.Id).ToHashSet();
        var ids2 = batch2.Select(m => m.Id).ToHashSet();

        ids1.Overlaps(ids2).Should().BeFalse("Concurrent workers with FOR UPDATE SKIP LOCKED must claim disjoint subsets.");
        batch1.All(m => m.LockToken == worker1Token).Should().BeTrue();
        batch2.All(m => m.LockToken == worker2Token).Should().BeTrue();
    }

    [DockerFact]
    public async Task ClaimPendingBatchAsync_WhenLeaseExpired_ReclaimsMessageForNewWorker()
    {
        // Arrange: Insert a message stuck in Processing whose lease expired
        var envelope = EventEnvelope.Create(
            eventId: Guid.NewGuid(),
            eventType: "payment.settled.v1",
            version: 1,
            source: "payment-gateway",
            occurredAt: DateTimeOffset.UtcNow,
            correlationId: $"corr_{Guid.NewGuid():N}",
            traceParent: null,
            tenantId: "tenant_stuck",
            idempotencyKey: $"ik_{Guid.NewGuid():N}",
            payload: "{\"amount\":100}").Value;

        var msg = OutboxMessage.FromEnvelope(envelope, DateTimeOffset.UtcNow.AddMinutes(-5));
        msg.Claim("crashed_worker_token", DateTimeOffset.UtcNow.AddMinutes(-2)); // Expired 2 minutes ago
        _dbContext.OutboxMessages.Add(msg);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        var store = new EfOutboxStore(_dbContext);
        string newWorkerToken = $"new_worker_{Guid.NewGuid():N}";

        // Act
        var claimed = await store.ClaimPendingBatchAsync(10, newWorkerToken, TimeSpan.FromSeconds(30), DateTimeOffset.UtcNow);

        // Assert: Expired message was reclaimed by new worker
        var reclaimedMsg = claimed.FirstOrDefault(m => m.Id == msg.Id);
        reclaimedMsg.Should().NotBeNull();
        reclaimedMsg!.LockToken.Should().Be(newWorkerToken);
        reclaimedMsg.Status.Should().Be(OutboxStatus.Processing);
    }

    [DockerFact]
    public async Task MarkPublishedAsync_WithValidLockToken_TransitionsToPublished()
    {
        // Arrange
        var store = new EfOutboxStore(_dbContext);
        string lockToken = $"worker_{Guid.NewGuid():N}";

        var envelope = EventEnvelope.Create(
            eventId: Guid.NewGuid(),
            eventType: "payment.settled.v1",
            version: 1,
            source: "payment-gateway",
            occurredAt: DateTimeOffset.UtcNow,
            correlationId: $"corr_{Guid.NewGuid():N}",
            traceParent: null,
            tenantId: "tenant_publish_test",
            idempotencyKey: $"ik_{Guid.NewGuid():N}",
            payload: "{\"amount\":50}").Value;

        var msg = OutboxMessage.FromEnvelope(envelope, DateTimeOffset.UtcNow);
        msg.Claim(lockToken, DateTimeOffset.UtcNow.AddSeconds(30));
        _dbContext.OutboxMessages.Add(msg);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        // Act
        var result = await store.MarkPublishedAsync(msg.Id, lockToken, DateTimeOffset.UtcNow, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        var reloaded = await _dbContext.OutboxMessages.FindAsync(msg.Id);
        reloaded.Should().NotBeNull();
        reloaded!.Status.Should().Be(OutboxStatus.Published);
        reloaded.LockToken.Should().BeNull();
    }
}
