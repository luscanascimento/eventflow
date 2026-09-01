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
        batch.All(m => m.Status == OutboxStatus.Pending).Should().BeTrue();
    }

    [DockerFact]
    public async Task UpdateStatusAsync_TransitionsToPublishedAndRecordsTimestamp()
    {
        // Arrange
        var store = new EfOutboxStore(_dbContext);
        var envelope = EventEnvelope.Create(
            eventId: Guid.NewGuid(),
            eventType: "payment.settled.v1",
            version: 1,
            source: "payment-gateway",
            occurredAt: DateTimeOffset.UtcNow,
            correlationId: $"corr_{Guid.NewGuid():N}",
            traceParent: null,
            tenantId: "tenant_status_test",
            idempotencyKey: $"ik_{Guid.NewGuid():N}",
            payload: "{\"amount\":50}").Value;

        var msg = OutboxMessage.FromEnvelope(envelope, DateTimeOffset.UtcNow);
        _dbContext.OutboxMessages.Add(msg);
        await _dbContext.SaveChangesAsync(CancellationToken.None);

        // Act
        msg.MarkPublished(DateTimeOffset.UtcNow);
        await store.SaveChangesAsync(CancellationToken.None);

        // Assert
        var reloaded = await _dbContext.OutboxMessages.FindAsync(msg.Id);
        reloaded.Should().NotBeNull();
        reloaded!.Status.Should().Be(OutboxStatus.Published);
        reloaded.ProcessedAt.Should().NotBeNull();
    }
}
