using System.Net;
using EventFlow.Infrastructure.Resilience;
using FluentAssertions;
using Polly.CircuitBreaker;
using Xunit;

namespace EventFlow.UnitTests.Infrastructure;

public class ResiliencePipelineFactoryTests
{
    [Fact]
    public void CreateExternalDispatchPipeline_WithValidOptions_ShouldCreateValidPipeline()
    {
        var options = new ResilienceOptions
        {
            MaxRetryAttempts = 2,
            BaseDelaySeconds = 0.01,
            MaxDelaySeconds = 0.05,
            CircuitBreakerFailureRatio = 0.5,
            CircuitBreakerMinimumThroughput = 2,
            CircuitBreakerSamplingDurationSeconds = 10,
            CircuitBreakerBreakDurationSeconds = 5,
            AttemptTimeoutSeconds = 1,
            TotalTimeoutSeconds = 5
        };

        var pipeline = ResiliencePipelineFactory.CreateExternalDispatchPipeline(options);

        pipeline.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTransient503ThenSuccess_ShouldRetryAndSucceed()
    {
        var options = new ResilienceOptions
        {
            MaxRetryAttempts = 3,
            BaseDelaySeconds = 0.01,
            MaxDelaySeconds = 0.05,
            CircuitBreakerFailureRatio = 0.9,
            CircuitBreakerMinimumThroughput = 10,
            AttemptTimeoutSeconds = 2,
            TotalTimeoutSeconds = 5
        };

        var pipeline = ResiliencePipelineFactory.CreateExternalDispatchPipeline(options);

        int attempts = 0;
        var response = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            await Task.Yield();
            if (attempts == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        attempts.Should().Be(2);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNonTransient400BadRequest_ShouldNotRetry()
    {
        var options = new ResilienceOptions
        {
            MaxRetryAttempts = 3,
            BaseDelaySeconds = 0.01,
            TotalTimeoutSeconds = 5
        };

        var pipeline = ResiliencePipelineFactory.CreateExternalDispatchPipeline(options);

        int attempts = 0;
        var response = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        attempts.Should().Be(1);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ExecuteAsync_WhenConsecutiveFailuresExceedThreshold_ShouldOpenCircuitBreaker()
    {
        var options = new ResilienceOptions
        {
            MaxRetryAttempts = 1,
            BaseDelaySeconds = 0.001,
            MaxDelaySeconds = 0.01,
            CircuitBreakerFailureRatio = 0.5,
            CircuitBreakerMinimumThroughput = 2,
            CircuitBreakerSamplingDurationSeconds = 10,
            CircuitBreakerBreakDurationSeconds = 5,
            AttemptTimeoutSeconds = 2,
            TotalTimeoutSeconds = 5
        };

        var pipeline = ResiliencePipelineFactory.CreateExternalDispatchPipeline(options);

        Func<Task> act = async () =>
        {
            for (int i = 0; i < 5; i++)
            {
                await pipeline.ExecuteAsync(async _ =>
                {
                    await Task.Yield();
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                });
            }
        };

        await act.Should().ThrowAsync<BrokenCircuitException>();
    }
}
