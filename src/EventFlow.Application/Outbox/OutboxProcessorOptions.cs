namespace EventFlow.Application.Outbox;

/// <summary>
/// Configuration options for the Transactional Outbox processing engine.
/// </summary>
public sealed class OutboxProcessorOptions
{
    public const string SectionName = "OutboxProcessor";

    public int BatchSize { get; set; } = 50;
    public int PollingIntervalMs { get; set; } = 1000;
    public int MaxRetries { get; set; } = 5;
    public int BaseRetryDelaySeconds { get; set; } = 2;
}
