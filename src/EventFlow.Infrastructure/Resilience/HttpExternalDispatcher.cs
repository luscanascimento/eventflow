using System.Diagnostics;
using System.Text;
using EventFlow.Application.Abstractions;
using EventFlow.Domain.Common;
using EventFlow.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace EventFlow.Infrastructure.Resilience;

/// <summary>
/// Production HTTP external dispatcher executing outgoing webhooks inside a Polly v8 resilience pipeline
/// with SSRF defense, W3C TraceContext propagation, and structured diagnostics.
/// </summary>
public sealed partial class HttpExternalDispatcher : IExternalDispatcher
{
    private readonly HttpClient _httpClient;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;
    private readonly ILogger<HttpExternalDispatcher> _logger;

    public HttpExternalDispatcher(
        HttpClient httpClient,
        ResiliencePipeline<HttpResponseMessage> pipeline,
        ILogger<HttpExternalDispatcher> logger)
    {
        _httpClient = httpClient;
        _pipeline = pipeline;
        _logger = logger;
    }

    public async Task<Result<ExternalDispatchResponse>> DispatchAsync(
        ExternalDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // SSRF Defense: validate URI scheme and safety
        if (!SsrfGuard.IsSafeTarget(request.TargetUrl, allowPrivateAddresses: true))
        {
            LogInvalidUriScheme(_logger, request.TargetUrl.ToString());
            return Result.Failure<ExternalDispatchResponse>(
                DomainError.Validation("ExternalDispatcher.InvalidScheme", "Target URL scheme is invalid or blocked by security policies."));
        }

        LogDispatching(_logger, request.Method.Method, request.TargetUrl.ToString());

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = await _pipeline.ExecuteAsync(async ct =>
            {
                using var requestMessage = new HttpRequestMessage(request.Method, request.TargetUrl)
                {
                    Content = new StringContent(request.Payload, Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrWhiteSpace(request.TraceParent))
                {
                    requestMessage.Headers.TryAddWithoutValidation("traceparent", request.TraceParent);
                }

                if (request.Headers != null)
                {
                    foreach (var (key, value) in request.Headers)
                    {
                        requestMessage.Headers.TryAddWithoutValidation(key, value);
                    }
                }

                return await _httpClient.SendAsync(requestMessage, ct);
            }, cancellationToken);

            stopwatch.Stop();
            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            LogDispatchCompleted(_logger, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);

            return Result.Success(new ExternalDispatchResponse(
                StatusCode: (int)response.StatusCode,
                ResponseBody: responseBody,
                Elapsed: stopwatch.Elapsed));
        }
        catch (BrokenCircuitException ex)
        {
            stopwatch.Stop();
            LogCircuitBroken(_logger, request.TargetUrl.ToString(), ex);
            return Result.Failure<ExternalDispatchResponse>(
                DomainError.Failure("Resilience.CircuitBreakerOpen", "Downstream service circuit breaker is open. Request short-circuited."));
        }
        catch (TimeoutRejectedException ex)
        {
            stopwatch.Stop();
            LogTimeout(_logger, request.TargetUrl.ToString(), stopwatch.ElapsedMilliseconds, ex);
            return Result.Failure<ExternalDispatchResponse>(
                DomainError.Failure("Resilience.Timeout", "The external dispatch operation timed out."));
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            LogNetworkError(_logger, request.TargetUrl.ToString(), ex);
            return Result.Failure<ExternalDispatchResponse>(
                DomainError.Failure("Resilience.NetworkError", $"Network error during dispatch: {ex.Message}"));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LogUnhandledError(_logger, request.TargetUrl.ToString(), ex);
            return Result.Failure<ExternalDispatchResponse>(
                DomainError.Failure("Resilience.UnhandledError", $"Unexpected error during dispatch: {ex.Message}"));
        }
    }

    [LoggerMessage(EventId = 9201, Level = LogLevel.Warning, Message = "Rejected dispatch to target '{TargetUrl}' due to invalid scheme")]
    private static partial void LogInvalidUriScheme(ILogger logger, string targetUrl);

    [LoggerMessage(EventId = 9202, Level = LogLevel.Information, Message = "Dispatching HTTP {Method} request to '{TargetUrl}'")]
    private static partial void LogDispatching(ILogger logger, string method, string targetUrl);

    [LoggerMessage(EventId = 9203, Level = LogLevel.Information, Message = "External HTTP dispatch finished with status {StatusCode} in {ElapsedMs}ms")]
    private static partial void LogDispatchCompleted(ILogger logger, int statusCode, long elapsedMs);

    [LoggerMessage(EventId = 9204, Level = LogLevel.Error, Message = "Circuit breaker is open for endpoint '{TargetUrl}'")]
    private static partial void LogCircuitBroken(ILogger logger, string targetUrl, Exception ex);

    [LoggerMessage(EventId = 9205, Level = LogLevel.Warning, Message = "External dispatch to '{TargetUrl}' timed out after {ElapsedMs}ms")]
    private static partial void LogTimeout(ILogger logger, string targetUrl, long elapsedMs, Exception ex);

    [LoggerMessage(EventId = 9206, Level = LogLevel.Error, Message = "Network error during external dispatch to '{TargetUrl}'")]
    private static partial void LogNetworkError(ILogger logger, string targetUrl, Exception ex);

    [LoggerMessage(EventId = 9207, Level = LogLevel.Error, Message = "Unhandled exception during external dispatch to '{TargetUrl}'")]
    private static partial void LogUnhandledError(ILogger logger, string targetUrl, Exception ex);
}
