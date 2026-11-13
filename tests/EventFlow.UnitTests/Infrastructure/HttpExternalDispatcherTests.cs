using System.Net;
using EventFlow.Application.Abstractions;
using EventFlow.Domain.Common;
using EventFlow.Infrastructure.Resilience;
using EventFlow.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Polly;
using Xunit;

namespace EventFlow.UnitTests.Infrastructure;

public sealed class HttpExternalDispatcherTests : IDisposable
{
    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Handler { get; set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (Handler != null)
            {
                return Handler(request, cancellationToken);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ok\"}")
            });
        }
    }

    private readonly TestHttpMessageHandler _handler = new();
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpExternalDispatcher> _logger = Substitute.For<ILogger<HttpExternalDispatcher>>();
    private readonly ResilienceOptions _options = new()
    {
        MaxRetryAttempts = 1,
        BaseDelaySeconds = 0.01,
        MaxDelaySeconds = 0.05,
        CircuitBreakerFailureRatio = 0.5,
        CircuitBreakerMinimumThroughput = 5,
        AttemptTimeoutSeconds = 2,
        TotalTimeoutSeconds = 5
    };
    private readonly IOptions<SsrfOptions> _ssrfOptions = Options.Create(new SsrfOptions
    {
        AllowedHosts = { "api.partner.com" }
    });
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;
    private readonly HttpExternalDispatcher _sut;

    public HttpExternalDispatcherTests()
    {
        _httpClient = new HttpClient(_handler);
        _pipeline = ResiliencePipelineFactory.CreateExternalDispatchPipeline(_options);
        _sut = new HttpExternalDispatcher(_httpClient, _pipeline, _ssrfOptions, _logger);
    }

    [Theory]
    [InlineData("ftp://example.com/webhook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://example.com")]
    public async Task DispatchAsync_WhenInvalidScheme_ShouldReturnValidationFailure(string invalidUrl)
    {
        var request = new ExternalDispatchRequest(
            TargetUrl: new Uri(invalidUrl),
            Method: HttpMethod.Post,
            Payload: "{}");

        var result = await _sut.DispatchAsync(request);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ExternalDispatcher.BlockedTarget");
        result.Error.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public async Task DispatchAsync_WhenSuccessful200_ShouldReturnSuccessWithResponseDetails()
    {
        _handler.Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"result\":\"accepted\"}")
        });

        var request = new ExternalDispatchRequest(
            TargetUrl: new Uri("https://api.partner.com/webhooks/orders"),
            Method: HttpMethod.Post,
            Payload: "{\"orderId\":\"ord-1\"}",
            TraceParent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01");

        var result = await _sut.DispatchAsync(request);

        result.IsSuccess.Should().BeTrue();
        result.Value.StatusCode.Should().Be(200);
        result.Value.ResponseBody.Should().Be("{\"result\":\"accepted\"}");
        result.Value.Elapsed.Should().BeGreaterThan(TimeSpan.Zero);

        _handler.LastRequest.Should().NotBeNull();
        _handler.LastRequest!.Headers.Contains("traceparent").Should().BeTrue();
    }

    [Fact]
    public async Task DispatchAsync_WhenNetworkFailsPermanently_ShouldReturnNetworkErrorFailure()
    {
        _handler.Handler = (req, ct) => throw new HttpRequestException("Connection refused");

        var request = new ExternalDispatchRequest(
            TargetUrl: new Uri("https://api.partner.com/webhook"),
            Method: HttpMethod.Post,
            Payload: "{}");

        var result = await _sut.DispatchAsync(request);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Resilience.NetworkError");
        result.Error.Message.Should().Contain("Connection refused");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task DispatchAsync_WhenNonSuccessStatusCode_ShouldReturnHttpErrorFailure(HttpStatusCode statusCode)
    {
        _handler.Handler = (req, ct) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("{\"error\":\"upstream failure\"}")
        });

        var request = new ExternalDispatchRequest(
            TargetUrl: new Uri("https://api.partner.com/webhook"),
            Method: HttpMethod.Post,
            Payload: "{}");

        var result = await _sut.DispatchAsync(request);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Resilience.HttpError");
        result.Error.Message.Should().Contain(((int)statusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }
}
