using EventFlow.Application.DeadLetter;
using FluentAssertions;
using Xunit;

namespace EventFlow.UnitTests.DeadLetter;

public class DeadLetterMessageTests
{
    [Fact]
    public void DeadLetterMessage_ShouldInitializePropertiesCorrectly()
    {
        var now = DateTimeOffset.UtcNow;
        var headers = new Dictionary<string, string> { ["custom"] = "value" };

        var message = new DeadLetterMessage(
            MessageId: "msg-001",
            RoutingKey: "order.created.v1",
            Payload: "{\"orderId\":\"ord-1\"}",
            TenantId: "tenant-acme",
            EventType: "order.created.v1",
            TraceParent: "00-trace-01",
            ExceptionMessage: "Internal Server Error",
            ExceptionType: "HttpRequestException",
            FailedAt: now,
            RetryCount: 3,
            Headers: headers);

        message.MessageId.Should().Be("msg-001");
        message.RoutingKey.Should().Be("order.created.v1");
        message.Payload.Should().Be("{\"orderId\":\"ord-1\"}");
        message.TenantId.Should().Be("tenant-acme");
        message.EventType.Should().Be("order.created.v1");
        message.TraceParent.Should().Be("00-trace-01");
        message.ExceptionMessage.Should().Be("Internal Server Error");
        message.ExceptionType.Should().Be("HttpRequestException");
        message.FailedAt.Should().Be(now);
        message.RetryCount.Should().Be(3);
        message.Headers.Should().ContainKey("custom");
    }
}
