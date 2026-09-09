namespace EventFlow.Infrastructure.Security;

/// <summary>
/// Configuration options for Server-Side Request Forgery (SSRF) defense.
/// </summary>
public sealed class SsrfOptions
{
    public const string SectionName = "Security:Ssrf";

    /// <summary>
    /// Explicit list of allowed hostnames or IP addresses when private endpoints are strictly necessary.
    /// Default is empty (strict block).
    /// </summary>
    public HashSet<string> AllowedHosts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether private network access is allowed globally. Default is false (security by default).
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }
}
