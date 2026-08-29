using System.Text.Json;

namespace EventFlow.Application.Events.Commands;

/// <summary>
/// Command representing an external event ingestion request.
/// </summary>
public sealed record IngestEventCommand(
    Guid? EventId,
    string EventType,
    int? Version,
    string Source,
    DateTimeOffset? OccurredAt,
    string? CorrelationId,
    string? TraceParent,
    string TenantId,
    string IdempotencyKey,
    JsonElement Payload,
    IReadOnlyDictionary<string, string>? Metadata);

/// <summary>
/// Response payload for a successfully accepted event ingestion.
/// </summary>
public sealed record IngestEventResponse(
    Guid EventId,
    string Status,
    DateTimeOffset IngestedAt);
