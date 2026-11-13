using EventFlow.Application.Abstractions;
using EventFlow.Domain.Common;
using EventFlow.Domain.Diagnostics;
using EventFlow.Domain.Outbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventFlow.Application.Outbox;

/// <summary>
/// Core application service responsible for claiming, publishing, and managing lease lifecycle of pending outbox messages.
/// Enforces at-least-once delivery without holding long-lived database transactions during broker network dispatches.
/// </summary>
public sealed partial class OutboxProcessor
{
    private static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(30);

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
        string lockToken = Guid.NewGuid().ToString("N");

        var messages = await _outboxStore.ClaimPendingBatchAsync(
            _options.BatchSize,
            lockToken,
            DefaultLeaseDuration,
            now,
            cancellationToken);

        if (messages.Count == 0)
        {
            return 0;
        }

        LogBatchProcessingStarted(_logger, messages.Count);

        using var batchActivity = EventFlowDiagnostics.ActivitySource.StartActivity("EventFlow.OutboxBatchDispatch");
        batchActivity?.SetTag("eventflow.outbox.batch_size", messages.Count);

        int processedCount = 0;

        foreach (var message in messages)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            Result publishResult;
            try
            {
                publishResult = await _publisher.PublishAsync(message, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                publishResult = Result.Failure(DomainError.Failure("Publisher.Exception", ex.Message));
            }

            processedCount++;

            if (publishResult.IsSuccess)
            {
                var markResult = await _outboxStore.MarkPublishedAsync(
                    message.Id,
                    lockToken,
                    _dateTimeProvider.UtcNow,
                    CancellationToken.None);

                if (markResult.IsSuccess)
                {
                    EventFlowDiagnostics.OutboxDispatched.Add(
                        1,
                        new KeyValuePair<string, object?>("status", "published"),
                        new KeyValuePair<string, object?>("event_type", message.EventType));

                    LogMessagePublished(_logger, message.Id, message.EventType, message.TenantId);
                }
                else
                {
                    LogLeaseLost(_logger, message.Id, markResult.Error.Message);
                }
            }
            else
            {
                int nextAttemptNumber = message.RetryCount + 1;
                var backoff = CalculateExponentialBackoff(message.RetryCount);
                DateTimeOffset failedAt = _dateTimeProvider.UtcNow;

                var markResult = await _outboxStore.MarkFailedAsync(
                    message.Id,
                    lockToken,
                    publishResult.Error.Message,
                    _options.MaxRetries,
                    backoff,
                    failedAt,
                    CancellationToken.None);

                if (markResult.IsSuccess)
                {
                    if (nextAttemptNumber >= _options.MaxRetries)
                    {
                        EventFlowDiagnostics.OutboxDispatched.Add(
                            1,
                            new KeyValuePair<string, object?>("status", "dead_lettered"),
                            new KeyValuePair<string, object?>("event_type", message.EventType));

                        EventFlowDiagnostics.DlqMessages.Add(
                            1,
                            new KeyValuePair<string, object?>("source", "outbox"),
                            new KeyValuePair<string, object?>("event_type", message.EventType));

                        LogMessageDeadLettered(_logger, message.Id, message.EventType, publishResult.Error.Message, nextAttemptNumber);
                    }
                    else
                    {
                        EventFlowDiagnostics.OutboxDispatched.Add(
                            1,
                            new KeyValuePair<string, object?>("status", "failed_retry"),
                            new KeyValuePair<string, object?>("event_type", message.EventType));

                        LogMessageFailedAndScheduledRetry(_logger, message.Id, message.EventType, publishResult.Error.Message, nextAttemptNumber, failedAt.Add(backoff));
                    }
                }
                else
                {
                    LogLeaseLost(_logger, message.Id, markResult.Error.Message);
                }
            }
        }

        LogBatchProcessingFinished(_logger, processedCount);
        return processedCount;
    }

    private TimeSpan CalculateExponentialBackoff(int retryCount)
    {
        // Exponential backoff: BaseDelay * 2^(retryCount)
        double delaySeconds = Math.Pow(2, Math.Max(0, retryCount)) * _options.BaseRetryDelaySeconds;
        return TimeSpan.FromSeconds(Math.Min(delaySeconds, 300)); // Cap at 5 minutes
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Debug, Message = "Started processing batch of {BatchSize} claimed outbox messages")]
    private static partial void LogBatchProcessingStarted(ILogger logger, int batchSize);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Lost lease for outbox message {MessageId}: {Reason}")]
    private static partial void LogLeaseLost(ILogger logger, Guid messageId, string reason);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "Successfully published outbox message {MessageId} for EventType '{EventType}' and Tenant '{TenantId}'")]
    private static partial void LogMessagePublished(ILogger logger, Guid messageId, string eventType, string tenantId);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Warning, Message = "Failed to publish outbox message {MessageId} for EventType '{EventType}': {Error}. Scheduled attempt #{RetryCount} at {NextRetryAt}")]
    private static partial void LogMessageFailedAndScheduledRetry(ILogger logger, Guid messageId, string eventType, string error, int retryCount, DateTimeOffset? nextRetryAt);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Error, Message = "Outbox message {MessageId} for EventType '{EventType}' reached max retries ({RetryCount}) and was moved to DeadLettered: {Error}")]
    private static partial void LogMessageDeadLettered(ILogger logger, Guid messageId, string eventType, string error, int retryCount);

    [LoggerMessage(EventId = 2006, Level = LogLevel.Debug, Message = "Finished processing batch of {BatchSize} outbox messages")]
    private static partial void LogBatchProcessingFinished(ILogger logger, int batchSize);
}
