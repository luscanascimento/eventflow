using EventFlow.Application.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EventFlow.Api.Endpoints;

public static class DeadLetterEndpoints
{
    public static IEndpointRouteBuilder MapDeadLetterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/dlq")
            .WithTags("DeadLetterQueue");

        group.MapGet("/", async (
            int? count,
            IDeadLetterManager dlqManager,
            CancellationToken cancellationToken) =>
        {
            int peekCount = count.GetValueOrDefault(10);
            var messages = await dlqManager.PeekMessagesAsync(peekCount, cancellationToken);
            return Results.Ok(new
            {
                Count = messages.Count,
                Messages = messages
            });
        })
        .WithName("PeekDeadLetterQueue")
        .WithSummary("Peeks pending messages in the Dead Letter Queue without removing them.");

        group.MapPost("/replay", async (
            int? maxCount,
            IDeadLetterManager dlqManager,
            CancellationToken cancellationToken) =>
        {
            int batch = maxCount.GetValueOrDefault(50);
            var result = await dlqManager.ReplayMessagesAsync(batch, cancellationToken);

            if (result.IsFailure)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "DLQ Replay Failed",
                    detail: result.Error.Message);
            }

            return Results.Ok(new
            {
                ReplayedCount = result.Value,
                Status = "Replayed"
            });
        })
        .WithName("ReplayDeadLetterQueue")
        .WithSummary("Replays messages from the DLQ back to the primary topic exchange.");

        group.MapDelete("/", async (
            IDeadLetterManager dlqManager,
            CancellationToken cancellationToken) =>
        {
            var result = await dlqManager.PurgeQueueAsync(cancellationToken);

            if (result.IsFailure)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "DLQ Purge Failed",
                    detail: result.Error.Message);
            }

            return Results.Ok(new
            {
                PurgedCount = result.Value,
                Status = "Purged"
            });
        })
        .WithName("PurgeDeadLetterQueue")
        .WithSummary("Purges all messages currently residing in the Dead Letter Queue.");

        return app;
    }
}
