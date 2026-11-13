using EventFlow.Domain.Common;
using EventFlow.Domain.Idempotency;
using FluentAssertions;
using Xunit;

namespace EventFlow.UnitTests.Domain;

public class ProcessedEventTests
{
    [Fact]
    public void Create_WithValidParameters_ShouldReturnSuccess()
    {
        var eventId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var result = ProcessedEvent.Create(
            tenantId: "tenant-acme",
            idempotencyKey: "pay-12345",
            scope: "PaymentSettledHandler",
            eventId: eventId,
            eventType: "payment.settled.v1",
            resultSummary: "Processed successfully",
            processedAt: now);

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().NotBeEmpty();
        result.Value.TenantId.Should().Be("tenant-acme");
        result.Value.IdempotencyKey.Should().Be("pay-12345");
        result.Value.Scope.Should().Be("PaymentSettledHandler");
        result.Value.EventId.Should().Be(eventId);
        result.Value.EventType.Should().Be("payment.settled.v1");
        result.Value.ResultSummary.Should().Be("Processed successfully");
        result.Value.ProcessedAt.Should().Be(now);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Create_WithEmptyTenantId_ShouldReturnValidationFailure(string? tenantId)
    {
        var result = ProcessedEvent.Create(
            tenantId: tenantId!,
            idempotencyKey: "pay-123",
            scope: "scope",
            eventId: Guid.NewGuid(),
            eventType: "payment.settled.v1",
            resultSummary: null,
            processedAt: DateTimeOffset.UtcNow);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProcessedEvent.EmptyTenantId");
        result.Error.Type.Should().Be(ErrorType.Validation);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Create_WithEmptyIdempotencyKey_ShouldReturnValidationFailure(string? key)
    {
        var result = ProcessedEvent.Create(
            tenantId: "tenant-1",
            idempotencyKey: key!,
            scope: "scope",
            eventId: Guid.NewGuid(),
            eventType: "payment.settled.v1",
            resultSummary: null,
            processedAt: DateTimeOffset.UtcNow);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProcessedEvent.EmptyIdempotencyKey");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Create_WithEmptyScope_ShouldReturnValidationFailure(string? scope)
    {
        var result = ProcessedEvent.Create(
            tenantId: "tenant-1",
            idempotencyKey: "key-1",
            scope: scope!,
            eventId: Guid.NewGuid(),
            eventType: "payment.settled.v1",
            resultSummary: null,
            processedAt: DateTimeOffset.UtcNow);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProcessedEvent.EmptyScope");
    }

    [Fact]
    public void Create_WithEmptyEventId_ShouldReturnValidationFailure()
    {
        var result = ProcessedEvent.Create(
            tenantId: "tenant-1",
            idempotencyKey: "key-1",
            scope: "scope",
            eventId: Guid.Empty,
            eventType: "payment.settled.v1",
            resultSummary: null,
            processedAt: DateTimeOffset.UtcNow);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProcessedEvent.InvalidEventId");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Create_WithEmptyEventType_ShouldReturnValidationFailure(string? eventType)
    {
        var result = ProcessedEvent.Create(
            tenantId: "tenant-1",
            idempotencyKey: "key-1",
            scope: "scope",
            eventId: Guid.NewGuid(),
            eventType: eventType!,
            resultSummary: null,
            processedAt: DateTimeOffset.UtcNow);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ProcessedEvent.EmptyEventType");
    }

    [Fact]
    public void MarkFailed_WhenAlreadyCompleted_ShouldReturnConflictAndPreserveCompletedStatus()
    {
        var now = DateTimeOffset.UtcNow;
        var record = ProcessedEvent.CreateProcessing(
            tenantId: "tenant-1",
            idempotencyKey: "key-1",
            scope: "scope",
            eventId: Guid.NewGuid(),
            eventType: "payment.settled.v1",
            lockToken: "token-1",
            lockedUntilUtc: now.AddMinutes(1),
            createdAt: now).Value;

        var completedResult = record.MarkCompleted("token-1", "Done", now);
        completedResult.IsSuccess.Should().BeTrue();

        var failResult = record.MarkFailed("token-1");

        failResult.IsFailure.Should().BeTrue();
        failResult.Error.Code.Should().Be("ProcessedEvent.AlreadyCompleted");
        record.Status.Should().Be(ProcessedEventStatus.Completed);
    }

    [Fact]
    public void MarkCompleted_WhenEventTypeProvided_ShouldUpdatePlaceholderEventType()
    {
        var now = DateTimeOffset.UtcNow;
        var record = ProcessedEvent.CreateProcessing(
            tenantId: "tenant-1",
            idempotencyKey: "key-1",
            scope: "scope",
            eventId: Guid.NewGuid(),
            eventType: "Unknown",
            lockToken: "token-1",
            lockedUntilUtc: now.AddMinutes(1),
            createdAt: now).Value;

        var result = record.MarkCompleted("token-1", "Done", now, "payment.settled.v1");

        result.IsSuccess.Should().BeTrue();
        record.EventType.Should().Be("payment.settled.v1");
    }
}
