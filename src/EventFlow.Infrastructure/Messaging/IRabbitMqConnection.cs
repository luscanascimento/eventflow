using RabbitMQ.Client;

namespace EventFlow.Infrastructure.Messaging;

/// <summary>
/// Thread-safe abstraction managing the underlying RabbitMQ connection and exchange topology.
/// </summary>
public interface IRabbitMqConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default);

    Task<IChannel> CreateChannelAsync(
        CreateChannelOptions options,
        CancellationToken cancellationToken = default);
}
