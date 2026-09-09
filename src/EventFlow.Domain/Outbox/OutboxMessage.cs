using EventFlow.Domain.Common;
using EventFlow.Domain.Events;

namespace EventFlow.Domain.Outbox;

/// <summary>
/// Represents an event waiting to be reliably dispatched to the message broker (RabbitMQ).
/// Supports atomic leasing and concurrency control across multiple worker replicas.
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
    public string? LockToken { get; private set; }
    public DateTimeOffset? LockedUntilUtc { get; private set; }
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

    public Result Claim(string lockToken, DateTimeOffset lockedUntil)
    {
        if (Status == OutboxStatus.Published)
        {
            return Result.Failure(DomainError.Conflict("OutboxMessage.AlreadyPublished", "Cannot claim an already published message."));
        }

        Status = OutboxStatus.Processing;
        LockToken = lockToken;
        LockedUntilUtc = lockedUntil;
        return Result.Success();
    }

    public Result MarkProcessing(string? lockToken = null, DateTimeOffset? lockedUntil = null)
    {
        if (Status == OutboxStatus.Published)
        {
            return Result.Failure(DomainError.Conflict("OutboxMessage.AlreadyPublished", "Cannot process an already published message."));
        }

        Status = OutboxStatus.Processing;
        LockToken = lockToken;
        LockedUntilUtc = lockedUntil;
        return Result.Success();
    }

    public Result MarkPublished(DateTimeOffset publishedAt)
    {
        return MarkPublished(LockToken, publishedAt);
    }

    public Result MarkPublished(string? lockToken, DateTimeOffset publishedAt)
    {
        if (Status == OutboxStatus.Published)
        {
            return Result.Failure(DomainError.Conflict("OutboxMessage.AlreadyPublished", "Message is already marked as published."));
        }

        if (!string.IsNullOrEmpty(LockToken) && !string.Equals(LockToken, lockToken, StringComparison.Ordinal))
        {
            return Result.Failure(DomainError.Conflict("OutboxMessage.LockTokenMismatch", "Lock token mismatch; cannot mark published."));
        }

        Status = OutboxStatus.Published;
        ProcessedAt = publishedAt;
        LockToken = null;
        LockedUntilUtc = null;
        LastError = null;
        return Result.Success();
    }

    public void MarkFailed(string error, int maxRetries, TimeSpan retryBackoff, DateTimeOffset now)
    {
        MarkFailed(LockToken, error, maxRetries, retryBackoff, now);
    }

    public Result MarkFailed(string? lockToken, string error, int maxRetries, TimeSpan retryBackoff, DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(LockToken) && !string.IsNullOrEmpty(lockToken) && !string.Equals(LockToken, lockToken, StringComparison.Ordinal))
        {
            return Result.Failure(DomainError.Conflict("OutboxMessage.LockTokenMismatch", "Lock token mismatch; cannot mark failed."));
        }

        RetryCount++;
        LastError = error;
        LockToken = null;
        LockedUntilUtc = null;

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

        return Result.Success();
    }
}
