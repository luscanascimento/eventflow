using System.Text;
using EventFlow.Infrastructure.Messaging;
using FluentAssertions;
using Xunit;

namespace EventFlow.UnitTests.Infrastructure;

public class AmqpHeaderParserTests
{
    [Fact]
    public void GetString_WhenValueIsByteArray_ShouldDecodeUtf8Correctly()
    {
        var headers = new Dictionary<string, object?>
        {
            ["tenant_id"] = Encoding.UTF8.GetBytes("tenant-acme"),
            ["event_type"] = Encoding.UTF8.GetBytes("payment.settled.v1")
        };

        string? tenantId = AmqpHeaderParser.GetString(headers, "tenant_id");
        string? eventType = AmqpHeaderParser.GetString(headers, "event_type");

        tenantId.Should().Be("tenant-acme");
        eventType.Should().Be("payment.settled.v1");
    }

    [Fact]
    public void GetString_WhenValueIsString_ShouldReturnString()
    {
        var headers = new Dictionary<string, object?>
        {
            ["traceparent"] = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
        };

        string? traceParent = AmqpHeaderParser.GetString(headers, "traceparent");

        traceParent.Should().Be("00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");
    }

    [Fact]
    public void GetString_WhenValueIsReadOnlyMemory_ShouldDecodeCorrectly()
    {
        ReadOnlyMemory<byte> memory = Encoding.UTF8.GetBytes("outbox-12345");
        var headers = new Dictionary<string, object?>
        {
            ["outbox_id"] = memory
        };

        string? outboxId = AmqpHeaderParser.GetString(headers, "outbox_id");

        outboxId.Should().Be("outbox-12345");
    }

    [Fact]
    public void GetString_WhenKeyNotFoundOrNull_ShouldReturnNull()
    {
        var headers = new Dictionary<string, object?>
        {
            ["null_key"] = null
        };

        AmqpHeaderParser.GetString(headers, "null_key").Should().BeNull();
        AmqpHeaderParser.GetString(headers, "non_existing").Should().BeNull();
        AmqpHeaderParser.GetString((Dictionary<string, object?>?)null, "any").Should().BeNull();
    }

    [Fact]
    public void ExtractAll_WhenHeadersPresent_ShouldReturnSanitizedStringDictionary()
    {
        var headers = new Dictionary<string, object?>
        {
            ["tenant_id"] = Encoding.UTF8.GetBytes("tenant-1"),
            ["version"] = 1,
            ["traceparent"] = "00-123-456-01"
        };

        var extracted = AmqpHeaderParser.ExtractAll(headers);

        extracted.Should().HaveCount(3);
        extracted["tenant_id"].Should().Be("tenant-1");
        extracted["version"].Should().Be("1");
        extracted["traceparent"].Should().Be("00-123-456-01");
    }

    [Fact]
    public void ExtractAll_WhenHeadersNullOrEmpty_ShouldReturnEmptyDictionary()
    {
        AmqpHeaderParser.ExtractAll((Dictionary<string, object?>?)null).Should().BeEmpty();
        AmqpHeaderParser.ExtractAll(new Dictionary<string, object?>()).Should().BeEmpty();
    }
}
