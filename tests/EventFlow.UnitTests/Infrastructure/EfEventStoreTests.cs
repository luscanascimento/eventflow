using EventFlow.Domain.Common;
using EventFlow.Domain.Events;
using EventFlow.Domain.Outbox;
using EventFlow.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EventFlow.UnitTests.Infrastructure;

public sealed class EfEventStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly EventFlowDbContext _context;
    private readonly EfEventStore _sut;

    public EfEventStoreTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<EventFlowDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new EventFlowDbContext(options);
        _context.Database.EnsureCreated();

        _sut = new EfEventStore(_context);
    }

    private static (EventRecord Record, OutboxMessage Outbox) CreateEventAndOutbox(
        string tenantId = "tenant-alpha",
        string idempotencyKey = "tx-123")
    {
        var envelope = EventEnvelope.Create(
            eventId: Guid.NewGuid(),
            eventType: "payment.settled.v1",
            version: 1,
            source: "payment-gateway",
            occurredAt: DateTimeOffset.UtcNow,
            correlationId: "corr-1",
            traceParent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            tenantId: tenantId,
            idempotencyKey: idempotencyKey,
            payload: "{\"amount\": 99.90}").Value;

        var now = DateTimeOffset.UtcNow;
        return (EventRecord.FromEnvelope(envelope, now), OutboxMessage.FromEnvelope(envelope, now));
    }

    [Fact]
    public async Task SaveEventAndOutboxAsync_WhenValid_ShouldPersistBothEntitiesAtomically()
    {
        var (record, outbox) = CreateEventAndOutbox();

        var result = await _sut.SaveEventAndOutboxAsync(record, outbox);

        result.IsSuccess.Should().BeTrue();

        var persistedEvent = await _context.Events.FirstOrDefaultAsync(e => e.Id == record.Id);
        persistedEvent.Should().NotBeNull();
        persistedEvent!.EventId.Should().Be(record.EventId);
        persistedEvent.TenantId.Should().Be(record.TenantId);

        var persistedOutbox = await _context.OutboxMessages.FirstOrDefaultAsync(o => o.Id == outbox.Id);
        persistedOutbox.Should().NotBeNull();
        persistedOutbox!.Status.Should().Be(OutboxStatus.Pending);
    }

    [Fact]
    public async Task ExistsByIdempotencyKeyAsync_WhenRecordExists_ShouldReturnTrue()
    {
        var (record, outbox) = CreateEventAndOutbox(tenantId: "tenant-delta", idempotencyKey: "key-999");
        await _sut.SaveEventAndOutboxAsync(record, outbox);

        var exists = await _sut.ExistsByIdempotencyKeyAsync("tenant-delta", "key-999");

        exists.Should().BeTrue();
    }

    [Fact]
    public async Task ExistsByIdempotencyKeyAsync_WhenRecordDoesNotExist_ShouldReturnFalse()
    {
        var exists = await _sut.ExistsByIdempotencyKeyAsync("tenant-nonexistent", "key-random");

        exists.Should().BeFalse();
    }

    [Fact]
    public async Task SaveEventAndOutboxAsync_WhenDuplicateTenantAndIdempotencyKey_ShouldReturnConflict()
    {
        var (record1, outbox1) = CreateEventAndOutbox(tenantId: "tenant-beta", idempotencyKey: "duplicate-key");
        var (record2, outbox2) = CreateEventAndOutbox(tenantId: "tenant-beta", idempotencyKey: "duplicate-key");

        var firstResult = await _sut.SaveEventAndOutboxAsync(record1, outbox1);
        firstResult.IsSuccess.Should().BeTrue();

        // Second attempt with duplicate idempotency key on same tenant
        var secondResult = await _sut.SaveEventAndOutboxAsync(record2, outbox2);

        secondResult.IsFailure.Should().BeTrue();
        secondResult.Error.Type.Should().Be(ErrorType.Conflict);
        secondResult.Error.Code.Should().Be("Event.DuplicateIdempotencyKey");
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }
}
