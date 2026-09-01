using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Xunit;

namespace EventFlow.IntegrationTests.Fixtures;

/// <summary>
/// Shared test fixture managing real Docker containers for PostgreSQL, RabbitMQ, and Redis
/// during integration test suite execution.
/// </summary>
public sealed class DockerTestcontainersFixture : IAsyncLifetime, IDisposable
{
    private static readonly Lazy<bool> DockerAvailableLazy = new(CheckDockerAvailability);

    public static bool IsDockerRunning => DockerAvailableLazy.Value;

    public const string RabbitMqUsername = "guest";
    public const string RabbitMqPassword = "guest";

    private readonly PostgreSqlContainer? _postgresContainer;
    private readonly RabbitMqContainer? _rabbitMqContainer;
    private readonly RedisContainer? _redisContainer;

    public DockerTestcontainersFixture()
    {
        if (!IsDockerRunning)
        {
            return;
        }

        try
        {
            _postgresContainer = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("eventflow_integration")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();

            _rabbitMqContainer = new RabbitMqBuilder("rabbitmq:3.13-management-alpine")
                .WithUsername(RabbitMqUsername)
                .WithPassword(RabbitMqPassword)
                .Build();

            _redisContainer = new RedisBuilder("redis:7-alpine")
                .Build();
        }
        catch
        {
            // Fallback in case container builder initialization fails
        }
    }

    public string PostgreSqlConnectionString => _postgresContainer?.GetConnectionString()
        ?? "Host=localhost;Database=eventflow_test;Username=postgres;Password=postgres";

    public string RabbitMqHost => _rabbitMqContainer?.Hostname ?? "localhost";

    public int RabbitMqPort => _rabbitMqContainer != null
        ? _rabbitMqContainer.GetMappedPublicPort(5672)
        : 5672;

    public string RedisConnectionString => _redisContainer?.GetConnectionString() ?? "localhost:6379";

    public async Task InitializeAsync()
    {
        if (!IsDockerRunning || _postgresContainer == null || _rabbitMqContainer == null || _redisContainer == null)
        {
            return;
        }

        await Task.WhenAll(
            _postgresContainer.StartAsync(),
            _rabbitMqContainer.StartAsync(),
            _redisContainer.StartAsync());
    }

    public async Task DisposeAsync()
    {
        if (!IsDockerRunning)
        {
            return;
        }

        var stopTasks = new List<Task>();

        if (_postgresContainer != null)
        {
            stopTasks.Add(_postgresContainer.DisposeAsync().AsTask());
        }

        if (_rabbitMqContainer != null)
        {
            stopTasks.Add(_rabbitMqContainer.DisposeAsync().AsTask());
        }

        if (_redisContainer != null)
        {
            stopTasks.Add(_redisContainer.DisposeAsync().AsTask());
        }

        if (stopTasks.Count > 0)
        {
            await Task.WhenAll(stopTasks);
        }
    }

    public void Dispose()
    {
        // No synchronous unmanaged resources to dispose
    }

    private static bool CheckDockerAvailability()
    {
        try
        {
            // Check for standard unix socket
            if (File.Exists("/var/run/docker.sock"))
            {
                return true;
            }

            // Check if DOCKER_HOST is explicitly configured (e.g. CI / remote daemon)
            string? dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
            if (!string.IsNullOrWhiteSpace(dockerHost))
            {
                return true;
            }

            // Check for user-space rootless docker socket
            string userRuntimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? string.Empty;
            if (!string.IsNullOrEmpty(userRuntimeDir) && File.Exists(Path.Combine(userRuntimeDir, "docker.sock")))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
