using System.Net;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace EventFlow.Infrastructure.Resilience;

/// <summary>
/// Factory configuring production Polly v8 resilience pipelines with timeouts,
/// exponential backoff, decorrelated jitter, and circuit breaker protection.
/// </summary>
public static class ResiliencePipelineFactory
{
    public const string ExternalDispatchPipelineName = "external-dispatch-pipeline";

    /// <summary>
    /// Builds a layered resilience pipeline for HTTP dispatch:
    /// 1. Total Timeout -> 2. Retry with Jitter -> 3. Circuit Breaker -> 4. Per-Attempt Timeout.
    /// </summary>
    public static ResiliencePipeline<HttpResponseMessage> CreateExternalDispatchPipeline(ResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new ResiliencePipelineBuilder<HttpResponseMessage>()
            // 1. Total Timeout (encompasses all retries and execution time)
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(options.TotalTimeoutSeconds)
            })
            // 2. Retry with Exponential Backoff and Decorrelated Jitter
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = options.MaxRetryAttempts,
                Delay = TimeSpan.FromSeconds(options.BaseDelaySeconds),
                MaxDelay = TimeSpan.FromSeconds(options.MaxDelaySeconds),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(response =>
                        (int)response.StatusCode >= 500 ||
                        response.StatusCode == HttpStatusCode.RequestTimeout ||
                        response.StatusCode == HttpStatusCode.TooManyRequests)
            })
            // 3. Circuit Breaker (Fail-Fast during sustained downstream outages)
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = options.CircuitBreakerFailureRatio,
                MinimumThroughput = options.CircuitBreakerMinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(options.CircuitBreakerSamplingDurationSeconds),
                BreakDuration = TimeSpan.FromSeconds(options.CircuitBreakerBreakDurationSeconds),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(response =>
                        (int)response.StatusCode >= 500 ||
                        response.StatusCode == HttpStatusCode.RequestTimeout ||
                        response.StatusCode == HttpStatusCode.TooManyRequests)
            })
            // 4. Per-Attempt Timeout (prevents hung individual HTTP calls)
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = TimeSpan.FromSeconds(options.AttemptTimeoutSeconds)
            })
            .Build();
    }
}
