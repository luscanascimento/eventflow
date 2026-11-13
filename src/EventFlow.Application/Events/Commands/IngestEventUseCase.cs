using System.Diagnostics;
using EventFlow.Application.Abstractions;
using EventFlow.Domain.Common;
using EventFlow.Domain.Diagnostics;
using EventFlow.Domain.Events;
using EventFlow.Domain.Outbox;
using FluentValidation;

namespace EventFlow.Application.Events.Commands;

/// <summary>
/// Use case orchestrating the atomic validation, normalization, and persistence of ingested events and outbox messages.
/// </summary>
public sealed class IngestEventUseCase
{
    private readonly IValidator<IngestEventCommand> _validator;
    private readonly IEventStore _eventStore;
    private readonly IDateTimeProvider _dateTimeProvider;

    public IngestEventUseCase(
        IValidator<IngestEventCommand> validator,
        IEventStore eventStore,
        IDateTimeProvider dateTimeProvider)
    {
        _validator = validator;
        _eventStore = eventStore;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<Result<IngestEventResponse>> ExecuteAsync(
        IngestEventCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Activity? activity;
        if (!string.IsNullOrWhiteSpace(command.TraceParent) &&
            ActivityContext.TryParse(command.TraceParent, null, out var parentContext))
        {
            activity = EventFlowDiagnostics.ActivitySource.StartActivity(
                "EventFlow.IngestEvent",
                ActivityKind.Server,
                parentContext);
        }
        else
        {
            activity = EventFlowDiagnostics.ActivitySource.StartActivity("EventFlow.IngestEvent");
        }

        using (activity)
        {
            activity?.SetTag("eventflow.tenant_id", command.TenantId);
            activity?.SetTag("eventflow.event_type", command.EventType);
            activity?.SetTag("eventflow.idempotency_key", command.IdempotencyKey);

            var validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                var firstError = validationResult.Errors[0];
                activity?.SetStatus(ActivityStatusCode.Error, firstError.ErrorMessage);
                return Result.Failure<IngestEventResponse>(
                    DomainError.Validation(firstError.PropertyName, firstError.ErrorMessage));
            }

            bool alreadyExists = await _eventStore.ExistsByIdempotencyKeyAsync(
                command.TenantId,
                command.IdempotencyKey,
                cancellationToken);

            if (alreadyExists)
            {
                return Result.Failure<IngestEventResponse>(
                    DomainError.Conflict(
                        "Event.DuplicateIdempotencyKey",
                        $"An event with idempotency key '{command.IdempotencyKey}' has already been accepted for tenant '{command.TenantId}'."));
            }

            DateTimeOffset now = _dateTimeProvider.UtcNow;
            Guid eventId = command.EventId.HasValue && command.EventId.Value != Guid.Empty
                ? command.EventId.Value
                : Guid.NewGuid();

            DateTimeOffset occurredAt = command.OccurredAt ?? now;
            string correlationId = !string.IsNullOrWhiteSpace(command.CorrelationId)
                ? command.CorrelationId
                : eventId.ToString("D");

            string? effectiveTraceParent = !string.IsNullOrWhiteSpace(command.TraceParent)
                ? command.TraceParent
                : (activity?.Id ?? Activity.Current?.Id);

            int version = command.Version.GetValueOrDefault(1);
            string rawPayload = command.Payload.GetRawText();

            var envelopeResult = EventEnvelope.Create(
                eventId: eventId,
                eventType: command.EventType,
                version: version,
                source: command.Source,
                occurredAt: occurredAt,
                correlationId: correlationId,
                traceParent: effectiveTraceParent,
                tenantId: command.TenantId,
                idempotencyKey: command.IdempotencyKey,
                payload: rawPayload,
                metadata: command.Metadata);

            if (envelopeResult.IsFailure)
            {
                return Result.Failure<IngestEventResponse>(envelopeResult.Error);
            }

            EventEnvelope envelope = envelopeResult.Value;
            EventRecord eventRecord = EventRecord.FromEnvelope(envelope, now);
            OutboxMessage outboxMessage = OutboxMessage.FromEnvelope(envelope, now);

            Result saveResult = await _eventStore.SaveEventAndOutboxAsync(
                eventRecord,
                outboxMessage,
                cancellationToken);

            if (saveResult.IsFailure)
            {
                activity?.SetStatus(ActivityStatusCode.Error, saveResult.Error.Message);
                return Result.Failure<IngestEventResponse>(saveResult.Error);
            }

            EventFlowDiagnostics.EventsIngested.Add(
                1,
                new KeyValuePair<string, object?>("tenant_id", command.TenantId),
                new KeyValuePair<string, object?>("event_type", command.EventType));

            activity?.SetTag("eventflow.event_id", envelope.EventId.ToString("D"));
            activity?.SetStatus(ActivityStatusCode.Ok);

            return Result.Success(new IngestEventResponse(
                EventId: envelope.EventId,
                Status: "Accepted",
                IngestedAt: now));
        }
    }
}
