using EventFlow.Domain.Common;
using EventFlow.Domain.Events;
using FluentAssertions;
using Xunit;

namespace EventFlow.UnitTests.Domain;

public class EventEnvelopeTests
{
    [Fact]
    public void Create_WithValidParameters_ShouldReturnSuccess()
    {
        var eventId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, string> { ["env"] = "test" };

        var result = EventEnvelope.Create(
            eventId: eventId,
            eventType: "payment.settled.v1",
            version: 1,
            source: "payment-gateway",
            occurredAt: occurredAt,
            correlationId: "corr-123",
            traceParent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            tenantId: "tenant-acme",
            idempotencyKey: "tx-999",
            payload: "{\"amount\": 100}",
            metadata: metadata);

        result.IsSuccess.Should().BeTrue();
        var envelope = result.Value;
        envelope.EventId.Should().Be(eventId);
        envelope.EventType.Should().Be("payment.settled.v1");
        envelope.Version.Should().Be(1);
        envelope.Source.Should().Be("payment-gateway");
        envelope.OccurredAt.Should().Be(occurredAt);
        envelope.CorrelationId.Should().Be("corr-123");
        envelope.TraceParent.Should().Be("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
        envelope.TenantId.Should().Be("tenant-acme");
        envelope.IdempotencyKey.Should().Be("tx-999");
        envelope.Payload.Should().Be("{\"amount\": 100}");
        envelope.Metadata.Should().ContainKey("env").WhoseValue.Should().Be("test");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithEmptyEventType_ShouldReturnValidationError(string invalidEventType)
    {
        var result = EventEnvelope.Create(
            eventId: Guid.NewGuid(),
            eventType: invalidEventType,
            version: 1,
            source: "source",
            occurredAt: DateTimeOffset.UtcNow,
            correlationId: "corr-1",
            traceParent: null,
            tenantId: "tenant-1",
            idempotencyKey: "key-1",
            payload: "{}");

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Validation);
        result.Error.Code.Should().Be("EventEnvelope.EmptyEventType");
    }

    [Fact]
    public void Create_WithEmptyGuidEventId_ShouldReturnValidationError()
    {
        var result = EventEnvelope.Create(
            eventId: Guid.Empty,
            eventType: "payment.settled.v1",
            version: 1,
            source: "source",
            occurredAt: DateTimeOffset.UtcNow,
            correlationId: "corr-1",
            traceParent: null,
            tenantId: "tenant-1",
            idempotencyKey: "key-1",
            payload: "{}");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EventEnvelope.InvalidEventId");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_WithInvalidVersion_ShouldReturnValidationError(int invalidVersion)
    {
        var result = EventEnvelope.Create(
            eventId: Guid.NewGuid(),
            eventType: "payment.settled.v1",
            version: invalidVersion,
            source: "source",
            occurredAt: DateTimeOffset.UtcNow,
            correlationId: "corr-1",
            traceParent: null,
            tenantId: "tenant-1",
            idempotencyKey: "key-1",
            payload: "{}");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EventEnvelope.InvalidVersion");
    }
}
