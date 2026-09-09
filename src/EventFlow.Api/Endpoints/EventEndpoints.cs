using EventFlow.Api.Security;
using EventFlow.Application.Events.Commands;
using EventFlow.Domain.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EventFlow.Api.Endpoints;

public static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/events")
            .WithTags("Events")
            .AddEndpointFilter<ApiKeyEndpointFilter>();

        group.MapPost("/", async (
            [FromBody] IngestEventCommand command,
            [FromServices] IngestEventUseCase useCase,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            // Validate X-Tenant-Id header against payload tenant if header is present
            string? headerTenant = httpContext.Request.Headers["X-Tenant-Id"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(headerTenant) &&
                !string.Equals(headerTenant.Trim(), command?.TenantId?.Trim(), StringComparison.Ordinal))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Tenant Mismatch",
                    detail: "The 'X-Tenant-Id' header value does not match the payload 'tenantId'.",
                    instance: httpContext.Request.Path);
            }

            var result = await useCase.ExecuteAsync(command!, cancellationToken);

            if (result.IsSuccess)
            {
                return Results.Accepted(
                    uri: $"/api/v1/events/{result.Value.EventId}",
                    value: result.Value);
            }

            return result.Error.Type switch
            {
                ErrorType.Validation => Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Validation Failure",
                    detail: result.Error.Message,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = result.Error.Code }),

                ErrorType.Conflict => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Conflict",
                    detail: result.Error.Message,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = result.Error.Code }),

                ErrorType.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Not Found",
                    detail: result.Error.Message,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = result.Error.Code }),

                _ => Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Processing Failure",
                    detail: result.Error.Message,
                    extensions: new Dictionary<string, object?> { ["errorCode"] = result.Error.Code })
            };
        })
        .WithName("IngestEvent")
        .WithSummary("Ingests a new event/webhook into the transactional outbox pipeline.")
        .Produces<IngestEventResponse>(StatusCodes.Status202Accepted)
        .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
        .Produces<ProblemDetails>(StatusCodes.Status409Conflict)
        .Produces<ProblemDetails>(StatusCodes.Status500InternalServerError)
        .RequireRateLimiting("events-rate-limit");

        return endpoints;
    }
}
