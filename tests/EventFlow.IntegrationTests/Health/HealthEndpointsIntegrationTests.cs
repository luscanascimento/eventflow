using System.Net;
using System.Net.Http.Json;
using EventFlow.IntegrationTests.Fixtures;
using FluentAssertions;
using Xunit;

namespace EventFlow.IntegrationTests.Health;

public sealed class HealthEndpointsIntegrationTests : IClassFixture<EventFlowApiFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointsIntegrationTests(EventFlowApiFactory factory)
    {
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
    public async Task GetReadiness_Returns200Ok_WithReadyStatus()
    {
        // Act
        var response = await _client.GetAsync("/health/ready");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<HealthStatusResponse>();
        body.Should().NotBeNull();
        body!.Status.Should().Be("Ready");
    }

    private sealed record HealthStatusResponse(string Status);
}
