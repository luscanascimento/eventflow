using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventFlow.Application.Events.Commands;
using EventFlow.Domain.Outbox;
using EventFlow.Infrastructure.Persistence;
using EventFlow.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventFlow.IntegrationTests.Api;

public sealed class EventIngestionApiIntegrationTests : IClassFixture<EventFlowApiFactory>
{
    private readonly EventFlowApiFactory _factory;
    private readonly HttpClient _client;

    public EventIngestionApiIntegrationTests(EventFlowApiFactory factory)
    {
        _factory = factory;
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task IngestEvent_WithValidPayload_Returns202Accepted_AndPersistsToDatabase()
    {
        // Arrange
        var command = new IngestEventCommand(
            EventId: null,
            EventType: "payment.settled.v1",
            Version: 1,
            Source: "payment-gateway",
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: $"corr_{Guid.NewGuid():N}",
            TraceParent: null,
            TenantId: "tenant_integration_test",
            IdempotencyKey: $"ik_{Guid.NewGuid():N}",
            Payload: JsonSerializer.SerializeToElement(new { orderId = "ord_1001", amount = 199.99m, currency = "BRL" }),
            Metadata: new Dictionary<string, string> { ["env"] = "integration-testing" });

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events")
        {
            Content = JsonContent.Create(command)
        };
        request.Headers.Add(TestAuthenticationDefaults.ApiKeyHeaderName, TestAuthenticationDefaults.ValidApiKey);
        request.Headers.Add(TestAuthenticationDefaults.TenantIdHeaderName, command.TenantId);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location.Should().NotBeNull();

        var body = await response.Content.ReadFromJsonAsync<IngestEventResponse>();
        body.Should().NotBeNull();
        body!.EventId.Should().NotBeEmpty();
        body.Status.Should().Be("Accepted");

        // Verify database persistence
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventFlowDbContext>();

        var persistedEvent = await db.Events.FirstOrDefaultAsync(e => e.EventId == body.EventId);
        persistedEvent.Should().NotBeNull();
        persistedEvent!.TenantId.Should().Be("tenant_integration_test");
        persistedEvent.EventType.Should().Be("payment.settled.v1");

        var outboxMessage = await db.OutboxMessages.FirstOrDefaultAsync(o => o.EventId == body.EventId);
        outboxMessage.Should().NotBeNull();
        outboxMessage!.Status.Should().Be(OutboxStatus.Pending);
    }

    [Fact]
    public async Task IngestEvent_WithInvalidApiKey_Returns401Unauthorized_WithProblemDetails()
    {
        // Arrange
        var command = new IngestEventCommand(
            EventId: null,
            EventType: "order.created.v1",
            Version: 1,
            Source: "store-service",
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: $"corr_{Guid.NewGuid():N}",
            TraceParent: null,
            TenantId: "tenant_test",
            IdempotencyKey: $"ik_{Guid.NewGuid():N}",
            Payload: JsonSerializer.SerializeToElement(new { item = "book" }),
            Metadata: null);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events")
        {
            Content = JsonContent.Create(command)
        };
        request.Headers.Add(TestAuthenticationDefaults.ApiKeyHeaderName, TestAuthenticationDefaults.InvalidApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be((int)HttpStatusCode.Unauthorized);
        problem.Title.Should().Be("Unauthorized");
    }

    [Fact]
    public async Task IngestEvent_WithoutApiKey_Returns401Unauthorized()
    {
        // Arrange
        var command = new IngestEventCommand(
            EventId: null,
            EventType: "order.created.v1",
            Version: 1,
            Source: "store-service",
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: $"corr_{Guid.NewGuid():N}",
            TraceParent: null,
            TenantId: "tenant_test",
            IdempotencyKey: $"ik_{Guid.NewGuid():N}",
            Payload: JsonSerializer.SerializeToElement(new { item = "book" }),
            Metadata: null);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events")
        {
            Content = JsonContent.Create(command)
        };

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("", "tenant_1", "source_1")]
    [InlineData("event.type", "", "source_1")]
    [InlineData("event.type", "tenant_1", "")]
    public async Task IngestEvent_WithMissingRequiredFields_Returns400BadRequest(
        string eventType,
        string tenantId,
        string source)
    {
        // Arrange
        var command = new IngestEventCommand(
            EventId: null,
            EventType: eventType,
            Version: 1,
            Source: source,
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: $"corr_{Guid.NewGuid():N}",
            TraceParent: null,
            TenantId: tenantId,
            IdempotencyKey: $"ik_{Guid.NewGuid():N}",
            Payload: JsonSerializer.SerializeToElement(new { test = "val" }),
            Metadata: null);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events")
        {
            Content = JsonContent.Create(command)
        };
        request.Headers.Add(TestAuthenticationDefaults.ApiKeyHeaderName, TestAuthenticationDefaults.ValidApiKey);

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        problem.Title.Should().Be("Validation Failure");
    }

    [Fact]
    public async Task IngestEvent_WithDuplicateIdempotencyKey_Returns409Conflict()
    {
        // Arrange
        string idempotencyKey = $"ik_dup_{Guid.NewGuid():N}";
        string tenantId = $"tenant_dup_{Guid.NewGuid():N}";

        var command = new IngestEventCommand(
            EventId: null,
            EventType: "payment.settled.v1",
            Version: 1,
            Source: "payment-gateway",
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: $"corr_{Guid.NewGuid():N}",
            TraceParent: null,
            TenantId: tenantId,
            IdempotencyKey: idempotencyKey,
            Payload: JsonSerializer.SerializeToElement(new { amount = 50.00m }),
            Metadata: null);

        var request1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events")
        {
            Content = JsonContent.Create(command)
        };
        request1.Headers.Add(TestAuthenticationDefaults.ApiKeyHeaderName, TestAuthenticationDefaults.ValidApiKey);

        var request2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events")
        {
            Content = JsonContent.Create(command)
        };
        request2.Headers.Add(TestAuthenticationDefaults.ApiKeyHeaderName, TestAuthenticationDefaults.ValidApiKey);

        // Act
        var response1 = await _client.SendAsync(request1);
        var response2 = await _client.SendAsync(request2);

        // Assert
        response1.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response2.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await response2.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be((int)HttpStatusCode.Conflict);
        problem.Title.Should().Be("Conflict");
    }

    [Fact]
    public async Task IngestEvent_Response_ContainsAllOwaspSecurityHeaders()
    {
        // Arrange
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");

        // Act
        var response = await _client.SendAsync(request);

        // Assert
        response.Headers.Contains("X-Content-Type-Options").Should().BeTrue();
        response.Headers.GetValues("X-Content-Type-Options").First().Should().Be("nosniff");

        response.Headers.Contains("X-Frame-Options").Should().BeTrue();
        response.Headers.GetValues("X-Frame-Options").First().Should().Be("DENY");

        response.Headers.Contains("Content-Security-Policy").Should().BeTrue();
        response.Headers.GetValues("Content-Security-Policy").First().Should().Contain("default-src 'none'");

        response.Headers.Contains("Strict-Transport-Security").Should().BeTrue();
        response.Headers.GetValues("Strict-Transport-Security").First().Should().Contain("max-age=31536000");

        response.Headers.Contains("Referrer-Policy").Should().BeTrue();
        response.Headers.GetValues("Referrer-Policy").First().Should().Be("no-referrer");

        response.Headers.Contains("Permissions-Policy").Should().BeTrue();
        response.Headers.GetValues("Permissions-Policy").First().Should().Contain("camera=()");

        response.Headers.Contains("X-XSS-Protection").Should().BeTrue();
        response.Headers.GetValues("X-XSS-Protection").First().Should().Be("0");
    }
}
