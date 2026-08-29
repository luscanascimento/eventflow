using EventFlow.Application.Abstractions;
using EventFlow.Domain.Common;
using EventFlow.Domain.Outbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventFlow.Application.Outbox;

/// <summary>
/// Core application service responsible for fetching, publishing, and updating status of pending outbox messages.
/// </summary>
public sealed partial class OutboxProcessor
{
    private readonly IOutboxStore _outboxStore;
    private readonly IEventPublisher _publisher;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly OutboxProcessorOptions _options;

    public OutboxProcessor(
        IOutboxStore outboxStore,
        IEventPublisher publisher,
        IDateTimeProvider dateTimeProvider,
        ILogger<OutboxProcessor> logger,
        IOptions<OutboxProcessorOptions> options)
    {
        _outboxStore = outboxStore;
        _publisher = publisher;
        _dateTimeProvider = dateTimeProvider;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<int> ProcessPendingMessagesAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _dateTimeProvider.UtcNow;

        var messages = await _outboxStore.FetchPendingBatchAsync(
            _options.BatchSize,
            now,
            cancellationToken);

        if (messages.Count == 0)
        {
            return 0;
        }

        LogBatchProcessingStarted(_logger, messages.Count);

        foreach (var message in messages)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var processingResult = message.MarkProcessing();
            if (processingResult.IsFailure)
            {
                LogMessageSkipped(_logger, message.Id, processingResult.Error.Message);
                continue;
            }

            Result publishResult;
            try
            {
                publishResult = await _publisher.PublishAsync(message, cancellationToken);
            }
            catch (Exception ex)
            {
                publishResult = Result.Failure(DomainError.Failure("Publisher.Exception", ex.Message));
            }

            if (publishResult.IsSuccess)
            {
                message.MarkPublished(_dateTimeProvider.UtcNow);
                LogMessagePublished(_logger, message.Id, message.EventType, message.TenantId);
            }
            else
            {
                var backoff = CalculateExponentialBackoff(message.RetryCount);
                message.MarkFailed(publishResult.Error.Message, _options.MaxRetries, backoff, _dateTimeProvider.UtcNow);

                if (message.Status == OutboxStatus.DeadLettered)
                {
                    LogMessageDeadLettered(_logger, message.Id, message.EventType, publishResult.Error.Message, message.RetryCount);
                }
                else
                {
                    LogMessageFailedAndScheduledRetry(_logger, message.Id, message.EventType, publishResult.Error.Message, message.RetryCount, message.NextRetryAt);
                }
            }
        }

        await _outboxStore.SaveChangesAsync(cancellationToken);

        LogBatchProcessingFinished(_logger, messages.Count);
        return messages.Count;
    }

    private TimeSpan CalculateExponentialBackoff(int retryCount)
    {
        // Exponential backoff: BaseDelay * 2^(retryCount)
        double delaySeconds = Math.Pow(2, Math.Max(0, retryCount)) * _options.BaseRetryDelaySeconds;
        return TimeSpan.FromSeconds(Math.Min(delaySeconds, 300)); // Cap at 5 minutes
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Debug, Message = "Started processing batch of {BatchSize} pending outbox messages")]
    private static partial void LogBatchProcessingStarted(ILogger logger, int batchSize);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Skipped outbox message {MessageId}: {Reason}")]
    private static partial void LogMessageSkipped(ILogger logger, Guid messageId, string reason);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "Successfully published outbox message {MessageId} for EventType '{EventType}' and Tenant '{TenantId}'")]
    private static partial void LogMessagePublished(ILogger logger, Guid messageId, string eventType, string tenantId);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Warning, Message = "Failed to publish outbox message {MessageId} for EventType '{EventType}': {Error}. Scheduled attempt #{RetryCount} at {NextRetryAt}")]
    private static partial void LogMessageFailedAndScheduledRetry(ILogger logger, Guid messageId, string eventType, string error, int retryCount, DateTimeOffset? nextRetryAt);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Error, Message = "Outbox message {MessageId} for EventType '{EventType}' reached max retries ({RetryCount}) and was moved to DeadLettered: {Error}")]
    private static partial void LogMessageDeadLettered(ILogger logger, Guid messageId, string eventType, string error, int retryCount);

    [LoggerMessage(EventId = 2006, Level = LogLevel.Debug, Message = "Finished processing batch of {BatchSize} outbox messages")]
    private static partial void LogBatchProcessingFinished(ILogger logger, int batchSize);
}
