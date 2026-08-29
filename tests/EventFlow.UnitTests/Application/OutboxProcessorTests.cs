using EventFlow.Application.Abstractions;
using EventFlow.Application.Outbox;
using EventFlow.Domain.Common;
using EventFlow.Domain.Events;
using EventFlow.Domain.Outbox;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace EventFlow.UnitTests.Application;

public class OutboxProcessorTests
{
    private readonly IOutboxStore _outboxStore = Substitute.For<IOutboxStore>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly IDateTimeProvider _dateTimeProvider = Substitute.For<IDateTimeProvider>();
    private readonly ILogger<OutboxProcessor> _logger = Substitute.For<ILogger<OutboxProcessor>>();
    private readonly OutboxProcessorOptions _options = new()
    {
        BatchSize = 10,
        PollingIntervalMs = 100,
        MaxRetries = 3,
        BaseRetryDelaySeconds = 2
    };
    private readonly OutboxProcessor _sut;

    public OutboxProcessorTests()
    {
        _dateTimeProvider.UtcNow.Returns(new DateTimeOffset(2026, 8, 29, 15, 0, 0, TimeSpan.Zero));
        _sut = new OutboxProcessor(
            _outboxStore,
            _publisher,
            _dateTimeProvider,
            _logger,
            Options.Create(_options));
    }

    private static OutboxMessage CreateSampleOutboxMessage()
    {
        var envelope = EventEnvelope.Create(
            eventId: Guid.NewGuid(),
            eventType: "order.created.v1",
            version: 1,
            source: "order-service",
            occurredAt: DateTimeOffset.UtcNow,
            correlationId: "corr-1",
            traceParent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            tenantId: "tenant-acme",
            idempotencyKey: "idem-1",
            payload: "{\"orderId\": 100}").Value;

        return OutboxMessage.FromEnvelope(envelope, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ProcessPendingMessagesAsync_WhenNoMessages_ShouldReturnZeroAndNotPublish()
    {
        _outboxStore.FetchPendingBatchAsync(_options.BatchSize, _dateTimeProvider.UtcNow, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<OutboxMessage>());

        int processed = await _sut.ProcessPendingMessagesAsync();

        processed.Should().Be(0);
        await _publisher.DidNotReceive().PublishAsync(Arg.Any<OutboxMessage>(), Arg.Any<CancellationToken>());
        await _outboxStore.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessPendingMessagesAsync_WhenPublishSucceeds_ShouldMarkPublishedAndSave()
    {
        var message = CreateSampleOutboxMessage();
        _outboxStore.FetchPendingBatchAsync(_options.BatchSize, _dateTimeProvider.UtcNow, Arg.Any<CancellationToken>())
            .Returns(new[] { message });
        _publisher.PublishAsync(message, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        int processed = await _sut.ProcessPendingMessagesAsync();

        processed.Should().Be(1);
        message.Status.Should().Be(OutboxStatus.Published);
        message.ProcessedAt.Should().Be(_dateTimeProvider.UtcNow);
        message.LastError.Should().BeNull();

        await _publisher.Received(1).PublishAsync(message, Arg.Any<CancellationToken>());
        await _outboxStore.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessPendingMessagesAsync_WhenPublishFails_ShouldScheduleRetryWithBackoff()
    {
        var message = CreateSampleOutboxMessage();
        _outboxStore.FetchPendingBatchAsync(_options.BatchSize, _dateTimeProvider.UtcNow, Arg.Any<CancellationToken>())
            .Returns(new[] { message });
        _publisher.PublishAsync(message, Arg.Any<CancellationToken>())
            .Returns(Result.Failure(DomainError.Failure("Broker.ConnectionFailed", "RabbitMQ unreachable")));

        int processed = await _sut.ProcessPendingMessagesAsync();

        processed.Should().Be(1);
        message.Status.Should().Be(OutboxStatus.Pending);
        message.RetryCount.Should().Be(1);
        message.LastError.Should().Be("RabbitMQ unreachable");
        message.NextRetryAt.Should().Be(_dateTimeProvider.UtcNow.AddSeconds(2)); // 2^0 * 2s = 2s

        await _outboxStore.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessPendingMessagesAsync_WhenPublishReachesMaxRetries_ShouldTransitionToDeadLettered()
    {
        var message = CreateSampleOutboxMessage();
        // Simulate already having 2 retries under max 3 retries
        message.MarkFailed("Error 1", maxRetries: 3, TimeSpan.FromSeconds(1), _dateTimeProvider.UtcNow);
        message.MarkFailed("Error 2", maxRetries: 3, TimeSpan.FromSeconds(2), _dateTimeProvider.UtcNow);

        _outboxStore.FetchPendingBatchAsync(_options.BatchSize, _dateTimeProvider.UtcNow, Arg.Any<CancellationToken>())
            .Returns(new[] { message });
        _publisher.PublishAsync(message, Arg.Any<CancellationToken>())
            .Returns(Result.Failure(DomainError.Failure("Broker.FatalError", "Poison message format")));

        int processed = await _sut.ProcessPendingMessagesAsync();

        processed.Should().Be(1);
        message.Status.Should().Be(OutboxStatus.DeadLettered);
        message.RetryCount.Should().Be(3);
        message.NextRetryAt.Should().BeNull();
        message.ProcessedAt.Should().Be(_dateTimeProvider.UtcNow);
        message.LastError.Should().Be("Poison message format");

        await _outboxStore.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
