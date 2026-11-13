using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EventFlow.Domain.Diagnostics;

/// <summary>
/// Centralized diagnostic source definitions for OpenTelemetry distributed tracing and metrics in EventFlow.
/// </summary>
public static class EventFlowDiagnostics
{
    public const string ServiceName = "EventFlow";
    public const string DiagnosticSourceName = "EventFlow.Engine";
    public const string Version = "1.0.0";

    /// <summary>
    /// OpenTelemetry ActivitySource for creating custom distributed trace spans across the event lifecycle.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(DiagnosticSourceName, Version);

    /// <summary>
    /// OpenTelemetry Meter for recording domain, application, and infrastructure metrics.
    /// </summary>
    public static readonly Meter Meter = new(DiagnosticSourceName, Version);

    // --- Metric Instruments ---

    public static readonly Counter<long> EventsIngested = Meter.CreateCounter<long>(
        name: "eventflow.events.ingested",
        unit: "{event}",
        description: "Total number of events ingested via HTTP API");

    public static readonly Counter<long> EventsProcessed = Meter.CreateCounter<long>(
        name: "eventflow.events.processed",
        unit: "{event}",
        description: "Total number of events processed by consumer workers");

    public static readonly Histogram<double> EventProcessingDuration = Meter.CreateHistogram<double>(
        name: "eventflow.events.processing.duration",
        unit: "ms",
        description: "Duration of event consumer processing in milliseconds");

    public static readonly Counter<long> OutboxDispatched = Meter.CreateCounter<long>(
        name: "eventflow.outbox.dispatched",
        unit: "{message}",
        description: "Total number of outbox messages dispatched to message broker");

    public static readonly Counter<long> IdempotencyHits = Meter.CreateCounter<long>(
        name: "eventflow.idempotency.hits",
        unit: "{hit}",
        description: "Total number of duplicate events intercepted by idempotency guards");

    public static readonly Counter<long> DlqMessages = Meter.CreateCounter<long>(
        name: "eventflow.dlq.messages",
        unit: "{message}",
        description: "Total number of messages routed to the dead letter queue");

    public static readonly Counter<long> DlqReplayed = Meter.CreateCounter<long>(
        name: "eventflow.dlq.replayed",
        unit: "{message}",
        description: "Total number of dead letter queue messages replayed");

    public static readonly Counter<long> DlqPurged = Meter.CreateCounter<long>(
        name: "eventflow.dlq.purged",
        unit: "{message}",
        description: "Total number of dead letter queue messages purged");
}
