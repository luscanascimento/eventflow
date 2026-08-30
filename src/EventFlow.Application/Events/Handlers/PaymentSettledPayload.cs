namespace EventFlow.Application.Events.Handlers;

/// <summary>
/// Strongly-typed payload for the payment settled business event (payment.settled.v1).
/// </summary>
public sealed record PaymentSettledPayload(
    string OrderId,
    decimal Amount,
    string Currency,
    string Status);
