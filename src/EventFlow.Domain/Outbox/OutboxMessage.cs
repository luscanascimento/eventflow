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
    public string? IdempotencyKey { get; private set; }
    public string? CorrelationId { get; private set; }
    public int Version { get; private set; } = 1;
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
        ArgumentNullException.ThrowIfNull(envelope);

        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            EventId = envelope.EventId,
            TenantId = envelope.TenantId,
            EventType = envelope.EventType,
            RoutingKey = envelope.EventType,
            Payload = envelope.Payload,
            TraceParent = envelope.TraceParent,
            IdempotencyKey = envelope.IdempotencyKey,
            CorrelationId = envelope.CorrelationId,
            Version = envelope.Version,
            Status = OutboxStatus.Pending,
            CreatedAt = createdAt,
            RetryCount = 0
        };
    }

    public void EnrichEnvelopeMetadata(string? idempotencyKey, string? correlationId, int version)
    {
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            IdempotencyKey = idempotencyKey;
        }

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            CorrelationId = correlationId;
        }

        if (version >= 1)
        {
            Version = version;
        }
    }

    public Result Claim(string lockToken, DateTimeOffset lockedUntil)
    {
        if (Status == OutboxStatus.Published)
        {
            return Result.Failure(DomainError.Conflict("OutboxMessage.AlreadyPublished", "Cannot claim an already published message."));
        }

        if (Status == OutboxStatus.DeadLettered)
        {
            return Result.Failure(DomainError.Conflict("OutboxMessage.AlreadyDeadLettered", "Cannot claim an already dead-lettered message."));
        }

        if (string.IsNullOrWhiteSpace(lockToken))
        {
            return Result.Failure(DomainError.Validation("OutboxMessage.EmptyLockToken", "LockToken cannot be empty."));
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

    public Result MarkFailed(string error, int maxRetries, TimeSpan retryBackoff, DateTimeOffset now)
    {
        return MarkFailed(LockToken, error, maxRetries, retryBackoff, now);
    }

    public Result MarkFailed(string? lockToken, string error, int maxRetries, TimeSpan retryBackoff, DateTimeOffset now)
    {
        if (Status == OutboxStatus.Published)
        {
            return Result.Failure(DomainError.Conflict("OutboxMessage.AlreadyPublished", "Cannot mark an already published message as failed."));
        }

        if (!string.IsNullOrEmpty(LockToken) && !string.Equals(LockToken, lockToken, StringComparison.Ordinal))
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
