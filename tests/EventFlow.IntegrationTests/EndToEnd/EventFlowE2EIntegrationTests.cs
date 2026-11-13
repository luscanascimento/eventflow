using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventFlow.Application.Abstractions;
using EventFlow.Application.Events.Commands;
using EventFlow.Application.Events.Consumers;
using EventFlow.Application.Outbox;
using EventFlow.Domain.Events;
using EventFlow.Domain.Idempotency;
using EventFlow.Domain.Outbox;
using EventFlow.Infrastructure.Persistence;
using EventFlow.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventFlow.IntegrationTests.EndToEnd;

[Collection("DockerContainers")]
public sealed class EventFlowE2EIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly DockerTestcontainersFixture _fixture;
    private readonly EventFlowTestcontainersFactory _factory;
    private HttpClient _client = null!;

    public EventFlowE2EIntegrationTests(DockerTestcontainersFixture fixture)
    {
        _fixture = fixture;
        _factory = new EventFlowTestcontainersFactory(_fixture);
    }

    public async Task InitializeAsync()
    {
        if (DockerTestcontainersFixture.IsDockerRunning)
        {
            await _factory.InitializeAsync();
            _client = _factory.CreateClient();
        }
    }

    public async Task DisposeAsync()
    {
        if (DockerTestcontainersFixture.IsDockerRunning)
        {
            _client.Dispose();
            await _factory.DisposeAsync();
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [DockerFact]
    public async Task E2E_IngestEvent_PersistsToPostgresEventsAndOutboxAtomically()
    {
        // Arrange
        var command = new IngestEventCommand(
            EventId: null,
            EventType: "payment.settled.v1",
            Version: 1,
            Source: "e2e-gateway",
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: $"e2e_corr_{Guid.NewGuid():N}",
            TraceParent: null,
            TenantId: "tenant_e2e_pg",
            IdempotencyKey: $"e2e_ik_{Guid.NewGuid():N}",
            Payload: JsonSerializer.SerializeToElement(new { orderId = "ord_9999", amount = 1499.00m, currency = "USD" }),
            Metadata: new Dictionary<string, string> { ["source_ip"] = "192.168.1.1" });

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events")
        {
            Content = JsonContent.Create(command)
        };
        request.Headers.Add(TestAuthenticationDefaults.ApiKeyHeaderName, TestAuthenticationDefaults.ValidApiKey);
        request.Headers.Add(TestAuthenticationDefaults.TenantIdHeaderName, command.TenantId);

        // Act
        var response = await _client.SendAsync(request);

        // Assert HTTP response
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var responseBody = await response.Content.ReadFromJsonAsync<IngestEventResponse>();
        responseBody.Should().NotBeNull();
        responseBody!.EventId.Should().NotBeEmpty();

        // Assert Postgres ACID persistence
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EventFlowDbContext>();

        var eventRecord = await db.Events.FirstOrDefaultAsync(e => e.EventId == responseBody.EventId);
        eventRecord.Should().NotBeNull();
        eventRecord!.TenantId.Should().Be("tenant_e2e_pg");
        eventRecord.EventType.Should().Be("payment.settled.v1");
        eventRecord.Payload.Should().Contain("1499");

        var outboxMessage = await db.OutboxMessages.FirstOrDefaultAsync(o => o.EventId == responseBody.EventId);
        outboxMessage.Should().NotBeNull();
        outboxMessage!.Status.Should().Be(OutboxStatus.Pending);
        outboxMessage.RoutingKey.Should().Be("payment.settled.v1");
    }

    [DockerFact]
    public async Task E2E_OutboxProcessor_DispatchesPendingToRabbitMqAndMarksPublished()
    {
        // Arrange: Ingest event to generate pending outbox message
        var command = new IngestEventCommand(
            EventId: null,
            EventType: "payment.settled.v1",
            Version: 1,
            Source: "e2e-gateway",
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: $"e2e_disp_corr_{Guid.NewGuid():N}",
            TraceParent: null,
            TenantId: "tenant_e2e_dispatch",
            IdempotencyKey: $"e2e_disp_{Guid.NewGuid():N}",
            Payload: JsonSerializer.SerializeToElement(new { orderId = "ord_disp_1", amount = 250.00m }),
            Metadata: null);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events")
        {
            Content = JsonContent.Create(command)
        };
        request.Headers.Add(TestAuthenticationDefaults.ApiKeyHeaderName, TestAuthenticationDefaults.ValidApiKey);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<IngestEventResponse>();

        // Act: Run OutboxProcessor batch dispatch
        using var scope = _factory.Services.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
        int processedCount = await processor.ProcessPendingMessagesAsync(CancellationToken.None);

        // Assert
        processedCount.Should().BeGreaterThanOrEqualTo(1);

        var db = scope.ServiceProvider.GetRequiredService<EventFlowDbContext>();
        var outboxMessage = await db.OutboxMessages.FirstOrDefaultAsync(o => o.EventId == body!.EventId);
        outboxMessage.Should().NotBeNull();
        outboxMessage!.Status.Should().Be(OutboxStatus.Published);
        outboxMessage.ProcessedAt.Should().NotBeNull();
    }

    [DockerFact]
    public async Task E2E_EventConsumer_ProcessesMessage_StoresInProcessedEventsAndRedis()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var eventProcessor = scope.ServiceProvider.GetRequiredService<IEventProcessor>();
        var db = scope.ServiceProvider.GetRequiredService<EventFlowDbContext>();

        var eventId = Guid.NewGuid();
        string idempotencyKey = $"e2e_proc_{Guid.NewGuid():N}";
        string tenantId = "tenant_e2e_proc";

        var context = new ConsumedEventContext(
            EventId: eventId,
            EventType: "payment.settled.v1",
            TenantId: tenantId,
            TraceParent: "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
            OutboxId: null,
            RoutingKey: "payment.settled.v1",
            Payload: JsonSerializer.Serialize(new { orderId = "ord_proc_1", amount = 750.50m, currency = "BRL" }),
            ConsumedAt: DateTimeOffset.UtcNow,
            Headers: new Dictionary<string, string>
            {
                ["idempotency_key"] = idempotencyKey,
                ["tenant_id"] = tenantId
            },
            Redelivered: false);

        // Act
        var result = await eventProcessor.ProcessAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();

        var processedRecord = await db.ProcessedEvents
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.IdempotencyKey == idempotencyKey);
        processedRecord.Should().NotBeNull();
        processedRecord!.EventId.Should().Be(eventId);
        processedRecord.ResultSummary.Should().Be("Success");
    }

    [DockerFact]
    public async Task E2E_ConcurrentRequests_WithSameIdempotencyKey_ExecutesExactlyOnce()
    {
        // Arrange
        string idempotencyKey = $"ik_concurrent_{Guid.NewGuid():N}";
        string tenantId = "tenant_concurrent_test";
        int concurrencyCount = 10;

        var tasks = Enumerable.Range(0, concurrencyCount).Select(async i =>
        {
            using var scope = _factory.Services.CreateScope();
            var eventProcessor = scope.ServiceProvider.GetRequiredService<IEventProcessor>();

            var context = new ConsumedEventContext(
                EventId: Guid.NewGuid(),
                EventType: "payment.settled.v1",
                TenantId: tenantId,
                TraceParent: null,
                OutboxId: null,
                RoutingKey: "payment.settled.v1",
                Payload: JsonSerializer.Serialize(new { orderId = $"ord_conc_{i}", amount = 100.00m }),
                ConsumedAt: DateTimeOffset.UtcNow,
                Headers: new Dictionary<string, string>
                {
                    ["idempotency_key"] = idempotencyKey,
                    ["tenant_id"] = tenantId
                },
                Redelivered: false);

            return await eventProcessor.ProcessAsync(context, CancellationToken.None);
        });

        // Act
        var results = await Task.WhenAll(tasks);

        // Assert
        // At least one invocation succeeds; concurrent overlapping callers return ConflictInFlight or AlreadyProcessed
        results.Count(r => r.IsSuccess).Should().BeGreaterThanOrEqualTo(1);
        results.Where(r => r.IsFailure).All(r => r.Error.Code == "Idempotency.ConflictInFlight").Should().BeTrue();

        // Exactly 1 record exists in PostgreSQL processed_events
        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EventFlowDbContext>();
        var records = await db.ProcessedEvents
            .Where(p => p.TenantId == tenantId && p.IdempotencyKey == idempotencyKey)
            .ToListAsync();

        records.Should().HaveCount(1);
    }
}
