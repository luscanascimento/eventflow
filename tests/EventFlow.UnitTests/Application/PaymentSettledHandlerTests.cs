using EventFlow.Application.Events.Consumers;
using EventFlow.Application.Events.Handlers;
using EventFlow.Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace EventFlow.UnitTests.Application;

public class PaymentSettledHandlerTests
{
    private readonly ILogger<PaymentSettledHandler> _logger = Substitute.For<ILogger<PaymentSettledHandler>>();
    private readonly PaymentSettledHandler _sut;

    public PaymentSettledHandlerTests()
    {
        _sut = new PaymentSettledHandler(_logger);
    }

    private static ConsumedEventContext CreateContext(string payload, string eventType = "payment.settled.v1")
    {
        return new ConsumedEventContext(
            EventId: Guid.NewGuid(),
            EventType: eventType,
            TenantId: "tenant-acme",
            TraceParent: null,
            OutboxId: null,
            RoutingKey: eventType,
            Payload: payload,
            ConsumedAt: DateTimeOffset.UtcNow,
            Headers: new Dictionary<string, string>(),
            Redelivered: false);
    }

    [Fact]
    public void CanHandle_WhenEventTypeMatches_ShouldReturnTrue()
    {
        _sut.CanHandle("payment.settled.v1").Should().BeTrue();
        _sut.CanHandle("PAYMENT.SETTLED.V1").Should().BeTrue();
    }

    [Fact]
    public void CanHandle_WhenEventTypeDoesNotMatch_ShouldReturnFalse()
    {
        _sut.CanHandle("order.created.v1").Should().BeFalse();
        _sut.CanHandle("user.registered.v1").Should().BeFalse();
    }

    [Fact]
    public async Task HandleAsync_WhenValidPayload_ShouldReturnSuccess()
    {
        string json = "{\"orderId\": \"ord_123\", \"amount\": 250.00, \"currency\": \"USD\", \"status\": \"PAID\"}";
        var context = CreateContext(json);

        var result = await _sut.HandleAsync(context);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task HandleAsync_WhenMalformedJson_ShouldReturnValidationFailure()
    {
        string json = "{ invalid_json }";
        var context = CreateContext(json);

        var result = await _sut.HandleAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PaymentSettled.InvalidPayload");
        result.Error.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public async Task HandleAsync_WhenOrderIdIsEmpty_ShouldReturnValidationFailure()
    {
        string json = "{\"orderId\": \" \", \"amount\": 100.00, \"currency\": \"BRL\", \"status\": \"PAID\"}";
        var context = CreateContext(json);

        var result = await _sut.HandleAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PaymentSettled.EmptyOrderId");
    }

    [Fact]
    public async Task HandleAsync_WhenAmountIsZeroOrNegative_ShouldReturnValidationFailure()
    {
        string json = "{\"orderId\": \"ord_1\", \"amount\": 0.00, \"currency\": \"BRL\", \"status\": \"PAID\"}";
        var context = CreateContext(json);

        var result = await _sut.HandleAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PaymentSettled.InvalidAmount");
    }

    [Fact]
    public async Task HandleAsync_WhenCurrencyIsEmpty_ShouldReturnValidationFailure()
    {
        string json = "{\"orderId\": \"ord_1\", \"amount\": 50.00, \"currency\": \"\", \"status\": \"PAID\"}";
        var context = CreateContext(json);

        var result = await _sut.HandleAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("PaymentSettled.EmptyCurrency");
    }
}
