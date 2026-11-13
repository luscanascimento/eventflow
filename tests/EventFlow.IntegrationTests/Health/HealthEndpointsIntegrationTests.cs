using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EventFlow.IntegrationTests.Fixtures;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace EventFlow.IntegrationTests.Health;

public sealed class HealthEndpointsIntegrationTests : IClassFixture<EventFlowApiFactory>
{
    private readonly EventFlowApiFactory _factory;
    private readonly HttpClient _client;

    public HealthEndpointsIntegrationTests(EventFlowApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetLiveness_Returns200Ok_WithHealthyStatus()
    {
        // Act
        var response = await _client.GetAsync("/health/live");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<HealthStatusResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("Healthy");
    }

    [Fact]
    public async Task GetReadiness_WhenAllDependenciesHealthy_Returns200Ok_WithReadyStatus()
    {
        // Arrange
        _factory.RabbitMqConnectionMock.IsConnected.Returns(true);

        // Act
        var response = await _client.GetAsync("/health/ready");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ReadinessResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("Ready");
        body.Dependencies.Database.Should().Be("Healthy");
        body.Dependencies.Broker.Should().Be("Healthy");
    }

    [Fact]
    public async Task GetHealth_Returns200Ok_WithHealthyStatus()
    {
        // Act
        var response = await _client.GetAsync("/health");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<HealthStatusResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("Healthy");
    }

    [Fact]
    public async Task GetReadiness_WhenBrokerUnhealthy_Returns503ServiceUnavailable()
    {
        try
        {
            // Arrange
            _factory.RabbitMqConnectionMock.IsConnected.Returns(false);
            _factory.RabbitMqConnectionMock.CreateChannelAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromException<RabbitMQ.Client.IChannel>(new InvalidOperationException("Broker unreachable")));

            // Act
            var response = await _client.GetAsync("/health/ready");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var body = await response.Content.ReadFromJsonAsync<ReadinessResponse>();
            body.Should().NotBeNull();
            body!.Status.Should().Be("Unhealthy");
            body.Dependencies.Broker.Should().Be("Unhealthy");
        }
        finally
        {
            _factory.RabbitMqConnectionMock.IsConnected.Returns(true);
        }
    }

    private sealed record HealthStatusResponse(
        [property: JsonPropertyName("status")] string Status);

    private sealed record ReadinessResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("dependencies")] DependenciesStatus Dependencies);

    private sealed record DependenciesStatus(
        [property: JsonPropertyName("database")] string Database,
        [property: JsonPropertyName("broker")] string Broker,
        [property: JsonPropertyName("cache")] string Cache);
}
