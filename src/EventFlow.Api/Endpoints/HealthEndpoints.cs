using EventFlow.Infrastructure.Idempotency;
using EventFlow.Infrastructure.Messaging;
using EventFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EventFlow.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/health").WithTags("Health");

        group.MapGet("/", () => Results.Ok(new { status = "Healthy" }))
            .WithName("HealthCheck")
            .WithSummary("Basic health check endpoint.");

        group.MapGet("/live", () => Results.Ok(new { status = "Healthy" }))
            .WithName("LivenessCheck")
            .WithSummary("Liveness probe verifying the service process is alive.");

        group.MapGet("/ready", async (
            EventFlowDbContext dbContext,
            IRabbitMqConnection rabbitMqConnection,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
        {
            bool dbOk;
            try
            {
                dbOk = await dbContext.Database.CanConnectAsync(cancellationToken);
            }
            catch
            {
                dbOk = false;
            }

            bool rabbitOk;
            try
            {
                rabbitOk = rabbitMqConnection.IsConnected;
                if (!rabbitOk)
                {
                    // Attempt lightweight channel check if connection was lazy
                    await using var channel = await rabbitMqConnection.CreateChannelAsync(cancellationToken: cancellationToken);
                    rabbitOk = channel != null;
                }
            }
            catch
            {
                rabbitOk = false;
            }

            var redisOptions = serviceProvider.GetService<IOptions<RedisOptions>>()?.Value;
            var redisConnection = serviceProvider.GetService<IRedisConnection>();

            bool redisOk = true;
            if (redisOptions != null && redisOptions.Enabled)
            {
                try
                {
                    var db = redisConnection?.GetDatabase();
                    if (db != null && (redisConnection?.IsConnected == true))
                    {
                        await db.PingAsync();
                        redisOk = true;
                    }
                    else
                    {
                        redisOk = false;
                    }
                }
                catch
                {
                    redisOk = false;
                }
            }

            bool isReady = dbOk && rabbitOk && redisOk;

            var response = new
            {
                status = isReady ? "Ready" : "Unhealthy",
                dependencies = new
                {
                    database = dbOk ? "Healthy" : "Unhealthy",
                    broker = rabbitOk ? "Healthy" : "Unhealthy",
                    cache = redisOk ? "Healthy" : "Unhealthy"
                }
            };

            return isReady
                ? Results.Ok(response)
                : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
        })
        .WithName("ReadinessCheck")
        .WithSummary("Readiness probe verifying all mandatory infrastructure dependencies are reachable.");

        return endpoints;
    }
}
