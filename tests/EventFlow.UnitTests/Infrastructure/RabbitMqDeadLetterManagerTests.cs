using System.Text;
using EventFlow.Infrastructure.DeadLetter;
using EventFlow.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;
using Xunit;

namespace EventFlow.UnitTests.Infrastructure;

public class RabbitMqDeadLetterManagerTests
{
    private readonly IRabbitMqConnection _connection = Substitute.For<IRabbitMqConnection>();
    private readonly IChannel _channel = Substitute.For<IChannel>();
    private readonly ILogger<RabbitMqDeadLetterManager> _logger = Substitute.For<ILogger<RabbitMqDeadLetterManager>>();
    private readonly RabbitMqOptions _options = new()
    {
        ExchangeName = "eventflow.events",
        DlqQueueName = "eventflow.events.dlq",
        DlxExchangeName = "eventflow.dlx"
    };
    private readonly RabbitMqDeadLetterManager _sut;

    public RabbitMqDeadLetterManagerTests()
    {
        _connection.CreateChannelAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(_channel));
        _sut = new RabbitMqDeadLetterManager(_connection, Options.Create(_options), _logger);
    }

    [Fact]
    public async Task PeekMessagesAsync_WhenCountZeroOrNegative_ShouldReturnEmptyListWithoutQueryingChannel()
    {
        var result = await _sut.PeekMessagesAsync(0);

        result.Should().BeEmpty();
        await _connection.DidNotReceive().CreateChannelAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PeekMessagesAsync_WhenMessagesExist_ShouldReturnMessagesAndRequeueThem()
    {
        var properties = new BasicProperties
        {
            MessageId = "msg-123",
            Headers = new Dictionary<string, object?>
            {
                ["tenant_id"] = Encoding.UTF8.GetBytes("tenant-1"),
                ["event_type"] = Encoding.UTF8.GetBytes("payment.settled.v1"),
                ["traceparent"] = "00-12345-01"
            }
        };

        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"amount\":100}"));

        var getResult = new BasicGetResult(
            deliveryTag: 101,
            redelivered: false,
            exchange: "eventflow.dlx",
            routingKey: "payment.settled.v1",
            messageCount: 1,
            basicProperties: properties,
            body: body);

        _channel.BasicGetAsync(_options.DlqQueueName, false, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<BasicGetResult?>(getResult),
                Task.FromResult<BasicGetResult?>(null));

        var messages = await _sut.PeekMessagesAsync(5);

        messages.Should().HaveCount(1);
        messages[0].MessageId.Should().Be("msg-123");
        messages[0].TenantId.Should().Be("tenant-1");
        messages[0].EventType.Should().Be("payment.settled.v1");
        messages[0].TraceParent.Should().Be("00-12345-01");
        messages[0].Payload.Should().Be("{\"amount\":100}");

        // Verify peeked message was NACKed with requeue: true
        await _channel.Received(1).BasicNackAsync(101, false, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplayMessagesAsync_WhenMessagesExist_ShouldRepublishAndAckFromDlq()
    {
        var properties = new BasicProperties
        {
            MessageId = "msg-replay-1",
            Headers = new Dictionary<string, object?>
            {
                ["tenant_id"] = "tenant-1"
            }
        };

        var body = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"replayed\":true}"));

        var getResult = new BasicGetResult(
            deliveryTag: 202,
            redelivered: false,
            exchange: "eventflow.dlx",
            routingKey: "payment.settled.v1",
            messageCount: 1,
            basicProperties: properties,
            body: body);

        _channel.BasicGetAsync(_options.DlqQueueName, false, Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<BasicGetResult?>(getResult),
                Task.FromResult<BasicGetResult?>(null));

        var result = await _sut.ReplayMessagesAsync(5);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);

        // Verify republish to primary exchange
        await _channel.Received(1).BasicPublishAsync(
            exchange: _options.ExchangeName,
            routingKey: "payment.settled.v1",
            mandatory: true,
            basicProperties: Arg.Is<BasicProperties>(p => p.Headers != null && p.Headers.ContainsKey("x-replayed-at")),
            body: body,
            cancellationToken: Arg.Any<CancellationToken>());

        // Verify ACK removal from DLQ
        await _channel.Received(1).BasicAckAsync(202, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PurgeQueueAsync_ShouldInvokeChannelQueuePurgeAndReturnCount()
    {
        _channel.QueuePurgeAsync(_options.DlqQueueName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(42u));

        var result = await _sut.PurgeQueueAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42u);
        await _channel.Received(1).QueuePurgeAsync(_options.DlqQueueName, Arg.Any<CancellationToken>());
    }
}
