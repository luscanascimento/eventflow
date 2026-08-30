namespace EventFlow.Infrastructure.Idempotency;

/// <summary>
/// Configuration options for Redis distributed locking and fast idempotency cache.
/// </summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    public string ConnectionString { get; set; } = "localhost:6379";
    public string KeyPrefix { get; set; } = "eventflow:";
    public int LockTtlSeconds { get; set; } = 30;
    public int CacheTtlHours { get; set; } = 24;
    public bool Enabled { get; set; } = true;
}
