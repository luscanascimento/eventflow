using System.Text;
using EventFlow.Application.Abstractions;
using EventFlow.Application.DeadLetter;
using EventFlow.Domain.Common;
using EventFlow.Infrastructure.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace EventFlow.Infrastructure.DeadLetter;

/// <summary>
/// Infrastructure manager providing DLQ inspection, purging, and safe idempotent replay
/// back to the primary RabbitMQ topic exchange.
/// </summary>
public sealed partial class RabbitMqDeadLetterManager : IDeadLetterManager
{
    private readonly IRabbitMqConnection _connection;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqDeadLetterManager> _logger;

    public RabbitMqDeadLetterManager(
        IRabbitMqConnection connection,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqDeadLetterManager> logger)
    {
        _connection = connection;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DeadLetterMessage>> PeekMessagesAsync(int count = 10, CancellationToken cancellationToken = default)
    {
        if (count <= 0)
        {
            return Array.Empty<DeadLetterMessage>();
        }

        await using var channel = await _connection.CreateChannelAsync(cancellationToken);

        var messages = new List<DeadLetterMessage>();
        var deliveryTagsToRequeue = new List<ulong>();

        try
        {
            for (int i = 0; i < count; i++)
            {
                var result = await channel.BasicGetAsync(_options.DlqQueueName, autoAck: false, cancellationToken);
                if (result == null)
                {
                    break;
                }

                deliveryTagsToRequeue.Add(result.DeliveryTag);

                string payload = Encoding.UTF8.GetString(result.Body.Span);
                var headers = result.BasicProperties.Headers;

                string? tenantId = AmqpHeaderParser.GetString(headers, "tenant_id");
                string? eventType = AmqpHeaderParser.GetString(headers, "event_type");
                string? traceParent = AmqpHeaderParser.GetString(headers, "traceparent");
                string? exceptionMsg = AmqpHeaderParser.GetString(headers, "x-exception-message");
                string? exceptionType = AmqpHeaderParser.GetString(headers, "x-exception-type");

                var allHeaders = AmqpHeaderParser.ExtractAll(headers);

                messages.Add(new DeadLetterMessage(
                    MessageId: result.BasicProperties.MessageId ?? Guid.NewGuid().ToString("D"),
                    RoutingKey: result.RoutingKey,
                    Payload: payload,
                    TenantId: tenantId,
                    EventType: eventType,
                    TraceParent: traceParent,
                    ExceptionMessage: exceptionMsg,
                    ExceptionType: exceptionType,
                    FailedAt: DateTimeOffset.UtcNow,
                    RetryCount: 0,
                    Headers: allHeaders));
            }
        }
        finally
        {
            // Re-queue peeked messages so they stay intact in the DLQ
            foreach (ulong tag in deliveryTagsToRequeue)
            {
                try
                {
                    await channel.BasicNackAsync(tag, multiple: false, requeue: true, cancellationToken);
                }
                catch (Exception ex)
                {
                    LogRequeueError(_logger, tag, ex);
                }
            }
        }

        LogPeekedMessages(_logger, messages.Count, _options.DlqQueueName);
        return messages;
    }

    public async Task<Result<int>> ReplayMessagesAsync(int maxCount = 50, CancellationToken cancellationToken = default)
    {
        if (maxCount <= 0)
        {
            return Result.Success(0);
        }

        await using var channel = await _connection.CreateChannelAsync(cancellationToken);
        int replayedCount = 0;

        for (int i = 0; i < maxCount; i++)
        {
            var result = await channel.BasicGetAsync(_options.DlqQueueName, autoAck: false, cancellationToken);
            if (result == null)
            {
                break;
            }

            try
            {
                // Re-publish back to the primary topic exchange
                var properties = new BasicProperties
                {
                    ContentType = result.BasicProperties.ContentType ?? "application/json",
                    DeliveryMode = DeliveryModes.Persistent,
                    MessageId = result.BasicProperties.MessageId,
                    CorrelationId = result.BasicProperties.CorrelationId,
                    Headers = result.BasicProperties.Headers != null
                        ? new Dictionary<string, object?>(result.BasicProperties.Headers)
                        : new Dictionary<string, object?>()
                };

                // Mark replay timestamp in headers
                properties.Headers["x-replayed-at"] = DateTimeOffset.UtcNow.ToString("O");

                await channel.BasicPublishAsync(
                    exchange: _options.ExchangeName,
                    routingKey: result.RoutingKey,
                    mandatory: true,
                    basicProperties: properties,
                    body: result.Body,
                    cancellationToken: cancellationToken);

                // Acknowledge and remove from DLQ upon successful replay
                await channel.BasicAckAsync(result.DeliveryTag, multiple: false, cancellationToken);
                replayedCount++;
            }
            catch (Exception ex)
            {
                LogReplayError(_logger, result.DeliveryTag, ex);
                // NACK with requeue so the message isn't lost on publish failure
                await channel.BasicNackAsync(result.DeliveryTag, multiple: false, requeue: true, cancellationToken);
                return Result.Failure<int>(DomainError.Failure("DLQ.ReplayFailed", $"Failed during message replay: {ex.Message}"));
            }
        }

        if (replayedCount > 0)
        {
            EventFlow.Domain.Diagnostics.EventFlowDiagnostics.DlqReplayed.Add(replayedCount);
        }

        LogReplayedBatch(_logger, replayedCount, _options.ExchangeName);
        return Result.Success(replayedCount);
    }

    public async Task<Result<uint>> PurgeQueueAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var channel = await _connection.CreateChannelAsync(cancellationToken);
            uint purgedCount = await channel.QueuePurgeAsync(_options.DlqQueueName, cancellationToken);

            if (purgedCount > 0)
            {
                EventFlow.Domain.Diagnostics.EventFlowDiagnostics.DlqPurged.Add(purgedCount);
            }

            LogPurgedDlq(_logger, purgedCount, _options.DlqQueueName);
            return Result.Success(purgedCount);
        }
        catch (Exception ex)
        {
            LogPurgeError(_logger, _options.DlqQueueName, ex);
            return Result.Failure<uint>(DomainError.Failure("DLQ.PurgeFailed", ex.Message));
        }
    }

    [LoggerMessage(EventId = 9301, Level = LogLevel.Information, Message = "Peeked {Count} messages from DLQ '{QueueName}'")]
    private static partial void LogPeekedMessages(ILogger logger, int count, string queueName);

    [LoggerMessage(EventId = 9302, Level = LogLevel.Information, Message = "Replayed {Count} messages from DLQ back to exchange '{ExchangeName}'")]
    private static partial void LogReplayedBatch(ILogger logger, int count, string exchangeName);

    [LoggerMessage(EventId = 9303, Level = LogLevel.Information, Message = "Purged {Count} messages from DLQ '{QueueName}'")]
    private static partial void LogPurgedDlq(ILogger logger, uint count, string queueName);

    [LoggerMessage(EventId = 9304, Level = LogLevel.Error, Message = "Failed to requeue peeked message {DeliveryTag}")]
    private static partial void LogRequeueError(ILogger logger, ulong deliveryTag, Exception ex);

    [LoggerMessage(EventId = 9305, Level = LogLevel.Error, Message = "Error replaying message {DeliveryTag} from DLQ")]
    private static partial void LogReplayError(ILogger logger, ulong deliveryTag, Exception ex);

    [LoggerMessage(EventId = 9306, Level = LogLevel.Error, Message = "Error purging DLQ '{QueueName}'")]
    private static partial void LogPurgeError(ILogger logger, string queueName, Exception ex);
}
