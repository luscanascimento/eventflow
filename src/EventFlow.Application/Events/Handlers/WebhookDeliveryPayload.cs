using System.Text.Json.Serialization;

namespace EventFlow.Application.Events.Handlers;

/// <summary>
/// Data payload for outbound webhook delivery events.
/// </summary>
public sealed record WebhookDeliveryPayload(
    [property: JsonPropertyName("targetUrl")] string TargetUrl,
    [property: JsonPropertyName("httpMethod")] string HttpMethod,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("headers")] Dictionary<string, string>? Headers = null);
