namespace EventFlow.Infrastructure.Resilience;

/// <summary>
/// Configuration options for Polly v8 resilience pipelines (Retry, Circuit Breaker, Timeouts).
/// </summary>
public sealed class ResilienceOptions
{
    public const string SectionName = "Resilience";

    public int MaxRetryAttempts { get; set; } = 3;
    public double BaseDelaySeconds { get; set; } = 1.0;
    public double MaxDelaySeconds { get; set; } = 10.0;
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;
    public int CircuitBreakerMinimumThroughput { get; set; } = 5;
    public double CircuitBreakerSamplingDurationSeconds { get; set; } = 30.0;
    public double CircuitBreakerBreakDurationSeconds { get; set; } = 15.0;
    public double AttemptTimeoutSeconds { get; set; } = 5.0;
    public double TotalTimeoutSeconds { get; set; } = 20.0;
}
