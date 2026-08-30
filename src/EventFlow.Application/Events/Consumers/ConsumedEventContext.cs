namespace EventFlow.Application.Events.Consumers;

/// <summary>
/// Immutable context containing metadata and payload of an event consumed from the messaging broker.
/// </summary>
public sealed record ConsumedEventContext(
    Guid EventId,
    string EventType,
    string TenantId,
    string? TraceParent,
    string? OutboxId,
    string RoutingKey,
    string Payload,
    DateTimeOffset ConsumedAt,
    IReadOnlyDictionary<string, string> Headers,
    bool Redelivered);
