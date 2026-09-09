namespace EventFlow.Domain.Idempotency;

/// <summary>
/// Represents the persistent processing state of an idempotency record.
/// </summary>
public enum ProcessedEventStatus
{
    Processing = 0,
    Completed = 1,
    Failed = 2
}
