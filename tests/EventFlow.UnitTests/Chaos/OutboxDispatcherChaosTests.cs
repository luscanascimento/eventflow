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

        _outboxStore.ClaimPendingBatchAsync(_options.BatchSize, Arg.Any<string>(), Arg.Any<TimeSpan>(), _dateTimeProvider.UtcNow, Arg.Any<CancellationToken>())
            .Returns(messages);
        _outboxStore.MarkPublishedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _outboxStore.MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

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

        // Verify the 6 successful messages called MarkPublishedAsync
        for (int i = 0; i < 6; i++)
        {
            await _outboxStore.Received(1).MarkPublishedAsync(messages[i].Id, Arg.Any<string>(), _dateTimeProvider.UtcNow, Arg.Any<CancellationToken>());
        }

        // Verify the 4 failed messages called MarkFailedAsync
        for (int i = 6; i < 10; i++)
        {
            await _outboxStore.Received(1).MarkFailedAsync(
                messages[i].Id,
                Arg.Any<string>(),
                "Connection reset by peer",
                _options.MaxRetries,
                TimeSpan.FromSeconds(2),
                _dateTimeProvider.UtcNow,
                Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task Chaos_BrokerThrowsUnhandledExceptionDuringPublish_ShouldCatchAndScheduleRetryWithoutCrashing()
    {
        // ARRANGE: Publisher throws unhandled exception (e.g. broker crash)
        var message = CreateMessage("unhandled-crash-msg");

        _outboxStore.ClaimPendingBatchAsync(_options.BatchSize, Arg.Any<string>(), Arg.Any<TimeSpan>(), _dateTimeProvider.UtcNow, Arg.Any<CancellationToken>())
            .Returns(new[] { message });
        _outboxStore.MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        _publisher.PublishAsync(message, Arg.Any<CancellationToken>())
            .ThrowsAsync(new System.IO.IOException("Broker connection severed unexpectedly"));

        // ACT: Must not throw unhandled exception
        int processed = await _sut.ProcessPendingMessagesAsync();

        // ASSERT
        processed.Should().Be(1);
        await _outboxStore.Received(1).MarkFailedAsync(
            message.Id,
            Arg.Any<string>(),
            "Broker connection severed unexpectedly",
            _options.MaxRetries,
            TimeSpan.FromSeconds(2),
            _dateTimeProvider.UtcNow,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Chaos_FullBackoffProgressionUnderPersistentOutage_TransitionsToDeadLetteredAfterMaxRetries()
    {
        // ARRANGE: Simulate persistent 3-step outage
        var message = CreateMessage("persistent-failure-msg");

        _outboxStore.ClaimPendingBatchAsync(_options.BatchSize, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[] { message });
        _outboxStore.MarkFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                string err = callInfo.ArgAt<string>(2);
                int maxRetries = callInfo.ArgAt<int>(3);
                TimeSpan backoff = callInfo.ArgAt<TimeSpan>(4);
                DateTimeOffset now = callInfo.ArgAt<DateTimeOffset>(5);
                return message.MarkFailed(err, maxRetries, backoff, now);
            });

        _publisher.PublishAsync(message, Arg.Any<CancellationToken>())
            .Returns(Result.Failure(DomainError.Failure("RabbitMQ.Unavailable", "Broker unreachable")));

        // Step 1: Initial Attempt (RetryCount: 0 -> 1, backoff 2s)
        int processed1 = await _sut.ProcessPendingMessagesAsync();
        processed1.Should().Be(1);
        message.Status.Should().Be(OutboxStatus.Pending);
        message.RetryCount.Should().Be(1);

        // Step 2: Second Attempt (RetryCount: 1 -> 2, backoff 4s)
        _dateTimeProvider.UtcNow.Returns(_dateTimeProvider.UtcNow.AddSeconds(2));
        int processed2 = await _sut.ProcessPendingMessagesAsync();
        processed2.Should().Be(1);
        message.Status.Should().Be(OutboxStatus.Pending);
        message.RetryCount.Should().Be(2);

        // Step 3: Third Attempt (RetryCount: 2 -> 3 == MaxRetries, transitions to DeadLettered)
        _dateTimeProvider.UtcNow.Returns(_dateTimeProvider.UtcNow.AddSeconds(4));
        int processed3 = await _sut.ProcessPendingMessagesAsync();
        processed3.Should().Be(1);
        message.Status.Should().Be(OutboxStatus.DeadLettered);
        message.RetryCount.Should().Be(_options.MaxRetries);

        await _outboxStore.Received(3).MarkFailedAsync(
            message.Id,
            Arg.Any<string>(),
            "Broker unreachable",
            _options.MaxRetries,
            Arg.Any<TimeSpan>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }
}
