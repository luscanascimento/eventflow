using System.Text.Json;
using EventFlow.Application.Events.Commands;
using FluentAssertions;
using Xunit;

namespace EventFlow.UnitTests.Application;

public class IngestEventValidatorTests
{
    private readonly IngestEventValidator _validator = new();

    private static IngestEventCommand CreateValidCommand(
        string? tenantId = "tenant-acme",
        string? eventType = "order.created.v1",
        string? source = "web-shop",
        string? idempotencyKey = "tx_12345",
        string? traceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
        string payloadJson = "{\"item\": \"laptop\"}")
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return new IngestEventCommand(
            EventId: Guid.NewGuid(),
            EventType: eventType!,
            Version: 1,
            Source: source!,
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: "corr-1",
            TraceParent: traceParent,
            TenantId: tenantId!,
            IdempotencyKey: idempotencyKey!,
            Payload: doc.RootElement.Clone(),
            Metadata: null);
    }

    [Fact]
    public async Task Validate_WithValidCommand_ShouldPass()
    {
        var command = CreateValidCommand();

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")] // Too short (< 3)
    [InlineData("tenant@domain")] // Invalid characters
    [InlineData("tenant/path/traversal")]
    public async Task Validate_WithInvalidTenantId_ShouldFail(string invalidTenant)
    {
        var command = CreateValidCommand(tenantId: invalidTenant);

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(IngestEventCommand.TenantId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("OrderCreated")] // Not lowercase dot-notated
    [InlineData("order created")] // Spaces not allowed
    [InlineData("singleword")] // Missing dot notation
    public async Task Validate_WithInvalidEventType_ShouldFail(string invalidEventType)
    {
        var command = CreateValidCommand(eventType: invalidEventType);

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(IngestEventCommand.EventType));
    }

    [Theory]
    [InlineData("invalid-trace")]
    [InlineData("01-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01")] // Invalid version (must be 00)
    [InlineData("00-shorttrace-00f067aa0ba902b7-01")]
    public async Task Validate_WithInvalidTraceParent_ShouldFail(string invalidTraceParent)
    {
        var command = CreateValidCommand(traceParent: invalidTraceParent);

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(IngestEventCommand.TraceParent));
    }

    [Fact]
    public async Task Validate_WithNullTraceParent_ShouldPass()
    {
        var command = CreateValidCommand(traceParent: null);

        var result = await _validator.ValidateAsync(command);

        result.IsValid.Should().BeTrue();
    }
}
