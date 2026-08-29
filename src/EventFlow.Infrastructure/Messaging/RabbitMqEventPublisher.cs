using System.Text;
using EventFlow.Application.Abstractions;
using EventFlow.Domain.Common;
using EventFlow.Domain.Outbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace EventFlow.Infrastructure.Messaging;

/// <summary>
/// High-performance RabbitMQ publisher implementing <see cref="IEventPublisher"/>.
/// Publishes outbox events to the topic exchange with persistent delivery mode and W3C TraceContext headers.
/// </summary>
public sealed partial class RabbitMqEventPublisher : IEventPublisher
{
    private readonly IRabbitMqConnection _connection;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqEventPublisher> _logger;

    public RabbitMqEventPublisher(
        IRabbitMqConnection connection,
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqEventPublisher> logger)
    {
        _connection = connection;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result> PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var channel = await _connection.CreateChannelAsync(cancellationToken);

            var properties = new BasicProperties
            {
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                MessageId = message.EventId.ToString("D"),
                CorrelationId = message.EventId.ToString("D"),
                Headers = new Dictionary<string, object?>
                {
                    ["tenant_id"] = message.TenantId,
                    ["event_type"] = message.EventType,
                    ["outbox_id"] = message.Id.ToString("D")
                }
            };

            if (!string.IsNullOrWhiteSpace(message.TraceParent))
            {
                properties.Headers["traceparent"] = message.TraceParent;
            }

            byte[] body = Encoding.UTF8.GetBytes(message.Payload);

            await channel.BasicPublishAsync(
                exchange: _options.ExchangeName,
                routingKey: message.RoutingKey,
                mandatory: true,
                basicProperties: properties,
                body: body,
                cancellationToken: cancellationToken);

            LogPublishedToBroker(_logger, message.Id, message.EventId, _options.ExchangeName, message.RoutingKey);
            return Result.Success();
        }
        catch (Exception ex)
        {
            LogPublishFailed(_logger, message.Id, message.EventId, ex.Message);
            return Result.Failure(DomainError.Failure("RabbitMQ.PublishFailed", ex.Message));
        }
    }

    [LoggerMessage(EventId = 6001, Level = LogLevel.Debug, Message = "Published outbox message {OutboxId} (Event {EventId}) to exchange '{Exchange}' with routing key '{RoutingKey}'")]
    private static partial void LogPublishedToBroker(ILogger logger, Guid outboxId, Guid eventId, string exchange, string routingKey);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Error, Message = "Failed to publish outbox message {OutboxId} (Event {EventId}) to RabbitMQ: {Error}")]
    private static partial void LogPublishFailed(ILogger logger, Guid outboxId, Guid eventId, string error);
}
