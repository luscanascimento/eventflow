using System.Diagnostics;
using System.Text;
using EventFlow.Application.Abstractions;
using EventFlow.Domain.Common;
using EventFlow.Domain.Diagnostics;
using EventFlow.Infrastructure.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace EventFlow.Infrastructure.Resilience;

/// <summary>
/// Production HTTP external dispatcher executing outgoing webhooks inside a Polly v8 resilience pipeline
/// with active SSRF defense, W3C TraceContext propagation, and sanitized structured diagnostics.
/// </summary>
public sealed partial class HttpExternalDispatcher : IExternalDispatcher
{
    private readonly HttpClient _httpClient;
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;
    private readonly SsrfOptions _ssrfOptions;
    private readonly ILogger<HttpExternalDispatcher> _logger;

    public HttpExternalDispatcher(
        HttpClient httpClient,
        ResiliencePipeline<HttpResponseMessage> pipeline,
        IOptions<SsrfOptions> ssrfOptions,
        ILogger<HttpExternalDispatcher> logger)
    {
        _httpClient = httpClient;
        _pipeline = pipeline;
        _ssrfOptions = ssrfOptions.Value;
        _logger = logger;
    }

    public async Task<Result<ExternalDispatchResponse>> DispatchAsync(
        ExternalDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        string sanitizedUrl = SsrfGuard.SanitizeUrlForLogging(request.TargetUrl);

        using var activity = EventFlowDiagnostics.ActivitySource.StartActivity(
            "EventFlow.HttpExternalDispatch",
            ActivityKind.Client);

        activity?.SetTag("http.request.method", request.Method.Method);
        activity?.SetTag("url.full", sanitizedUrl);

        // SSRF Defense: validate URI scheme and safety against private/cloud metadata ranges
        if (!SsrfGuard.IsSafeTarget(request.TargetUrl, _ssrfOptions))
        {
            LogInvalidUriScheme(_logger, sanitizedUrl);
            activity?.SetStatus(ActivityStatusCode.Error, "BlockedBySsrfGuard");
            return Result.Failure<ExternalDispatchResponse>(
                DomainError.Validation("ExternalDispatcher.BlockedTarget", "Target URL is blocked by security and SSRF defense policies."));
        }

        LogDispatching(_logger, request.Method.Method, sanitizedUrl);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var response = await _pipeline.ExecuteAsync(async ct =>
            {
                using var requestMessage = new HttpRequestMessage(request.Method, request.TargetUrl)
                {
                    Content = new StringContent(request.Payload, Encoding.UTF8, "application/json")
                };

                string? traceParent = !string.IsNullOrWhiteSpace(request.TraceParent)
                    ? request.TraceParent
                    : activity?.Id;

                if (!string.IsNullOrWhiteSpace(traceParent))
                {
                    requestMessage.Headers.TryAddWithoutValidation("traceparent", traceParent);
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
            int statusCode = (int)response.StatusCode;
            activity?.SetTag("http.response.status_code", statusCode);

            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            LogDispatchCompleted(_logger, statusCode, stopwatch.ElapsedMilliseconds);

            if (!response.IsSuccessStatusCode)
            {
                activity?.SetStatus(ActivityStatusCode.Error, $"HTTP {statusCode}");
                return Result.Failure<ExternalDispatchResponse>(
                    DomainError.Failure("Resilience.HttpError", $"Downstream endpoint returned non-success HTTP status {statusCode}."));
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
            return Result.Success(new ExternalDispatchResponse(
                StatusCode: statusCode,
                ResponseBody: responseBody,
                Elapsed: stopwatch.Elapsed));
        }
        catch (BrokenCircuitException ex)
        {
            stopwatch.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, "CircuitBreakerOpen");
            LogCircuitBroken(_logger, sanitizedUrl, ex);
            return Result.Failure<ExternalDispatchResponse>(
                DomainError.Failure("Resilience.CircuitBreakerOpen", "Downstream service circuit breaker is open. Request short-circuited."));
        }
        catch (TimeoutRejectedException ex)
        {
            stopwatch.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, "Timeout");
            LogTimeout(_logger, sanitizedUrl, stopwatch.ElapsedMilliseconds, ex);
            return Result.Failure<ExternalDispatchResponse>(
                DomainError.Failure("Resilience.Timeout", "The external dispatch operation timed out."));
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            LogNetworkError(_logger, sanitizedUrl, ex);
            return Result.Failure<ExternalDispatchResponse>(
                DomainError.Failure("Resilience.NetworkError", $"Network error during dispatch: {ex.Message}"));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            LogUnhandledError(_logger, sanitizedUrl, ex);
            return Result.Failure<ExternalDispatchResponse>(
                DomainError.Failure("Resilience.UnhandledError", $"Unexpected error during dispatch: {ex.Message}"));
        }
    }

    [LoggerMessage(EventId = 9201, Level = LogLevel.Warning, Message = "Rejected dispatch to target '{TargetUrl}' by SSRF policy")]
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
