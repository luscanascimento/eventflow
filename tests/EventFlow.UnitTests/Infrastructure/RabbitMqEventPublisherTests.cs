using EventFlow.Domain.Events;
using EventFlow.Domain.Outbox;
using EventFlow.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RabbitMQ.Client;
using Xunit;

namespace EventFlow.UnitTests.Infrastructure;

public class RabbitMqEventPublisherTests
{
    private readonly IRabbitMqConnection _connection = Substitute.For<IRabbitMqConnection>();
    private readonly IChannel _channel = Substitute.For<IChannel>();
    private readonly ILogger<RabbitMqEventPublisher> _logger = Substitute.For<ILogger<RabbitMqEventPublisher>>();
    private readonly RabbitMqOptions _options = new()
    {
        ExchangeName = "eventflow.events",
        HostName = "localhost"
    };
    private readonly RabbitMqEventPublisher _sut;

    public RabbitMqEventPublisherTests()
    {
        _connection.CreateChannelAsync(Arg.Any<CreateChannelOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(_channel));
        _sut = new RabbitMqEventPublisher(_connection, Options.Create(_options), _logger);
    }

    private static OutboxMessage CreateSampleMessage()
    {
        var envelope = EventEnvelope.Create(
            eventId: Guid.NewGuid(),
            eventType: "payment.settled.v1",
            version: 1,
            source: "payment-gateway",
            occurredAt: DateTimeOffset.UtcNow,
            correlationId: "corr-1",
            traceParent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            tenantId: "tenant-acme",
            idempotencyKey: "idem-1",
            payload: "{\"amount\": 150.00}").Value;

        return OutboxMessage.FromEnvelope(envelope, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task PublishAsync_WhenConnectionAvailable_ShouldPublishWithHeadersAndPersistentDelivery()
    {
        var message = CreateSampleMessage();

        var result = await _sut.PublishAsync(message, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await _channel.Received(1).BasicPublishAsync(
            exchange: _options.ExchangeName,
            routingKey: message.RoutingKey,
            mandatory: true,
            basicProperties: Arg.Is<BasicProperties>(p =>
                p.ContentType == "application/json" &&
                p.DeliveryMode == DeliveryModes.Persistent &&
                p.MessageId == message.EventId.ToString("D") &&
                p.CorrelationId == message.EventId.ToString("D") &&
                (string)p.Headers!["tenant_id"]! == message.TenantId &&
                (string)p.Headers!["event_type"]! == message.EventType &&
                (string)p.Headers!["traceparent"]! == message.TraceParent!),
            body: Arg.Any<ReadOnlyMemory<byte>>(),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_WhenConnectionFails_ShouldReturnFailureResult()
    {
        var message = CreateSampleMessage();
        _connection.CreateChannelAsync(Arg.Any<CreateChannelOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Broker connection refused"));

        var result = await _sut.PublishAsync(message, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RabbitMQ.PublishFailed");
        result.Error.Message.Should().Contain("Broker connection refused");
    }
}
