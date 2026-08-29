using RabbitMQ.Client;

namespace EventFlow.Infrastructure.Messaging;

/// <summary>
/// Thread-safe abstraction managing the underlying RabbitMQ connection and exchange topology.
/// </summary>
public interface IRabbitMqConnection : IAsyncDisposable
{
    Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default);
}
