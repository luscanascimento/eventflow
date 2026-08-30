using EventFlow.Application.Events.Consumers;
using EventFlow.Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace EventFlow.UnitTests.Application;

public class EventProcessorTests
{
    private readonly IEventConsumerHandler _handler = Substitute.For<IEventConsumerHandler>();
    private readonly ILogger<EventProcessor> _logger = Substitute.For<ILogger<EventProcessor>>();
    private readonly EventProcessor _sut;

    public EventProcessorTests()
    {
        _sut = new EventProcessor(new[] { _handler }, _logger);
    }

    private static ConsumedEventContext CreateContext(string eventType = "payment.settled.v1")
    {
        return new ConsumedEventContext(
            EventId: Guid.NewGuid(),
            EventType: eventType,
            TenantId: "tenant-acme",
            TraceParent: null,
            OutboxId: null,
            RoutingKey: eventType,
            Payload: "{}",
            ConsumedAt: DateTimeOffset.UtcNow,
            Headers: new Dictionary<string, string>(),
            Redelivered: false);
    }

    [Fact]
    public async Task ProcessAsync_WhenHandlerMatches_ShouldInvokeHandlerAndReturnResult()
    {
        var context = CreateContext("payment.settled.v1");
        _handler.CanHandle("payment.settled.v1").Returns(true);
        _handler.HandleAsync(context, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var result = await _sut.ProcessAsync(context);

        result.IsSuccess.Should().BeTrue();
        await _handler.Received(1).HandleAsync(context, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_WhenNoHandlerFound_ShouldReturnSuccessToAcknowledgeSilently()
    {
        var context = CreateContext("unknown.event.type");
        _handler.CanHandle("unknown.event.type").Returns(false);

        var result = await _sut.ProcessAsync(context);

        result.IsSuccess.Should().BeTrue();
        await _handler.DidNotReceive().HandleAsync(Arg.Any<ConsumedEventContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_WhenHandlerFails_ShouldReturnFailureResult()
    {
        var context = CreateContext("payment.settled.v1");
        _handler.CanHandle("payment.settled.v1").Returns(true);
        _handler.HandleAsync(context, Arg.Any<CancellationToken>())
            .Returns(Result.Failure(DomainError.Validation("Payment.Invalid", "Bad order data")));

        var result = await _sut.ProcessAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Payment.Invalid");
    }

    [Fact]
    public async Task ProcessAsync_WhenHandlerThrowsException_ShouldCatchAndReturnFailure()
    {
        var context = CreateContext("payment.settled.v1");
        _handler.CanHandle("payment.settled.v1").Returns(true);
        _handler.HandleAsync(context, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("External service down"));

        var result = await _sut.ProcessAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EventProcessor.UnhandledException");
        result.Error.Message.Should().Contain("External service down");
    }

    [Fact]
    public async Task ProcessAsync_WhenContextIsNull_ShouldThrowArgumentNullException()
    {
        Func<Task> act = async () => await _sut.ProcessAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
