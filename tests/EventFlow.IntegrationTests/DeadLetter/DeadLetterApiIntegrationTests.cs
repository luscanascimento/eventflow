using System.Net;
using System.Net.Http.Json;
using EventFlow.Application.DeadLetter;
using EventFlow.Domain.Common;
using EventFlow.IntegrationTests.Fixtures;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace EventFlow.IntegrationTests.DeadLetter;

public sealed class DeadLetterApiIntegrationTests : IClassFixture<EventFlowApiFactory>
{
    private readonly EventFlowApiFactory _factory;
    private readonly HttpClient _client;

    public DeadLetterApiIntegrationTests(EventFlowApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task PeekDlq_ReturnsOk_WithPeekingResults()
    {
        // Arrange
        var sampleMessages = new List<DeadLetterMessage>
        {
            new(
                MessageId: "msg-1",
                RoutingKey: "payment.settled.v1",
                Payload: "{\"orderId\":\"123\"}",
                TenantId: "tenant_dlq",
                EventType: "payment.settled.v1",
                TraceParent: null,
                ExceptionMessage: "rejected",
                ExceptionType: "InvalidOperationException",
                FailedAt: DateTimeOffset.UtcNow,
                RetryCount: 3,
                Headers: new Dictionary<string, string>())
        };

        _factory.DeadLetterManagerMock
            .PeekMessagesAsync(5, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DeadLetterMessage>>(sampleMessages));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/dlq?count=5");
        request.Headers.Add(TestAuthenticationDefaults.ApiKeyHeaderName, TestAuthenticationDefaults.ValidApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DlqPeekResponse>();
        body.Should().NotBeNull();
        body!.Count.Should().Be(1);
        body.Messages.Should().HaveCount(1);
        body.Messages[0].MessageId.Should().Be("msg-1");
    }

    [Fact]
    public async Task ReplayDlq_ReturnsOk_WithReplayedCount()
    {
        // Arrange
        _factory.DeadLetterManagerMock
            .ReplayMessagesAsync(20, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(15)));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dlq/replay?maxCount=20");
        request.Headers.Add(TestAuthenticationDefaults.ApiKeyHeaderName, TestAuthenticationDefaults.ValidApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DlqReplayResponse>();
        body.Should().NotBeNull();
        body!.ReplayedCount.Should().Be(15);
        body.Status.Should().Be("Replayed");
    }

    [Fact]
    public async Task PurgeDlq_ReturnsOk_WithPurgedCount()
    {
        // Arrange
        _factory.DeadLetterManagerMock
            .PurgeQueueAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success(42u)));

        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/dlq");
        request.Headers.Add(TestAuthenticationDefaults.ApiKeyHeaderName, TestAuthenticationDefaults.ValidApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DlqPurgeResponse>();
        body.Should().NotBeNull();
        body!.PurgedCount.Should().Be(42u);
        body.Status.Should().Be("Purged");
    }

    private sealed record DlqPeekResponse(int Count, IReadOnlyList<DeadLetterMessage> Messages);
    private sealed record DlqReplayResponse(int ReplayedCount, string Status);
    private sealed record DlqPurgeResponse(uint PurgedCount, string Status);
}
