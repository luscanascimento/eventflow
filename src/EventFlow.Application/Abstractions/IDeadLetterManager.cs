using EventFlow.Application.DeadLetter;
using EventFlow.Domain.Common;

namespace EventFlow.Application.Abstractions;

/// <summary>
/// Manages dead letter queue inspection, purging, and safe event replay.
/// </summary>
public interface IDeadLetterManager
{
    /// <summary>
    /// Peeks pending messages currently stored in the Dead Letter Queue without acknowledging/removing them.
    /// </summary>
    Task<IReadOnlyList<DeadLetterMessage>> PeekMessagesAsync(int count = 10, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replays (republishes) up to <paramref name="maxCount"/> messages from the DLQ back to the primary topic exchange,
    /// removing them from the DLQ upon successful requeue.
    /// </summary>
    Task<Result<int>> ReplayMessagesAsync(int maxCount = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Purges all messages from the Dead Letter Queue.
    /// </summary>
    Task<Result<uint>> PurgeQueueAsync(CancellationToken cancellationToken = default);
}
