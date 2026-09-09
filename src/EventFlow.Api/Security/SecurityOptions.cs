namespace EventFlow.Api.Security;

/// <summary>
/// Security configuration options including API Key authentication.
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// API Key secret required for authenticating requests to protected endpoints.
    /// In production environments, this key must be configured and non-empty.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}
