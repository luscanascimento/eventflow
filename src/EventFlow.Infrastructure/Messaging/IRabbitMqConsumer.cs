using EventFlow.Application.Events.Consumers;
using EventFlow.Domain.Common;

namespace EventFlow.Infrastructure.Messaging;

/// <summary>
/// Manages the consumption loop from the RabbitMQ broker queue, acknowledging or rejecting messages.
/// </summary>
public interface IRabbitMqConsumer : IAsyncDisposable
{
    /// <summary>
    /// Starts consuming messages from the configured AMQP queue using the specified handler delegate.
    /// </summary>
    Task StartAsync(Func<ConsumedEventContext, CancellationToken, Task<Result>> messageHandler, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gracefully stops the active AMQP consumer.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
