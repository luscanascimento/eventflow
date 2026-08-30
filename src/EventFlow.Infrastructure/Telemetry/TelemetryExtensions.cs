using EventFlow.Domain.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace EventFlow.Infrastructure.Telemetry;

/// <summary>
/// Service collection extensions configuring OpenTelemetry Distributed Tracing, Metrics, and OTLP Exporters.
/// </summary>
public static class TelemetryExtensions
{
    public static IServiceCollection AddEventFlowTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string defaultServiceName)
    {
        var telemetryOptions = configuration
            .GetSection(TelemetryOptions.SectionName)
            .Get<TelemetryOptions>() ?? new TelemetryOptions();

        string resolvedServiceName = !string.IsNullOrWhiteSpace(telemetryOptions.ServiceName) && telemetryOptions.ServiceName != "EventFlow"
            ? telemetryOptions.ServiceName
            : defaultServiceName;

        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: resolvedServiceName,
                serviceVersion: telemetryOptions.ServiceVersion);

        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(resourceBuilder)
                    .AddSource(EventFlowDiagnostics.DiagnosticSourceName)
                    .AddHttpClientInstrumentation()
                    .AddNpgsql();

                if (telemetryOptions.EnableConsoleExporter)
                {
                    tracing.AddConsoleExporter();
                }

                if (!string.IsNullOrWhiteSpace(telemetryOptions.OtlpEndpoint))
                {
                    tracing.AddOtlpExporter(otlpOptions =>
                    {
                        otlpOptions.Endpoint = new Uri(telemetryOptions.OtlpEndpoint);
                    });
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(resourceBuilder)
                    .AddMeter(EventFlowDiagnostics.DiagnosticSourceName)
                    .AddHttpClientInstrumentation();

                if (telemetryOptions.EnableConsoleExporter)
                {
                    metrics.AddConsoleExporter();
                }

                if (!string.IsNullOrWhiteSpace(telemetryOptions.OtlpEndpoint))
                {
                    metrics.AddOtlpExporter(otlpOptions =>
                    {
                        otlpOptions.Endpoint = new Uri(telemetryOptions.OtlpEndpoint);
                    });
                }
            });

        return services;
    }
}
