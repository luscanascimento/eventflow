using System.Diagnostics;
using EventFlow.Application.Abstractions;
using EventFlow.Domain.Common;
using EventFlow.Domain.Diagnostics;
using Microsoft.Extensions.Logging;

namespace EventFlow.Application.Events.Consumers;

/// <summary>
/// Default event processor that routes incoming events to matching <see cref="IEventConsumerHandler"/> implementations,
/// protected by distributed idempotency guarantees and OpenTelemetry tracing.
/// </summary>
public sealed partial class EventProcessor : IEventProcessor
{
    private readonly IEnumerable<IEventConsumerHandler> _handlers;
    private readonly IIdempotencyManager _idempotencyManager;
    private readonly ILogger<EventProcessor> _logger;

    public EventProcessor(
        IEnumerable<IEventConsumerHandler> handlers,
        IIdempotencyManager idempotencyManager,
        ILogger<EventProcessor> logger)
    {
        _handlers = handlers;
        _idempotencyManager = idempotencyManager;
        _logger = logger;
    }

    public async Task<Result> ProcessAsync(ConsumedEventContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        ActivityContext parentContext = default;
        if (!string.IsNullOrWhiteSpace(context.TraceParent) &&
            ActivityContext.TryParse(context.TraceParent, null, out var parsedParent))
        {
            parentContext = parsedParent;
        }

        using var activity = EventFlowDiagnostics.ActivitySource.StartActivity(
            "EventFlow.ConsumerProcess",
            ActivityKind.Consumer,
            parentContext);

        activity?.SetTag("eventflow.event_id", context.EventId.ToString("D"));
        activity?.SetTag("eventflow.event_type", context.EventType);
        activity?.SetTag("eventflow.tenant_id", context.TenantId);

        var stopwatch = Stopwatch.StartNew();

        LogProcessingEvent(_logger, context.EventId, context.EventType, context.TenantId);

        var handler = _handlers.FirstOrDefault(h => h.CanHandle(context.EventType));
        if (handler == null)
        {
            LogNoHandlerFound(_logger, context.EventType, context.EventId);
            activity?.SetStatus(ActivityStatusCode.Ok, "NoHandlerFound");
            return Result.Success();
        }

        string idempotencyKey = context.Headers.TryGetValue("idempotency_key", out var key) && !string.IsNullOrWhiteSpace(key)
            ? key
            : context.EventId.ToString("D");

        string scope = handler.GetType().Name;
        activity?.SetTag("eventflow.idempotency_key", idempotencyKey);
        activity?.SetTag("eventflow.scope", scope);

        var acquireResult = await _idempotencyManager.TryAcquireAsync(
            context.TenantId,
            idempotencyKey,
            scope,
            context.EventId,
            cancellationToken);

        if (acquireResult.IsAlreadyProcessed)
        {
            EventFlowDiagnostics.IdempotencyHits.Add(
                1,
                new KeyValuePair<string, object?>("tenant_id", context.TenantId),
                new KeyValuePair<string, object?>("scope", scope));

            activity?.SetTag("eventflow.idempotency_status", "AlreadyProcessed");
            activity?.SetStatus(ActivityStatusCode.Ok);

            LogEventAlreadyProcessed(_logger, context.EventId, context.EventType, idempotencyKey, scope);
            return Result.Success();
        }

        if (acquireResult.IsConflictInFlight)
        {
            activity?.SetTag("eventflow.idempotency_status", "ConflictInFlight");
            activity?.SetStatus(ActivityStatusCode.Error, "ConflictInFlight");

            LogEventConflictInFlight(_logger, context.EventId, context.EventType, idempotencyKey, scope);
            return Result.Failure(DomainError.Conflict(
                "Idempotency.ConflictInFlight",
                $"Another worker is currently processing event '{context.EventId}' with idempotency key '{idempotencyKey}'."));
        }

        Result result;
        try
        {
            result = await handler.HandleAsync(context, cancellationToken);
            stopwatch.Stop();

            EventFlowDiagnostics.EventProcessingDuration.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("event_type", context.EventType));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            EventFlowDiagnostics.EventProcessingDuration.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("event_type", context.EventType));

            EventFlowDiagnostics.EventsProcessed.Add(
                1,
                new KeyValuePair<string, object?>("tenant_id", context.TenantId),
                new KeyValuePair<string, object?>("event_type", context.EventType),
                new KeyValuePair<string, object?>("status", "exception"));

            await _idempotencyManager.ReleaseLockAsync(
                context.TenantId,
                idempotencyKey,
                scope,
                acquireResult.LockToken,
                CancellationToken.None);

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            LogUnhandledExceptionDuringProcessing(_logger, context.EventId, context.EventType, ex);
            return Result.Failure(DomainError.Failure("EventProcessor.UnhandledException", ex.Message));
        }

        if (result.IsSuccess)
        {
            await _idempotencyManager.MarkCompletedAsync(
                context.TenantId,
                idempotencyKey,
                scope,
                context.EventId,
                context.EventType,
                "Success",
                CancellationToken.None);

            EventFlowDiagnostics.EventsProcessed.Add(
                1,
                new KeyValuePair<string, object?>("tenant_id", context.TenantId),
                new KeyValuePair<string, object?>("event_type", context.EventType),
                new KeyValuePair<string, object?>("status", "success"));

            activity?.SetStatus(ActivityStatusCode.Ok);
            LogEventProcessedSuccessfully(_logger, context.EventId, context.EventType);
        }
        else
        {
            await _idempotencyManager.ReleaseLockAsync(
                context.TenantId,
                idempotencyKey,
                scope,
                acquireResult.LockToken,
                CancellationToken.None);

            EventFlowDiagnostics.EventsProcessed.Add(
                1,
                new KeyValuePair<string, object?>("tenant_id", context.TenantId),
                new KeyValuePair<string, object?>("event_type", context.EventType),
                new KeyValuePair<string, object?>("status", "failure"));

            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);
            LogEventProcessingFailed(_logger, context.EventId, context.EventType, result.Error.Code, result.Error.Message);
        }

        return result;
    }

    [LoggerMessage(EventId = 7001, Level = LogLevel.Information, Message = "Processing consumed event {EventId} of type '{EventType}' for tenant '{TenantId}'")]
    private static partial void LogProcessingEvent(ILogger logger, Guid eventId, string eventType, string tenantId);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning, Message = "No event handler registered for event type '{EventType}' (EventId: {EventId}). Event marked as processed.")]
    private static partial void LogNoHandlerFound(ILogger logger, string eventType, Guid eventId);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Information, Message = "Event {EventId} of type '{EventType}' processed successfully")]
    private static partial void LogEventProcessedSuccessfully(ILogger logger, Guid eventId, string eventType);

    [LoggerMessage(EventId = 7004, Level = LogLevel.Error, Message = "Event {EventId} of type '{EventType}' failed processing: [{ErrorCode}] {ErrorMessage}")]
    private static partial void LogEventProcessingFailed(ILogger logger, Guid eventId, string eventType, string errorCode, string errorMessage);

    [LoggerMessage(EventId = 7005, Level = LogLevel.Error, Message = "Unhandled exception processing event {EventId} of type '{EventType}'")]
    private static partial void LogUnhandledExceptionDuringProcessing(ILogger logger, Guid eventId, string eventType, Exception ex);

    [LoggerMessage(EventId = 7006, Level = LogLevel.Information, Message = "Event {EventId} ('{EventType}') was already processed (Key: '{IdempotencyKey}', Scope: '{Scope}'). Skipping duplicate execution.")]
    private static partial void LogEventAlreadyProcessed(ILogger logger, Guid eventId, string eventType, string idempotencyKey, string scope);

    [LoggerMessage(EventId = 7007, Level = LogLevel.Warning, Message = "In-flight concurrency conflict for event {EventId} ('{EventType}', Key: '{IdempotencyKey}', Scope: '{Scope}'). Execution deferred.")]
    private static partial void LogEventConflictInFlight(ILogger logger, Guid eventId, string eventType, string idempotencyKey, string scope);
}
