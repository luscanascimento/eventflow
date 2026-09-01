using System.Data.Common;
using EventFlow.Application.Abstractions;
using EventFlow.Domain.Common;
using EventFlow.Domain.Outbox;
using EventFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Xunit;

namespace EventFlow.IntegrationTests.Fixtures;

/// <summary>
/// Fast, isolated WebApplicationFactory for API-level integration tests (routing, middleware, validation, problem details),
/// backed by an in-memory SQLite connection and mocked transport dependencies.
/// </summary>
public sealed class EventFlowApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private DbConnection? _connection;

    public IEventPublisher PublisherMock { get; } = Substitute.For<IEventPublisher>();
    public IDeadLetterManager DeadLetterManagerMock { get; } = Substitute.For<IDeadLetterManager>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            var inMemoryConfig = new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "DataSource=:memory:",
                ["Security:ApiKey"] = TestAuthenticationDefaults.ValidApiKey,
                ["Redis:Enabled"] = "false"
            };

            configBuilder.AddInMemoryCollection(inMemoryConfig);
        });

        builder.ConfigureServices(services =>
        {
            var descriptorsToRemove = services
                .Where(d => d.ServiceType.FullName?.Contains("Npgsql", StringComparison.Ordinal) == true ||
                            d.ImplementationType?.FullName?.Contains("Npgsql", StringComparison.Ordinal) == true ||
                            d.ServiceType.FullName?.Contains("EntityFramework", StringComparison.Ordinal) == true ||
                            d.ImplementationType?.FullName?.Contains("EntityFramework", StringComparison.Ordinal) == true ||
                            d.ServiceType == typeof(EventFlowDbContext) ||
                            d.ServiceType == typeof(DbContextOptions) ||
                            d.ServiceType == typeof(DbContextOptions<EventFlowDbContext>))
                .ToList();

            foreach (var d in descriptorsToRemove)
            {
                services.Remove(d);
            }

            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            services.AddDbContext<EventFlowDbContext>(options =>
            {
                options.UseSqlite(_connection);
            });

            services.AddScoped<IEventStore, EfEventStore>();
            services.AddScoped<IOutboxStore, EfOutboxStore>();

            // Replace EventPublisher and DeadLetterManager with mock instances for isolated API testing
            services.RemoveAll<IEventPublisher>();
            services.AddScoped(_ => PublisherMock);

            services.RemoveAll<IDeadLetterManager>();
            services.AddScoped(_ => DeadLetterManagerMock);
        });

        builder.UseEnvironment("Testing");
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EventFlowDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        // Default mock behaviors
        PublisherMock.PublishAsync(Arg.Any<OutboxMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
    }

    public new async Task DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }

        await base.DisposeAsync();
    }
}
