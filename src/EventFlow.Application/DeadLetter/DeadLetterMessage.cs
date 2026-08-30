namespace EventFlow.Application.DeadLetter;

/// <summary>
/// Represents an event message that failed terminal processing and was routed to the Dead Letter Queue (DLQ).
/// </summary>
public sealed record DeadLetterMessage(
    string MessageId,
    string RoutingKey,
    string Payload,
    string? TenantId,
    string? EventType,
    string? TraceParent,
    string? ExceptionMessage,
    string? ExceptionType,
    DateTimeOffset FailedAt,
    int RetryCount,
    IReadOnlyDictionary<string, string> Headers);
