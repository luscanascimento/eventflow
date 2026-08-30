namespace EventFlow.Infrastructure.Telemetry;

/// <summary>
/// Configuration options for OpenTelemetry collectors and OTLP exporters.
/// </summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "OpenTelemetry";

    public string ServiceName { get; set; } = "EventFlow";
    public string ServiceVersion { get; set; } = "1.0.0";
    public string? OtlpEndpoint { get; set; }
    public bool EnableConsoleExporter { get; set; }
}
