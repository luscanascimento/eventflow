using StackExchange.Redis;

namespace EventFlow.Infrastructure.Idempotency;

/// <summary>
/// Thread-safe abstraction providing access to the shared Redis multiplexer and database.
/// </summary>
public interface IRedisConnection : IAsyncDisposable
{
    /// <summary>
    /// Gets the active Redis database instance, or null if Redis is disabled or unreachable.
    /// </summary>
    IDatabase? GetDatabase();

    /// <summary>
    /// Checks if the underlying Redis connection is established and healthy.
    /// </summary>
    bool IsConnected { get; }
}
