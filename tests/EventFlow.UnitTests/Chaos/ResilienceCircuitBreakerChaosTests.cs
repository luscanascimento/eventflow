using System.Net;
using EventFlow.Infrastructure.Resilience;
using FluentAssertions;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Xunit;

namespace EventFlow.UnitTests.Chaos;

/// <summary>
/// Chaos tests evaluating Polly v8 Resilience Pipeline behavior under severe downstream failure spikes,
/// cascading failure prevention, and circuit breaker trip/recovery dynamics.
/// </summary>
public sealed class ResilienceCircuitBreakerChaosTests
{
    private readonly ResilienceOptions _options = new()
    {
        TotalTimeoutSeconds = 10,
        AttemptTimeoutSeconds = 1,
        MaxRetryAttempts = 1,
        BaseDelaySeconds = 0.001,
        MaxDelaySeconds = 0.01,
        CircuitBreakerFailureRatio = 0.5,
        CircuitBreakerMinimumThroughput = 2,
        CircuitBreakerSamplingDurationSeconds = 10,
        CircuitBreakerBreakDurationSeconds = 2
    };

    [Fact]
    public async Task Chaos_DownstreamOutage_TripsCircuitBreaker_AndFailsFastWithBrokenCircuitException()
    {
        // ARRANGE
        var pipeline = ResiliencePipelineFactory.CreateExternalDispatchPipeline(_options);
        int networkCallsMade = 0;

        // ACT: Execute failing calls until circuit trips
        Func<Task> callUntilTripped = async () =>
        {
            for (int i = 0; i < 5; i++)
            {
                await pipeline.ExecuteAsync(async ct =>
                {
                    Interlocked.Increment(ref networkCallsMade);
                    await Task.Yield();
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                });
            }
        };

        // ASSERT: Must throw BrokenCircuitException
        await callUntilTripped.Should().ThrowAsync<BrokenCircuitException>()
            .WithMessage("*The circuit is now open and is not allowing calls.*");

        int callsBeforeOpen = networkCallsMade;

        // Subsequent call must fail immediately without making ANY new network calls
        Func<Task> callWhenCircuitIsOpen = async () =>
        {
            await pipeline.ExecuteAsync(async ct =>
            {
                Interlocked.Increment(ref networkCallsMade);
                await Task.Yield();
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        };

        await callWhenCircuitIsOpen.Should().ThrowAsync<BrokenCircuitException>();
        networkCallsMade.Should().Be(callsBeforeOpen, "Circuit breaker in Open state must block execution before reaching the downstream network call");
    }

    [Fact]
    public async Task Chaos_NonRetryable400BadRequest_FailsImmediatelyWithoutWastingRetries()
    {
        // ARRANGE
        var pipeline = ResiliencePipelineFactory.CreateExternalDispatchPipeline(_options);
        int callCount = 0;

        // ACT
        var response = await pipeline.ExecuteAsync(async ct =>
        {
            Interlocked.Increment(ref callCount);
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        // ASSERT: 400 Bad Request must NOT trigger retries
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        callCount.Should().Be(1, "400 Bad Request is a client contract error and must not waste retries");
    }

    [Fact]
    public async Task Chaos_SlowHungRequest_TriggersAttemptTimeoutAndRetries()
    {
        // ARRANGE: Pipeline with 50ms attempt timeout
        var options = new ResilienceOptions
        {
            TotalTimeoutSeconds = 5,
            AttemptTimeoutSeconds = 0.05,
            MaxRetryAttempts = 2,
            BaseDelaySeconds = 0.01,
            MaxDelaySeconds = 0.05
        };

        var pipeline = ResiliencePipelineFactory.CreateExternalDispatchPipeline(options);
        int attempts = 0;

        // ACT
        var response = await pipeline.ExecuteAsync(async ct =>
        {
            int current = Interlocked.Increment(ref attempts);
            if (current <= 2)
            {
                // Simulate hung call (longer than 50ms attempt timeout)
                await Task.Delay(200, ct);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        // ASSERT
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        attempts.Should().Be(3, "Slow calls exceeding attempt timeout must be interrupted and retried");
    }
}
