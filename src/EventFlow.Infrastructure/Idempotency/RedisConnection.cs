using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace EventFlow.Infrastructure.Idempotency;

/// <summary>
/// Resilient Redis connection manager establishing lazy, auto-recovering connections with graceful failure handling.
/// </summary>
public sealed partial class RedisConnection : IRedisConnection
{
    private readonly RedisOptions _options;
    private readonly ILogger<RedisConnection> _logger;
    private readonly object _connectionLock = new();
    private ConnectionMultiplexer? _connection;
    private bool _loggedDisabled;
    private bool _isDisposed;

    public RedisConnection(
        IOptions<RedisOptions> options,
        ILogger<RedisConnection> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public IDatabase? GetDatabase()
    {
        if (_isDisposed)
        {
            return null;
        }

        try
        {
            var connection = GetOrCreateConnection();
            if (connection is { IsConnected: true })
            {
                return connection.GetDatabase();
            }
        }
        catch (Exception ex)
        {
            LogDatabaseAccessFailed(_logger, ex);
        }

        return null;
    }

    public bool IsConnected => !_isDisposed && _connection is { IsConnected: true };

    private ConnectionMultiplexer? GetOrCreateConnection()
    {
        if (_connection != null)
        {
            return _connection;
        }

        lock (_connectionLock)
        {
            if (_isDisposed)
            {
                return null;
            }

            if (_connection != null)
            {
                return _connection;
            }

            if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.ConnectionString))
            {
                if (!_loggedDisabled)
                {
                    LogRedisDisabled(_logger);
                    _loggedDisabled = true;
                }

                return null;
            }

            try
            {
                LogConnectingToRedis(_logger, _options.ConnectionString);

                var configuration = ConfigurationOptions.Parse(_options.ConnectionString);
                configuration.AbortOnConnectFail = false;
                configuration.ConnectTimeout = 5000;
                configuration.SyncTimeout = 5000;

                _connection = ConnectionMultiplexer.Connect(configuration);
                LogConnectedToRedis(_logger);
                return _connection;
            }
            catch (Exception ex)
            {
                LogConnectionFailed(_logger, ex);
                return null;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_connectionLock)
        {
            if (_isDisposed)
            {
                return ValueTask.CompletedTask;
            }

            _isDisposed = true;

            if (_connection != null)
            {
                try
                {
                    _connection.Dispose();
                    LogRedisDisposed(_logger);
                }
                catch (Exception ex)
                {
                    LogDisposeError(_logger, ex);
                }

                _connection = null;
            }
        }

        return ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 9001, Level = LogLevel.Information, Message = "Redis idempotency fast-guard is disabled by configuration.")]
    private static partial void LogRedisDisabled(ILogger logger);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Information, Message = "Attempting connection to Redis at '{ConnectionString}'...")]
    private static partial void LogConnectingToRedis(ILogger logger, string connectionString);

    [LoggerMessage(EventId = 9003, Level = LogLevel.Information, Message = "Successfully connected to Redis instance.")]
    private static partial void LogConnectedToRedis(ILogger logger);

    [LoggerMessage(EventId = 9004, Level = LogLevel.Warning, Message = "Failed to connect to Redis. Idempotency manager will fall back to PostgreSQL.")]
    private static partial void LogConnectionFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 9005, Level = LogLevel.Warning, Message = "Error retrieving Redis database. Falling back to PostgreSQL.")]
    private static partial void LogDatabaseAccessFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 9006, Level = LogLevel.Information, Message = "Redis connection multiplexer disposed.")]
    private static partial void LogRedisDisposed(ILogger logger);

    [LoggerMessage(EventId = 9007, Level = LogLevel.Warning, Message = "Error disposing Redis connection multiplexer.")]
    private static partial void LogDisposeError(ILogger logger, Exception ex);
}
