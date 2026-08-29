using EventFlow.Domain.Common;
using EventFlow.Domain.Outbox;

namespace EventFlow.Application.Abstractions;

/// <summary>
/// Abstraction for publishing outbox messages to the message broker.
/// </summary>
public interface IEventPublisher
{
    Task<Result> PublishAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}
