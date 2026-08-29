using System.Text.Json;
using EventFlow.Application.Abstractions;
using EventFlow.Application.Events.Commands;
using EventFlow.Domain.Common;
using EventFlow.Domain.Events;
using EventFlow.Domain.Outbox;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace EventFlow.UnitTests.Application;

public class IngestEventUseCaseTests
{
    private readonly IngestEventValidator _validator = new();
    private readonly IEventStore _eventStore = Substitute.For<IEventStore>();
    private readonly IDateTimeProvider _dateTimeProvider = Substitute.For<IDateTimeProvider>();
    private readonly IngestEventUseCase _sut;

    public IngestEventUseCaseTests()
    {
        _dateTimeProvider.UtcNow.Returns(new DateTimeOffset(2026, 8, 29, 14, 0, 0, TimeSpan.Zero));
        _sut = new IngestEventUseCase(_validator, _eventStore, _dateTimeProvider);
    }

    private static IngestEventCommand CreateValidCommand()
    {
        using var doc = JsonDocument.Parse("{\"orderId\": \"123\", \"amount\": 250.00}");
        return new IngestEventCommand(
            EventId: Guid.Parse("01918a22-4a7b-7212-8e2b-7c3e1e9f1a01"),
            EventType: "order.placed.v1",
            Version: 1,
            Source: "checkout-service",
            OccurredAt: new DateTimeOffset(2026, 8, 29, 13, 59, 0, TimeSpan.Zero),
            CorrelationId: "corr-xyz",
            TraceParent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            TenantId: "tenant-bravo",
            IdempotencyKey: "idem-key-888",
            Payload: doc.RootElement.Clone(),
            Metadata: new Dictionary<string, string> { ["region"] = "sa-east-1" });
    }

    [Fact]
    public async Task ExecuteAsync_WhenCommandIsValidAndNotDuplicate_ShouldAcceptEvent()
    {
        var command = CreateValidCommand();
        _eventStore.ExistsByIdempotencyKeyAsync(command.TenantId, command.IdempotencyKey, Arg.Any<CancellationToken>())
            .Returns(false);
        _eventStore.SaveEventAndOutboxAsync(Arg.Any<EventRecord>(), Arg.Any<OutboxMessage>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _sut.ExecuteAsync(command);

        result.IsSuccess.Should().BeTrue();
        result.Value.EventId.Should().Be(command.EventId!.Value);
        result.Value.Status.Should().Be("Accepted");
        result.Value.IngestedAt.Should().Be(_dateTimeProvider.UtcNow);

        await _eventStore.Received(1).SaveEventAndOutboxAsync(
            Arg.Is<EventRecord>(e => e.EventId == command.EventId && e.TenantId == command.TenantId),
            Arg.Is<OutboxMessage>(o => o.EventId == command.EventId && o.Status == OutboxStatus.Pending),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenDuplicateIdempotencyKey_ShouldReturnConflict()
    {
        var command = CreateValidCommand();
        _eventStore.ExistsByIdempotencyKeyAsync(command.TenantId, command.IdempotencyKey, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _sut.ExecuteAsync(command);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Conflict);
        result.Error.Code.Should().Be("Event.DuplicateIdempotencyKey");

        await _eventStore.DidNotReceive().SaveEventAndOutboxAsync(
            Arg.Any<EventRecord>(),
            Arg.Any<OutboxMessage>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenValidationFails_ShouldReturnValidationFailure()
    {
        using var doc = JsonDocument.Parse("{}");
        var invalidCommand = new IngestEventCommand(
            EventId: null,
            EventType: "", // Invalid empty event type
            Version: 1,
            Source: "test",
            OccurredAt: null,
            CorrelationId: null,
            TraceParent: null,
            TenantId: "tenant",
            IdempotencyKey: "key-1",
            Payload: doc.RootElement.Clone(),
            Metadata: null);

        var result = await _sut.ExecuteAsync(invalidCommand);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Validation);

        await _eventStore.DidNotReceive().ExistsByIdempotencyKeyAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }
}
