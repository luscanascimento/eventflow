using EventFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Information);
});

string connectionString = configuration.GetConnectionString("DefaultConnection")
    ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not found for migration runner.");

services.AddDbContext<EventFlowDbContext>(options =>
{
    options.UseNpgsql(connectionString, npgsqlOptions =>
    {
        npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(5),
            errorCodesToAdd: null);
        npgsqlOptions.MigrationsAssembly(typeof(EventFlowDbContext).Assembly.FullName);
    });
});

var serviceProvider = services.BuildServiceProvider();
var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

MigrationLoggers.LogStarting(logger);

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var cancellationToken = cts.Token;

const int maxAttempts = 10;
for (int attempt = 1; attempt <= maxAttempts; attempt++)
{
    try
    {
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EventFlowDbContext>();

        MigrationLoggers.LogTestingConnectivity(logger, attempt, maxAttempts);
        bool canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
        if (!canConnect)
        {
            MigrationLoggers.LogDbNotReady(logger);
            await Task.Delay(3000, cancellationToken);
            continue;
        }

        var pendingMigrations = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pendingMigrations.Count == 0)
        {
            MigrationLoggers.LogAlreadyUpToDate(logger);
        }
        else
        {
            MigrationLoggers.LogApplyingMigrations(logger, pendingMigrations.Count, string.Join(", ", pendingMigrations));
            await dbContext.Database.MigrateAsync(cancellationToken);
            MigrationLoggers.LogMigrationsApplied(logger);
        }

        MigrationLoggers.LogCompleted(logger);
        return 0;
    }
    catch (Exception ex) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
    {
        MigrationLoggers.LogAttemptFailed(logger, attempt, ex);
        await Task.Delay(3000, cancellationToken);
    }
    catch (Exception ex)
    {
        MigrationLoggers.LogFatalError(logger, ex);
        return 1;
    }
}

MigrationLoggers.LogExhausted(logger);
return 1;

public partial class Program;

internal static partial class MigrationLoggers
{
    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Starting EventFlow Database Migration Runner...")]
    public static partial void LogStarting(ILogger logger);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information, Message = "Testing database connectivity (Attempt {Attempt}/{MaxAttempts})...")]
    public static partial void LogTestingConnectivity(ILogger logger, int attempt, int maxAttempts);

    [LoggerMessage(EventId = 103, Level = LogLevel.Warning, Message = "Database not ready yet. Retrying in 3 seconds...")]
    public static partial void LogDbNotReady(ILogger logger);

    [LoggerMessage(EventId = 104, Level = LogLevel.Information, Message = "Database is already up to date. No pending migrations to apply.")]
    public static partial void LogAlreadyUpToDate(ILogger logger);

    [LoggerMessage(EventId = 105, Level = LogLevel.Information, Message = "Applying {Count} pending migrations: {Migrations}")]
    public static partial void LogApplyingMigrations(ILogger logger, int count, string migrations);

    [LoggerMessage(EventId = 106, Level = LogLevel.Information, Message = "Successfully applied all pending migrations.")]
    public static partial void LogMigrationsApplied(ILogger logger);

    [LoggerMessage(EventId = 107, Level = LogLevel.Information, Message = "EventFlow Database Migration completed successfully.")]
    public static partial void LogCompleted(ILogger logger);

    [LoggerMessage(EventId = 108, Level = LogLevel.Warning, Message = "Migration attempt {Attempt} failed. Retrying in 3 seconds...")]
    public static partial void LogAttemptFailed(ILogger logger, int attempt, Exception ex);

    [LoggerMessage(EventId = 109, Level = LogLevel.Critical, Message = "Fatal error executing database migrations.")]
    public static partial void LogFatalError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 110, Level = LogLevel.Critical, Message = "Exhausted all migration retry attempts without establishing a connection.")]
    public static partial void LogExhausted(ILogger logger);
}
