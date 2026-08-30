using EventFlow.Domain.Common;

namespace EventFlow.Application.Events.Consumers;

/// <summary>
/// Contract for domain event handlers responsible for processing specific event types.
/// </summary>
public interface IEventConsumerHandler
{
    /// <summary>
    /// Determines whether this handler can process the specified event type.
    /// </summary>
    bool CanHandle(string eventType);

    /// <summary>
    /// Executes the business logic for the consumed event.
    /// </summary>
    Task<Result> HandleAsync(ConsumedEventContext context, CancellationToken cancellationToken = default);
}
