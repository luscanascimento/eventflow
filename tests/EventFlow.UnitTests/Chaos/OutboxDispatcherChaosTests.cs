using EventFlow.Application.Abstractions;
using EventFlow.Application.Outbox;
using EventFlow.Domain.Common;
using EventFlow.Domain.Events;
using EventFlow.Domain.Outbox;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace EventFlow.UnitTests.Chaos;

/// <summary>
/// Chaos stress tests evaluating the Outbox Dispatcher's behavior under severe network degradation,
/// intermittent RabbitMQ disconnections, and partial batch failures.
/// </summary>
public sealed class OutboxDispatcherChaosTests
{
    private readonly IOutboxStore _outboxStore = Substitute.For<IOutboxStore>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private readonly IDateTimeProvider _dateTimeProvider = Substitute.For<IDateTimeProvider>();
    private readonly ILogger<OutboxProcessor> _logger = Substitute.For<ILogger<OutboxProcessor>>();

    private readonly OutboxProcessorOptions _options = new()
    {
        BatchSize = 10,
        PollingIntervalMs = 50,
        MaxRetries = 3,
        BaseRetryDelaySeconds = 2
    };

    private readonly OutboxProcessor _sut;

    public OutboxDispatcherChaosTests()
    {
        _dateTimeProvider.UtcNow.Returns(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
        _sut = new OutboxProcessor(
            _outboxStore,
            _publisher,
            _dateTimeProvider,
            _logger,
            Options.Create(_options));
    }

    private static OutboxMessage CreateMessage(string correlationId)
    {
        var envelope = EventEnvelope.Create(
            eventId: Guid.NewGuid(),
            eventType: "payment.settled.v1",
            version: 1,
            source: "chaos-test",
            occurredAt: DateTimeOffset.UtcNow,
            correlationId: correlationId,
            traceParent: null,
            tenantId: "tenant-chaos",
            idempotencyKey: $"key-{correlationId}",
            payload: "{\"amount\": 100}").Value;

        return OutboxMessage.FromEnvelope(envelope, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Chaos_PartialBatchFailure_6SucceedAnd4Fail_ShouldPersistExactStatesIndependently()
    {
        // ARRANGE: A batch of 10 messages
        var messages = Enumerable.Range(1, 10).Select(i => CreateMessage($"msg-{i}")).ToList();

        _outboxStore.FetchPendingBatchAsync(_options.BatchSize, _dateTimeProvider.UtcNow, Arg.Any<CancellationToken>())
            .Returns(messages);

        // First 6 succeed, last 4 fail with connection reset
        for (int i = 0; i < 6; i++)
        {
            _publisher.PublishAsync(messages[i], Arg.Any<CancellationToken>())
                .Returns(Result.Success());
        }

        for (int i = 6; i < 10; i++)
        {
            _publisher.PublishAsync(messages[i], Arg.Any<CancellationToken>())
                .Returns(Result.Failure(DomainError.Failure("RabbitMQ.SocketReset", "Connection reset by peer")));
        }

        // ACT
        int processed = await _sut.ProcessPendingMessagesAsync();

        // ASSERT: All 10 were processed
        processed.Should().Be(10);

        // Verify the 6 successful messages transitioned to Published
        for (int i = 0; i < 6; i++)
        {
            messages[i].Status.Should().Be(OutboxStatus.Published);
            messages[i].ProcessedAt.Should().Be(_dateTimeProvider.UtcNow);
            messages[i].RetryCount.Should().Be(0);
        }

        // Verify the 4 failed messages were scheduled for exponential retry
        for (int i = 6; i < 10; i++)
        {
            messages[i].Status.Should().Be(OutboxStatus.Pending);
            messages[i].RetryCount.Should().Be(1);
            messages[i].LastError.Should().Be("Connection reset by peer");
            messages[i].NextRetryAt.Should().Be(_dateTimeProvider.UtcNow.AddSeconds(2));
        }

        await _outboxStore.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Chaos_BrokerThrowsUnhandledExceptionDuringPublish_ShouldCatchAndScheduleRetryWithoutCrashing()
    {
        // ARRANGE: Publisher throws unhandled exception (e.g. broker crash)
        var message = CreateMessage("unhandled-crash-msg");

        _outboxStore.FetchPendingBatchAsync(_options.BatchSize, _dateTimeProvider.UtcNow, Arg.Any<CancellationToken>())
            .Returns(new[] { message });

        _publisher.PublishAsync(message, Arg.Any<CancellationToken>())
            .ThrowsAsync(new System.IO.IOException("Broker connection severed unexpectedly"));

        // ACT: Must not throw unhandled exception
        int processed = await _sut.ProcessPendingMessagesAsync();

        // ASSERT
        processed.Should().Be(1);
        message.Status.Should().Be(OutboxStatus.Pending);
        message.RetryCount.Should().Be(1);
        message.LastError.Should().Contain("Broker connection severed unexpectedly");
        message.NextRetryAt.Should().Be(_dateTimeProvider.UtcNow.AddSeconds(2));

        await _outboxStore.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Chaos_FullBackoffProgressionUnderPersistentOutage_TransitionsToDeadLetteredAfterMaxRetries()
    {
        // ARRANGE: Simulate persistent 3-step outage
        var message = CreateMessage("persistent-failure-msg");

        _outboxStore.FetchPendingBatchAsync(_options.BatchSize, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { message });

        _publisher.PublishAsync(message, Arg.Any<CancellationToken>())
            .Returns(Result.Failure(DomainError.Failure("RabbitMQ.Unavailable", "Broker unreachable")));

        // Step 1: Initial Attempt
        await _sut.ProcessPendingMessagesAsync();
        message.Status.Should().Be(OutboxStatus.Pending);
        message.RetryCount.Should().Be(1);
        message.NextRetryAt.Should().Be(_dateTimeProvider.UtcNow.AddSeconds(2)); // 2^0 * 2 = 2s

        // Step 2: Second Attempt
        _dateTimeProvider.UtcNow.Returns(_dateTimeProvider.UtcNow.AddSeconds(2));
        await _sut.ProcessPendingMessagesAsync();
        message.Status.Should().Be(OutboxStatus.Pending);
        message.RetryCount.Should().Be(2);
        message.NextRetryAt.Should().Be(_dateTimeProvider.UtcNow.AddSeconds(4)); // 2^1 * 2 = 4s

        // Step 3: Third Attempt (Reaching MaxRetries = 3)
        _dateTimeProvider.UtcNow.Returns(_dateTimeProvider.UtcNow.AddSeconds(4));
        await _sut.ProcessPendingMessagesAsync();

        // ASSERT: Final State is DeadLettered
        message.Status.Should().Be(OutboxStatus.DeadLettered);
        message.RetryCount.Should().Be(3);
        message.NextRetryAt.Should().BeNull();
        message.ProcessedAt.Should().Be(_dateTimeProvider.UtcNow);
        message.LastError.Should().Be("Broker unreachable");
    }
}
