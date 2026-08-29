namespace EventFlow.Domain.Outbox;

/// <summary>
/// Lifecycle states of an outbox message.
/// </summary>
public enum OutboxStatus
{
    Pending = 0,
    Processing = 1,
    Published = 2,
    DeadLettered = 3
}
