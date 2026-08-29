namespace EventFlow.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/health").WithTags("Health");

        group.MapGet("/live", () => Results.Ok(new { status = "Healthy" }))
            .WithName("LivenessCheck")
            .WithSummary("Liveness probe verifying the service is running.");

        group.MapGet("/ready", () => Results.Ok(new { status = "Ready" }))
            .WithName("ReadinessCheck")
            .WithSummary("Readiness probe verifying the service is ready to accept traffic.");

        return endpoints;
    }
}
