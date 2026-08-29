using EventFlow.Application.Abstractions;
using EventFlow.Domain.Common;
using EventFlow.Domain.Outbox;
using Microsoft.Extensions.Logging;

namespace EventFlow.Worker.Publishers;

/// <summary>
/// Development / initial fallback implementation of <see cref="IEventPublisher"/>
/// used prior to Phase 4 (RabbitMQ integration).
/// </summary>
public sealed partial class LoggingEventPublisher : IEventPublisher
{
    private readonly ILogger<LoggingEventPublisher> _logger;

    public LoggingEventPublisher(ILogger<LoggingEventPublisher> logger)
    {
        _logger = logger;
    }

    public Task<Result> PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        LogEventPublished(_logger, message.EventId, message.EventType, message.TenantId, message.TraceParent);
        return Task.FromResult(Result.Success());
    }

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "[Phase 3 Local Broker] Published Event {EventId} of type '{EventType}' for Tenant '{TenantId}' with TraceParent '{TraceParent}'")]
    private static partial void LogEventPublished(
        ILogger logger,
        Guid eventId,
        string eventType,
        string tenantId,
        string? traceParent);
}
