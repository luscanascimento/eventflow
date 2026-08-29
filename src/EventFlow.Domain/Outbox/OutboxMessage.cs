using EventFlow.Domain.Common;
using EventFlow.Domain.Events;

namespace EventFlow.Domain.Outbox;

/// <summary>
/// Represents an event waiting to be reliably dispatched to the message broker (RabbitMQ).
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public string TenantId { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public string RoutingKey { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public string? TraceParent { get; private set; }
    public OutboxStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }
    public DateTimeOffset? NextRetryAt { get; private set; }
    public int RetryCount { get; private set; }
    public string? LastError { get; private set; }

    // Required for EF Core
    private OutboxMessage() { }

    public static OutboxMessage FromEnvelope(EventEnvelope envelope, DateTimeOffset createdAt)
    {
        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventId = envelope.EventId,
            TenantId = envelope.TenantId,
            EventType = envelope.EventType,
            RoutingKey = envelope.EventType,
            Payload = envelope.Payload,
            TraceParent = envelope.TraceParent,
            Status = OutboxStatus.Pending,
            CreatedAt = createdAt,
            RetryCount = 0
        };
    }

    public Result MarkProcessing()
    {
        if (Status == OutboxStatus.Published)
        {
            return Result.Failure(DomainError.Conflict("OutboxMessage.AlreadyPublished", "Cannot process an already published message."));
        }

        Status = OutboxStatus.Processing;
        return Result.Success();
    }

    public Result MarkPublished(DateTimeOffset publishedAt)
    {
        if (Status == OutboxStatus.Published)
        {
            return Result.Failure(DomainError.Conflict("OutboxMessage.AlreadyPublished", "Message is already marked as published."));
        }

        Status = OutboxStatus.Published;
        ProcessedAt = publishedAt;
        LastError = null;
        return Result.Success();
    }

    public void MarkFailed(string error, int maxRetries, TimeSpan retryBackoff, DateTimeOffset now)
    {
        RetryCount++;
        LastError = error;

        if (RetryCount >= maxRetries)
        {
            Status = OutboxStatus.DeadLettered;
            ProcessedAt = now;
            NextRetryAt = null;
        }
        else
        {
            Status = OutboxStatus.Pending;
            NextRetryAt = now.Add(retryBackoff);
        }
    }
}
