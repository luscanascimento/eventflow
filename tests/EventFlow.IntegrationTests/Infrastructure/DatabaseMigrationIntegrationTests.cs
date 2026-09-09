using EventFlow.Infrastructure.Persistence;
using EventFlow.IntegrationTests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EventFlow.IntegrationTests.Infrastructure;

[Collection("DockerContainers")]
public sealed class DatabaseMigrationIntegrationTests : IAsyncLifetime
{
    private readonly DockerTestcontainersFixture _fixture;

    public DatabaseMigrationIntegrationTests(DockerTestcontainersFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        if (!DockerTestcontainersFixture.IsDockerRunning)
        {
            return;
        }

        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [DockerFact]
    public async Task MigrateAsync_OnEmptyDatabase_CreatesAllTablesAndAppliesAllMigrations()
    {
        // Arrange: Connect to a fresh, unmigrated database
        string uniqueDbName = $"eventflow_mig_test_{Guid.NewGuid():N}";
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.PostgreSqlConnectionString)
        {
            Database = uniqueDbName
        };

        // Create the empty database
        using (var masterConn = new Npgsql.NpgsqlConnection(_fixture.PostgreSqlConnectionString))
        {
            await masterConn.OpenAsync();
            using var cmd = masterConn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{uniqueDbName}\";";
            await cmd.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<EventFlowDbContext>()
            .UseNpgsql(builder.ConnectionString)
            .Options;

        await using var dbContext = new EventFlowDbContext(options);

        // Act: Apply all migrations
        var pendingBefore = await dbContext.Database.GetPendingMigrationsAsync();
        pendingBefore.Should().NotBeEmpty();

        await dbContext.Database.MigrateAsync();

        // Assert: All migrations are applied and tables exist
        var pendingAfter = await dbContext.Database.GetPendingMigrationsAsync();
        pendingAfter.Should().BeEmpty();

        var appliedMigrations = await dbContext.Database.GetAppliedMigrationsAsync();
        appliedMigrations.Should().Contain("20260829182852_InitialCreate");
        appliedMigrations.Should().Contain("20260830150255_AddProcessedEventsTable");
        appliedMigrations.Should().Contain("20260908230000_AddLeaseAndConcurrencySupport");

        // Verify entities can be queried
        bool eventsCanQuery = await dbContext.Events.AnyAsync() == false;
        bool outboxCanQuery = await dbContext.OutboxMessages.AnyAsync() == false;
        bool processedCanQuery = await dbContext.ProcessedEvents.AnyAsync() == false;

        eventsCanQuery.Should().BeTrue();
        outboxCanQuery.Should().BeTrue();
        processedCanQuery.Should().BeTrue();
    }
}
