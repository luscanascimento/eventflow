using System.Text;
using EventFlow.Application.Events.Consumers;
using EventFlow.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace EventFlow.Infrastructure.Messaging;

/// <summary>
/// Production-grade RabbitMQ consumer implementing async stream consumption,
/// W3C TraceContext extraction, QoS backpressure, and manual acknowledgment.
/// </summary>
public sealed partial class RabbitMqConsumer : IRabbitMqConsumer
{
    private readonly IRabbitMqConnection _connection;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConsumer> _logger;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private IChannel? _channel;
    private string? _consumerTag;
    private bool _isDisposed;

    public RabbitMqConsumer(
        IRabbitMqConnection connection,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqConsumer> logger)
    {
        _connection = connection;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(
        Func<ConsumedEventContext, CancellationToken, Task<Result>> messageHandler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageHandler);
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_channel != null && _consumerTag != null)
            {
                return;
            }

            LogDeclaringTopology(_logger, _options.QueueName, _options.ExchangeName, _options.RoutingKeyPattern);

            _channel = await _connection.CreateChannelAsync(cancellationToken);

            // 1. Declare Durable Queue
            await _channel.QueueDeclareAsync(
                queue: _options.QueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: cancellationToken);

            // 2. Bind Queue to Topic Exchange
            await _channel.QueueBindAsync(
                queue: _options.QueueName,
                exchange: _options.ExchangeName,
                routingKey: _options.RoutingKeyPattern,
                arguments: null,
                cancellationToken: cancellationToken);

            // 3. Set QoS Prefetch Count for backpressure control
            await _channel.BasicQosAsync(
                prefetchSize: 0,
                prefetchCount: _options.PrefetchCount,
                global: false,
                cancellationToken: cancellationToken);

            // 4. Create Async Consumer
            var consumer = new AsyncEventingBasicConsumer(_channel);
            consumer.ReceivedAsync += async (sender, ea) =>
            {
                await HandleMessageAsync(ea, messageHandler, cancellationToken);
            };

            // 5. Start Consuming
            _consumerTag = await _channel.BasicConsumeAsync(
                queue: _options.QueueName,
                autoAck: false,
                consumer: consumer,
                cancellationToken: cancellationToken);

            LogConsumerStarted(_logger, _options.QueueName, _consumerTag);
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task HandleMessageAsync(
        BasicDeliverEventArgs ea,
        Func<ConsumedEventContext, CancellationToken, Task<Result>> messageHandler,
        CancellationToken cancellationToken)
    {
        if (_channel == null)
        {
            return;
        }

        string rawPayload = Encoding.UTF8.GetString(ea.Body.Span);
        var headers = ea.BasicProperties.Headers;

        string? tenantId = AmqpHeaderParser.GetString(headers, "tenant_id");
        string? eventType = AmqpHeaderParser.GetString(headers, "event_type");
        string? traceParent = AmqpHeaderParser.GetString(headers, "traceparent");
        string? outboxId = AmqpHeaderParser.GetString(headers, "outbox_id");

        // Parse or generate EventId from MessageId
        Guid eventId = Guid.TryParse(ea.BasicProperties.MessageId, out var parsedGuid)
            ? parsedGuid
            : Guid.NewGuid();

        string resolvedEventType = !string.IsNullOrWhiteSpace(eventType)
            ? eventType
            : ea.RoutingKey;

        string resolvedTenantId = !string.IsNullOrWhiteSpace(tenantId)
            ? tenantId
            : "unknown";

        var allHeaders = AmqpHeaderParser.ExtractAll(headers);

        var context = new ConsumedEventContext(
            EventId: eventId,
            EventType: resolvedEventType,
            TenantId: resolvedTenantId,
            TraceParent: traceParent,
            OutboxId: outboxId,
            RoutingKey: ea.RoutingKey,
            Payload: rawPayload,
            ConsumedAt: DateTimeOffset.UtcNow,
            Headers: allHeaders,
            Redelivered: ea.Redelivered);

        try
        {
            var result = await messageHandler(context, cancellationToken);

            if (result.IsSuccess)
            {
                await _channel.BasicAckAsync(
                    deliveryTag: ea.DeliveryTag,
                    multiple: false,
                    cancellationToken: cancellationToken);

                LogMessageAcknowledged(_logger, eventId, ea.DeliveryTag);
            }
            else
            {
                // Reject message without requeue to avoid poison message loops
                await _channel.BasicNackAsync(
                    deliveryTag: ea.DeliveryTag,
                    multiple: false,
                    requeue: false,
                    cancellationToken: cancellationToken);

                LogMessageRejected(_logger, eventId, ea.DeliveryTag, result.Error.Message);
            }
        }
        catch (Exception ex)
        {
            LogMessageProcessingError(_logger, eventId, ea.DeliveryTag, ex);

            try
            {
                await _channel.BasicNackAsync(
                    deliveryTag: ea.DeliveryTag,
                    multiple: false,
                    requeue: false,
                    cancellationToken: cancellationToken);
            }
            catch (Exception nackEx)
            {
                LogNackError(_logger, eventId, nackEx);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_channel != null && _consumerTag != null)
            {
                try
                {
                    await _channel.BasicCancelAsync(_consumerTag, cancellationToken: cancellationToken);
                    LogConsumerCancelled(_logger, _consumerTag);
                }
                catch (Exception ex)
                {
                    LogConsumerCancelError(_logger, ex);
                }

                _consumerTag = null;
            }

            if (_channel != null)
            {
                try
                {
                    await _channel.CloseAsync(cancellationToken);
                    await _channel.DisposeAsync();
                }
                catch (Exception ex)
                {
                    LogChannelCloseError(_logger, ex);
                }

                _channel = null;
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        await StopAsync();
        _startLock.Dispose();
    }

    [LoggerMessage(EventId = 6101, Level = LogLevel.Information, Message = "Declaring AMQP topology: Queue '{QueueName}' bound to Exchange '{ExchangeName}' with pattern '{RoutingPattern}'")]
    private static partial void LogDeclaringTopology(ILogger logger, string queueName, string exchangeName, string routingPattern);

    [LoggerMessage(EventId = 6102, Level = LogLevel.Information, Message = "AMQP Consumer started on queue '{QueueName}' with consumer tag '{ConsumerTag}'")]
    private static partial void LogConsumerStarted(ILogger logger, string queueName, string consumerTag);

    [LoggerMessage(EventId = 6103, Level = LogLevel.Debug, Message = "Message {EventId} (DeliveryTag: {DeliveryTag}) acknowledged successfully")]
    private static partial void LogMessageAcknowledged(ILogger logger, Guid eventId, ulong deliveryTag);

    [LoggerMessage(EventId = 6104, Level = LogLevel.Warning, Message = "Message {EventId} (DeliveryTag: {DeliveryTag}) rejected (nack). Reason: {Reason}")]
    private static partial void LogMessageRejected(ILogger logger, Guid eventId, ulong deliveryTag, string reason);

    [LoggerMessage(EventId = 6105, Level = LogLevel.Error, Message = "Unexpected error processing message {EventId} (DeliveryTag: {DeliveryTag})")]
    private static partial void LogMessageProcessingError(ILogger logger, Guid eventId, ulong deliveryTag, Exception ex);

    [LoggerMessage(EventId = 6106, Level = LogLevel.Error, Message = "Failed to NACK message {EventId}")]
    private static partial void LogNackError(ILogger logger, Guid eventId, Exception ex);

    [LoggerMessage(EventId = 6107, Level = LogLevel.Information, Message = "Consumer '{ConsumerTag}' cancelled")]
    private static partial void LogConsumerCancelled(ILogger logger, string consumerTag);

    [LoggerMessage(EventId = 6108, Level = LogLevel.Warning, Message = "Error cancelling consumer")]
    private static partial void LogConsumerCancelError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 6109, Level = LogLevel.Warning, Message = "Error closing consumer channel")]
    private static partial void LogChannelCloseError(ILogger logger, Exception ex);
}
