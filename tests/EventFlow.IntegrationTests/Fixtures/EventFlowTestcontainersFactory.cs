using System.Globalization;
using EventFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventFlow.IntegrationTests.Fixtures;

/// <summary>
/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> configuring ASP.NET Core against real Testcontainers
/// PostgreSQL, RabbitMQ, and Redis instances.
/// </summary>
public sealed class EventFlowTestcontainersFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public DockerTestcontainersFixture ContainersFixture { get; }

    public EventFlowTestcontainersFactory(DockerTestcontainersFixture containersFixture)
    {
        ContainersFixture = containersFixture;
    }

    public EventFlowTestcontainersFactory() : this(new DockerTestcontainersFixture())
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            var inMemoryConfig = new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = ContainersFixture.PostgreSqlConnectionString,
                ["RabbitMQ:HostName"] = ContainersFixture.RabbitMqHost,
                ["RabbitMQ:Port"] = ContainersFixture.RabbitMqPort.ToString(CultureInfo.InvariantCulture),
                ["RabbitMQ:UserName"] = DockerTestcontainersFixture.RabbitMqUsername,
                ["RabbitMQ:Password"] = DockerTestcontainersFixture.RabbitMqPassword,
                ["Redis:ConnectionString"] = ContainersFixture.RedisConnectionString,
                ["Redis:Enabled"] = "true",
                ["Security:ApiKey"] = TestAuthenticationDefaults.ValidApiKey
            };

            configBuilder.AddInMemoryCollection(inMemoryConfig);
        });

        builder.UseEnvironment("Testing");
    }

    public async Task InitializeAsync()
    {
        if (!DockerTestcontainersFixture.IsDockerRunning)
        {
            return;
        }

        await ContainersFixture.InitializeAsync();

        // Apply EF Core migrations to ensure schema is fully created in PostgreSQL container
        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EventFlowDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await ContainersFixture.DisposeAsync();
    }
}
