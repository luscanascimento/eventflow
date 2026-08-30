using EventFlow.Domain.Common;

namespace EventFlow.Application.Abstractions;

/// <summary>
/// Represents a request to dispatch an event payload to an external downstream webhook or API endpoint.
/// </summary>
public sealed record ExternalDispatchRequest(
    Uri TargetUrl,
    HttpMethod Method,
    string Payload,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? TraceParent = null);

/// <summary>
/// Represents the result of an external HTTP dispatch.
/// </summary>
public sealed record ExternalDispatchResponse(
    int StatusCode,
    string ResponseBody,
    TimeSpan Elapsed);

/// <summary>
/// Dispatches outgoing HTTP requests to external webhook subscribers or partner systems
/// protected by a comprehensive Polly v8 resilience pipeline.
/// </summary>
public interface IExternalDispatcher
{
    /// <summary>
    /// Executes the HTTP dispatch wrapped in exponential backoff, jitter, circuit breaker, and timeout policies.
    /// </summary>
    Task<Result<ExternalDispatchResponse>> DispatchAsync(
        ExternalDispatchRequest request,
        CancellationToken cancellationToken = default);
}
