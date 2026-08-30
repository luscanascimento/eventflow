using System.Text.Json;
using EventFlow.Application.Events.Consumers;
using EventFlow.Domain.Common;
using Microsoft.Extensions.Logging;

namespace EventFlow.Application.Events.Handlers;

/// <summary>
/// Domain event handler for processing 'payment.settled.v1' events.
/// Deserializes safely, validates invariants, and simulates settlement execution.
/// </summary>
public sealed partial class PaymentSettledHandler : IEventConsumerHandler
{
    public const string TargetEventType = "payment.settled.v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<PaymentSettledHandler> _logger;

    public PaymentSettledHandler(ILogger<PaymentSettledHandler> logger)
    {
        _logger = logger;
    }

    public bool CanHandle(string eventType) =>
        string.Equals(eventType, TargetEventType, StringComparison.OrdinalIgnoreCase);

    public Task<Result> HandleAsync(ConsumedEventContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        PaymentSettledPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PaymentSettledPayload>(context.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            LogInvalidJsonPayload(_logger, context.EventId, ex.Message);
            return Task.FromResult(Result.Failure(
                DomainError.Validation("PaymentSettled.InvalidPayload", $"Malformed JSON payload: {ex.Message}")));
        }

        if (payload is null)
        {
            return Task.FromResult(Result.Failure(
                DomainError.Validation("PaymentSettled.NullPayload", "Deserialized payload is null.")));
        }

        if (string.IsNullOrWhiteSpace(payload.OrderId))
        {
            return Task.FromResult(Result.Failure(
                DomainError.Validation("PaymentSettled.EmptyOrderId", "OrderId is required.")));
        }

        if (payload.Amount <= 0)
        {
            return Task.FromResult(Result.Failure(
                DomainError.Validation("PaymentSettled.InvalidAmount", "Payment amount must be greater than zero.")));
        }

        if (string.IsNullOrWhiteSpace(payload.Currency))
        {
            return Task.FromResult(Result.Failure(
                DomainError.Validation("PaymentSettled.EmptyCurrency", "Currency is required.")));
        }

        LogPaymentSettledProcessed(
            _logger,
            payload.OrderId,
            payload.Amount,
            payload.Currency,
            context.TenantId,
            context.EventId);

        return Task.FromResult(Result.Success());
    }

    [LoggerMessage(EventId = 7101, Level = LogLevel.Warning, Message = "Failed to deserialize JSON payload for event {EventId}: {Error}")]
    private static partial void LogInvalidJsonPayload(ILogger logger, Guid eventId, string error);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Information, Message = "Payment settled successfully for Order {OrderId}: {Amount} {Currency} (Tenant: {TenantId}, EventId: {EventId})")]
    private static partial void LogPaymentSettledProcessed(
        ILogger logger,
        string orderId,
        decimal amount,
        string currency,
        string tenantId,
        Guid eventId);
}
