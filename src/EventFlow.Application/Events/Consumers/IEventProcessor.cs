using EventFlow.Domain.Common;

namespace EventFlow.Application.Events.Consumers;

/// <summary>
/// Orchestrator responsible for routing and processing consumed events to the appropriate event handlers.
/// </summary>
public interface IEventProcessor
{
    /// <summary>
    /// Processes a single consumed event context.
    /// </summary>
    Task<Result> ProcessAsync(ConsumedEventContext context, CancellationToken cancellationToken = default);
}
