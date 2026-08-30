using EventFlow.Application.Abstractions;
using EventFlow.Application.Events.Consumers;
using EventFlow.Application.Idempotency;
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
    private readonly IIdempotencyManager _idempotencyManager = Substitute.For<IIdempotencyManager>();
    private readonly ILogger<EventProcessor> _logger = Substitute.For<ILogger<EventProcessor>>();
    private readonly EventProcessor _sut;

    public EventProcessorTests()
    {
        _sut = new EventProcessor(new[] { _handler }, _idempotencyManager, _logger);
    }

    private static ConsumedEventContext CreateContext(string eventType = "payment.settled.v1", string? idempotencyKey = "pay_key_1")
    {
        var headers = new Dictionary<string, string>();
        if (idempotencyKey != null)
        {
            headers["idempotency_key"] = idempotencyKey;
        }

        return new ConsumedEventContext(
            EventId: Guid.NewGuid(),
            EventType: eventType,
            TenantId: "tenant-acme",
            TraceParent: null,
            OutboxId: null,
            RoutingKey: eventType,
            Payload: "{}",
            ConsumedAt: DateTimeOffset.UtcNow,
            Headers: headers,
            Redelivered: false);
    }

    [Fact]
    public async Task ProcessAsync_WhenAlreadyProcessed_ShouldReturnSuccessWithoutInvokingHandler()
    {
        var context = CreateContext();
        _handler.CanHandle("payment.settled.v1").Returns(true);

        _idempotencyManager.TryAcquireAsync(
            context.TenantId,
            "pay_key_1",
            _handler.GetType().Name,
            context.EventId,
            Arg.Any<CancellationToken>()).Returns(IdempotencyAcquireResult.AlreadyProcessed());

        var result = await _sut.ProcessAsync(context);

        result.IsSuccess.Should().BeTrue();
        await _handler.DidNotReceive().HandleAsync(Arg.Any<ConsumedEventContext>(), Arg.Any<CancellationToken>());
        await _idempotencyManager.DidNotReceive().MarkCompletedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_WhenConflictInFlight_ShouldReturnConflictFailureWithoutInvokingHandler()
    {
        var context = CreateContext();
        _handler.CanHandle("payment.settled.v1").Returns(true);

        _idempotencyManager.TryAcquireAsync(
            context.TenantId,
            "pay_key_1",
            _handler.GetType().Name,
            context.EventId,
            Arg.Any<CancellationToken>()).Returns(IdempotencyAcquireResult.ConflictInFlight());

        var result = await _sut.ProcessAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Idempotency.ConflictInFlight");
        result.Error.Type.Should().Be(ErrorType.Conflict);
        await _handler.DidNotReceive().HandleAsync(Arg.Any<ConsumedEventContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_WhenLockAcquiredAndHandlerSucceeds_ShouldMarkCompletedAndReturnSuccess()
    {
        var context = CreateContext();
        _handler.CanHandle("payment.settled.v1").Returns(true);
        _handler.HandleAsync(context, Arg.Any<CancellationToken>()).Returns(Result.Success());

        _idempotencyManager.TryAcquireAsync(
            context.TenantId,
            "pay_key_1",
            _handler.GetType().Name,
            context.EventId,
            Arg.Any<CancellationToken>()).Returns(IdempotencyAcquireResult.Acquired("token-123"));

        var result = await _sut.ProcessAsync(context);

        result.IsSuccess.Should().BeTrue();
        await _handler.Received(1).HandleAsync(context, Arg.Any<CancellationToken>());
        await _idempotencyManager.Received(1).MarkCompletedAsync(
            context.TenantId,
            "pay_key_1",
            _handler.GetType().Name,
            context.EventId,
            context.EventType,
            "Success",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_WhenLockAcquiredAndHandlerFails_ShouldReleaseLockAndReturnFailure()
    {
        var context = CreateContext();
        _handler.CanHandle("payment.settled.v1").Returns(true);
        _handler.HandleAsync(context, Arg.Any<CancellationToken>())
            .Returns(Result.Failure(DomainError.Validation("Payment.Invalid", "Bad order amount")));

        _idempotencyManager.TryAcquireAsync(
            context.TenantId,
            "pay_key_1",
            _handler.GetType().Name,
            context.EventId,
            Arg.Any<CancellationToken>()).Returns(IdempotencyAcquireResult.Acquired("token-123"));

        var result = await _sut.ProcessAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Payment.Invalid");
        await _idempotencyManager.Received(1).ReleaseLockAsync(
            context.TenantId,
            "pay_key_1",
            _handler.GetType().Name,
            "token-123",
            Arg.Any<CancellationToken>());
        await _idempotencyManager.DidNotReceive().MarkCompletedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_WhenLockAcquiredAndHandlerThrows_ShouldReleaseLockAndReturnFailure()
    {
        var context = CreateContext();
        _handler.CanHandle("payment.settled.v1").Returns(true);
        _handler.HandleAsync(context, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("External API network failure"));

        _idempotencyManager.TryAcquireAsync(
            context.TenantId,
            "pay_key_1",
            _handler.GetType().Name,
            context.EventId,
            Arg.Any<CancellationToken>()).Returns(IdempotencyAcquireResult.Acquired("token-123"));

        var result = await _sut.ProcessAsync(context);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EventProcessor.UnhandledException");
        await _idempotencyManager.Received(1).ReleaseLockAsync(
            context.TenantId,
            "pay_key_1",
            _handler.GetType().Name,
            "token-123",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_WhenNoHandlerFound_ShouldReturnSuccessToAcknowledgeSilently()
    {
        var context = CreateContext("unknown.event.type");
        _handler.CanHandle("unknown.event.type").Returns(false);

        var result = await _sut.ProcessAsync(context);

        result.IsSuccess.Should().BeTrue();
        await _idempotencyManager.DidNotReceive().TryAcquireAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_WhenContextIsNull_ShouldThrowArgumentNullException()
    {
        Func<Task> act = async () => await _sut.ProcessAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
