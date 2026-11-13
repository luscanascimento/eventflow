using System.Text.Json;
using EventFlow.Application.Abstractions;
using EventFlow.Application.Events.Consumers;
using EventFlow.Domain.Common;
using Microsoft.Extensions.Logging;

namespace EventFlow.Application.Events.Handlers;

/// <summary>
/// Domain event handler for processing 'webhook.delivery.v1' events,
/// dispatching outgoing HTTP webhooks via the resilient external dispatcher.
/// </summary>
public sealed partial class WebhookDeliveryHandler : IEventConsumerHandler
{
    public const string TargetEventType = "webhook.delivery.v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IExternalDispatcher _externalDispatcher;
    private readonly ILogger<WebhookDeliveryHandler> _logger;

    public WebhookDeliveryHandler(
        IExternalDispatcher externalDispatcher,
        ILogger<WebhookDeliveryHandler> logger)
    {
        _externalDispatcher = externalDispatcher;
        _logger = logger;
    }

    public bool CanHandle(string eventType) =>
        string.Equals(eventType, TargetEventType, StringComparison.OrdinalIgnoreCase);

    public async Task<Result> HandleAsync(ConsumedEventContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        WebhookDeliveryPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<WebhookDeliveryPayload>(context.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            LogInvalidJsonPayload(_logger, context.EventId, ex.Message);
            return Result.Failure(
                DomainError.Validation("WebhookDelivery.InvalidPayload", $"Malformed JSON payload: {ex.Message}"));
        }

        if (payload is null)
        {
            return Result.Failure(
                DomainError.Validation("WebhookDelivery.NullPayload", "Deserialized payload is null."));
        }

        if (string.IsNullOrWhiteSpace(payload.TargetUrl) || !Uri.TryCreate(payload.TargetUrl, UriKind.Absolute, out var targetUri))
        {
            return Result.Failure(
                DomainError.Validation("WebhookDelivery.InvalidTargetUrl", "A valid absolute TargetUrl is required."));
        }

        HttpMethod httpMethod;
        try
        {
            httpMethod = new HttpMethod(string.IsNullOrWhiteSpace(payload.HttpMethod) ? "POST" : payload.HttpMethod);
        }
        catch
        {
            return Result.Failure(
                DomainError.Validation("WebhookDelivery.InvalidHttpMethod", "Invalid HTTP method specified."));
        }

        var dispatchRequest = new ExternalDispatchRequest(
            TargetUrl: targetUri,
            Method: httpMethod,
            Payload: payload.Body ?? string.Empty,
            Headers: payload.Headers,
            TraceParent: context.TraceParent);

        var dispatchResult = await _externalDispatcher.DispatchAsync(dispatchRequest, cancellationToken);

        if (dispatchResult.IsFailure)
        {
            LogWebhookDeliveryFailed(_logger, context.EventId, targetUri.Host, dispatchResult.Error.Message);
            return Result.Failure(dispatchResult.Error);
        }

        if (dispatchResult.Value.StatusCode is < 200 or >= 300)
        {
            string errorMessage = $"Downstream webhook endpoint returned HTTP {dispatchResult.Value.StatusCode}.";
            LogWebhookDeliveryFailed(_logger, context.EventId, targetUri.Host, errorMessage);
            return Result.Failure(
                DomainError.Failure("WebhookDelivery.DownstreamError", errorMessage));
        }

        LogWebhookDeliverySuccess(_logger, context.EventId, targetUri.Host, dispatchResult.Value.StatusCode);
        return Result.Success();
    }

    [LoggerMessage(EventId = 7201, Level = LogLevel.Warning, Message = "Failed to deserialize webhook delivery payload for event {EventId}: {Error}")]
    private static partial void LogInvalidJsonPayload(ILogger logger, Guid eventId, string error);

    [LoggerMessage(EventId = 7202, Level = LogLevel.Warning, Message = "Webhook delivery failed for event {EventId} to host '{Host}': {Error}")]
    private static partial void LogWebhookDeliveryFailed(ILogger logger, Guid eventId, string host, string error);

    [LoggerMessage(EventId = 7203, Level = LogLevel.Information, Message = "Webhook delivery succeeded for event {EventId} to host '{Host}' with HTTP status {StatusCode}")]
    private static partial void LogWebhookDeliverySuccess(ILogger logger, Guid eventId, string host, int statusCode);
}
