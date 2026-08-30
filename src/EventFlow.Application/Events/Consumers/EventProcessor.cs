using EventFlow.Domain.Common;
using Microsoft.Extensions.Logging;

namespace EventFlow.Application.Events.Consumers;

/// <summary>
/// Default event processor that routes incoming events to matching <see cref="IEventConsumerHandler"/> implementations.
/// </summary>
public sealed partial class EventProcessor : IEventProcessor
{
    private readonly IEnumerable<IEventConsumerHandler> _handlers;
    private readonly ILogger<EventProcessor> _logger;

    public EventProcessor(
        IEnumerable<IEventConsumerHandler> handlers,
        ILogger<EventProcessor> logger)
    {
        _handlers = handlers;
        _logger = logger;
    }

    public async Task<Result> ProcessAsync(ConsumedEventContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        LogProcessingEvent(_logger, context.EventId, context.EventType, context.TenantId);

        var handler = _handlers.FirstOrDefault(h => h.CanHandle(context.EventType));
        if (handler == null)
        {
            LogNoHandlerFound(_logger, context.EventType, context.EventId);
            // Non-handled event types are acknowledged without error to prevent blocking message flow
            return Result.Success();
        }

        try
        {
            var result = await handler.HandleAsync(context, cancellationToken);
            if (result.IsSuccess)
            {
                LogEventProcessedSuccessfully(_logger, context.EventId, context.EventType);
            }
            else
            {
                LogEventProcessingFailed(_logger, context.EventId, context.EventType, result.Error.Code, result.Error.Message);
            }

            return result;
        }
        catch (Exception ex)
        {
            LogUnhandledExceptionDuringProcessing(_logger, context.EventId, context.EventType, ex);
            return Result.Failure(DomainError.Failure("EventProcessor.UnhandledException", ex.Message));
        }
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
}
