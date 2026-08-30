using EventFlow.Application.Events.Consumers;
using EventFlow.Domain.Common;
using EventFlow.Infrastructure.Messaging;
using EventFlow.Worker.BackgroundServices;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace EventFlow.UnitTests.Worker;

public sealed class EventConsumerWorkerTests : IDisposable
{
    private readonly IRabbitMqConsumer _consumer = Substitute.For<IRabbitMqConsumer>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly IServiceScope _scope = Substitute.For<IServiceScope>();
    private readonly IServiceProvider _serviceProvider = Substitute.For<IServiceProvider>();
    private readonly IEventProcessor _processor = Substitute.For<IEventProcessor>();
    private readonly ILogger<EventConsumerWorker> _logger = Substitute.For<ILogger<EventConsumerWorker>>();
    private readonly EventConsumerWorker _sut;

    public EventConsumerWorkerTests()
    {
        _scopeFactory.CreateScope().Returns(_scope);
        _scope.ServiceProvider.Returns(_serviceProvider);
        _serviceProvider.GetService(typeof(IEventProcessor)).Returns(_processor);

        _sut = new EventConsumerWorker(_consumer, _scopeFactory, _logger);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStarted_ShouldCallConsumerStartAndStopOnCancel()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        _consumer.StartAsync(
            Arg.Any<Func<ConsumedEventContext, CancellationToken, Task<Result>>>(),
            Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        _consumer.StopAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await _sut.StartAsync(cts.Token);
        await Task.Delay(100);
        await _sut.StopAsync(CancellationToken.None);

        await _consumer.Received(1).StartAsync(
            Arg.Any<Func<ConsumedEventContext, CancellationToken, Task<Result>>>(),
            Arg.Any<CancellationToken>());
        await _consumer.Received().StopAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsumerCallback_WhenInvoked_ShouldResolveScopedProcessorAndProcess()
    {
        Func<ConsumedEventContext, CancellationToken, Task<Result>>? capturedCallback = null;

        _consumer.StartAsync(
            Arg.Do<Func<ConsumedEventContext, CancellationToken, Task<Result>>>(cb => capturedCallback = cb),
            Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);

        await _sut.StartAsync(cts.Token);
        await Task.Delay(100);
        await _sut.StopAsync(CancellationToken.None);

        var context = new ConsumedEventContext(
            EventId: Guid.NewGuid(),
            EventType: "payment.settled.v1",
            TenantId: "tenant-acme",
            TraceParent: null,
            OutboxId: null,
            RoutingKey: "payment.settled.v1",
            Payload: "{}",
            ConsumedAt: DateTimeOffset.UtcNow,
            Headers: new Dictionary<string, string>(),
            Redelivered: false);

        _processor.ProcessAsync(context, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        capturedCallback.Should().NotBeNull();
        var result = await capturedCallback!(context, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _processor.Received(1).ProcessAsync(context, Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        _sut.Dispose();
        _scope.Dispose();
    }
}
