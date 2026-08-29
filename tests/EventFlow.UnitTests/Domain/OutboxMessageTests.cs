using EventFlow.Domain.Common;
using EventFlow.Domain.Events;
using EventFlow.Domain.Outbox;
using FluentAssertions;
using Xunit;

namespace EventFlow.UnitTests.Domain;

public class OutboxMessageTests
{
    private static EventEnvelope CreateSampleEnvelope()
    {
        return EventEnvelope.Create(
            eventId: Guid.NewGuid(),
            eventType: "payment.settled.v1",
            version: 1,
            source: "payment-gateway",
            occurredAt: DateTimeOffset.UtcNow,
            correlationId: "corr-1",
            traceParent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            tenantId: "tenant-acme",
            idempotencyKey: "tx-123",
            payload: "{\"amount\": 100}").Value;
    }

    [Fact]
    public void FromEnvelope_ShouldCreatePendingOutboxMessage()
    {
        var envelope = CreateSampleEnvelope();
        var now = DateTimeOffset.UtcNow;

        var outbox = OutboxMessage.FromEnvelope(envelope, now);

        outbox.Id.Should().NotBeEmpty();
        outbox.EventId.Should().Be(envelope.EventId);
        outbox.TenantId.Should().Be(envelope.TenantId);
        outbox.EventType.Should().Be(envelope.EventType);
        outbox.RoutingKey.Should().Be(envelope.EventType);
        outbox.Payload.Should().Be(envelope.Payload);
        outbox.TraceParent.Should().Be(envelope.TraceParent);
        outbox.Status.Should().Be(OutboxStatus.Pending);
        outbox.CreatedAt.Should().Be(now);
        outbox.RetryCount.Should().Be(0);
        outbox.ProcessedAt.Should().BeNull();
        outbox.LastError.Should().BeNull();
    }

    [Fact]
    public void MarkProcessing_WhenPending_ShouldTransitionToProcessing()
    {
        var outbox = OutboxMessage.FromEnvelope(CreateSampleEnvelope(), DateTimeOffset.UtcNow);

        var result = outbox.MarkProcessing();

        result.IsSuccess.Should().BeTrue();
        outbox.Status.Should().Be(OutboxStatus.Processing);
    }

    [Fact]
    public void MarkPublished_WhenPending_ShouldTransitionToPublishedAndSetTimestamp()
    {
        var outbox = OutboxMessage.FromEnvelope(CreateSampleEnvelope(), DateTimeOffset.UtcNow);
        var publishedAt = DateTimeOffset.UtcNow;

        var result = outbox.MarkPublished(publishedAt);

        result.IsSuccess.Should().BeTrue();
        outbox.Status.Should().Be(OutboxStatus.Published);
        outbox.ProcessedAt.Should().Be(publishedAt);
        outbox.LastError.Should().BeNull();
    }

    [Fact]
    public void MarkPublished_WhenAlreadyPublished_ShouldReturnConflict()
    {
        var outbox = OutboxMessage.FromEnvelope(CreateSampleEnvelope(), DateTimeOffset.UtcNow);
        outbox.MarkPublished(DateTimeOffset.UtcNow);

        var result = outbox.MarkPublished(DateTimeOffset.UtcNow);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Conflict);
    }

    [Fact]
    public void MarkFailed_UnderMaxRetries_ShouldStayPendingAndScheduleNextRetry()
    {
        var outbox = OutboxMessage.FromEnvelope(CreateSampleEnvelope(), DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var backoff = TimeSpan.FromSeconds(5);

        outbox.MarkFailed("Network timeout", maxRetries: 3, retryBackoff: backoff, now: now);

        outbox.Status.Should().Be(OutboxStatus.Pending);
        outbox.RetryCount.Should().Be(1);
        outbox.LastError.Should().Be("Network timeout");
        outbox.NextRetryAt.Should().Be(now.Add(backoff));
    }

    [Fact]
    public void MarkFailed_WhenReachingMaxRetries_ShouldTransitionToDeadLettered()
    {
        var outbox = OutboxMessage.FromEnvelope(CreateSampleEnvelope(), DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;

        outbox.MarkFailed("Fatal Broker Error", maxRetries: 1, retryBackoff: TimeSpan.FromSeconds(5), now: now);

        outbox.Status.Should().Be(OutboxStatus.DeadLettered);
        outbox.RetryCount.Should().Be(1);
        outbox.LastError.Should().Be("Fatal Broker Error");
        outbox.NextRetryAt.Should().BeNull();
        outbox.ProcessedAt.Should().Be(now);
    }
}
