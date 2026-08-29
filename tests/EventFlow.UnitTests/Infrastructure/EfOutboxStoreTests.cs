using EventFlow.Domain.Events;
using EventFlow.Domain.Outbox;
using EventFlow.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EventFlow.UnitTests.Infrastructure;

public sealed class EfOutboxStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly EventFlowDbContext _context;
    private readonly EfOutboxStore _sut;

    public EfOutboxStoreTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<EventFlowDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new EventFlowDbContext(options);
        _context.Database.EnsureCreated();

        _sut = new EfOutboxStore(_context);
    }

    private static OutboxMessage CreateOutboxMessage(string idempotencyKey, DateTimeOffset createdAt)
    {
        var envelope = EventEnvelope.Create(
            eventId: Guid.NewGuid(),
            eventType: "payment.settled.v1",
            version: 1,
            source: "payment-gateway",
            occurredAt: createdAt,
            correlationId: "corr-1",
            traceParent: null,
            tenantId: "tenant-1",
            idempotencyKey: idempotencyKey,
            payload: "{\"amount\": 10}").Value;

        return OutboxMessage.FromEnvelope(envelope, createdAt);
    }

    [Fact]
    public async Task FetchPendingBatchAsync_ShouldReturnOnlyDuePendingMessagesOrderedByCreatedAt()
    {
        var now = new DateTimeOffset(2026, 8, 29, 15, 0, 0, TimeSpan.Zero);

        // 1. Pending and due (NextRetryAt is null)
        var msg1 = CreateOutboxMessage("key-1", now.AddMinutes(-5));

        // 2. Pending and due (NextRetryAt in past)
        var msg2 = CreateOutboxMessage("key-2", now.AddMinutes(-4));
        msg2.MarkFailed("error", 5, TimeSpan.FromMinutes(-1), now); // next retry in past

        // 3. Pending but NOT due (NextRetryAt in future)
        var msg3 = CreateOutboxMessage("key-3", now.AddMinutes(-3));
        msg3.MarkFailed("error", 5, TimeSpan.FromMinutes(10), now); // next retry in future

        // 4. Published (should be ignored)
        var msg4 = CreateOutboxMessage("key-4", now.AddMinutes(-2));
        msg4.MarkPublished(now);

        // 5. DeadLettered (should be ignored)
        var msg5 = CreateOutboxMessage("key-5", now.AddMinutes(-1));
        msg5.MarkFailed("fatal", 1, TimeSpan.Zero, now);

        await _context.OutboxMessages.AddRangeAsync(msg1, msg2, msg3, msg4, msg5);
        await _context.SaveChangesAsync();

        var batch = await _sut.FetchPendingBatchAsync(batchSize: 10, now: now);

        batch.Should().HaveCount(2);
        batch.Select(m => m.Id).Should().ContainInOrder(msg1.Id, msg2.Id);
    }

    [Fact]
    public async Task FetchPendingBatchAsync_ShouldRespectBatchSizeLimit()
    {
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 15; i++)
        {
            await _context.OutboxMessages.AddAsync(CreateOutboxMessage($"key-{i}", now.AddSeconds(i)));
        }
        await _context.SaveChangesAsync();

        var batch = await _sut.FetchPendingBatchAsync(batchSize: 5, now: now);

        batch.Should().HaveCount(5);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
