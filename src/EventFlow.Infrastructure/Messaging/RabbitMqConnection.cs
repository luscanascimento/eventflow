using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace EventFlow.Infrastructure.Messaging;

/// <summary>
/// Resilient connection manager for RabbitMQ that lazily establishes the connection
/// and ensures the primary topic exchange and dead-letter topology are declared.
/// </summary>
public sealed partial class RabbitMqConnection : IRabbitMqConnection
{
    private readonly ConnectionFactory _factory;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqConnection> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private IConnection? _connection;
    private bool _isDisposed;

    public RabbitMqConnection(
        IOptions<RabbitMqOptions> options,
        ILogger<RabbitMqConnection> logger)
    {
        _options = options.Value;
        _logger = logger;

        _factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            VirtualHost = _options.VirtualHost,
            UserName = _options.UserName,
            Password = _options.Password,
            ClientProvidedName = _options.ClientProvidedName,
            RequestedHeartbeat = TimeSpan.FromSeconds(_options.RequestedHeartbeatSeconds),
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };
    }

    public bool IsConnected => _connection is { IsOpen: true };

    public Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken = default)
    {
        return CreateChannelInternalAsync(null, cancellationToken);
    }

    public Task<IChannel> CreateChannelAsync(
        CreateChannelOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CreateChannelInternalAsync(options, cancellationToken);
    }

    private async Task<IChannel> CreateChannelInternalAsync(
        CreateChannelOptions? options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var connection = await GetOrCreateConnectionAsync(cancellationToken);
        var effectiveOptions = options ?? new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);

        var channel = await connection.CreateChannelAsync(effectiveOptions, cancellationToken);

        try
        {
            // Ensure topic exchange is idempotently declared
            await channel.ExchangeDeclareAsync(
                exchange: _options.ExchangeName,
                type: ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                arguments: null,
                cancellationToken: cancellationToken);

            return channel;
        }
        catch
        {
            try
            {
                await channel.DisposeAsync();
            }
            catch
            {
                // Ignore disposal errors during exception unwinding
            }

            throw;
        }
    }

    private async Task<IConnection> GetOrCreateConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            LogConnecting(_logger, _options.HostName, _options.Port);
            var connection = await _factory.CreateConnectionAsync(cancellationToken);

            try
            {
                await EnsureTopologyDeclaredAsync(connection, cancellationToken);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }

            _connection = connection;
            LogConnected(_logger, _options.HostName, _options.Port);

            return _connection;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private async Task EnsureTopologyDeclaredAsync(IConnection connection, CancellationToken cancellationToken)
    {
        await using var setupChannel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await setupChannel.ExchangeDeclareAsync(
            exchange: _options.ExchangeName,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        await setupChannel.ExchangeDeclareAsync(
            exchange: _options.DlxExchangeName,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        await setupChannel.QueueDeclareAsync(
            queue: _options.DlqQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        await setupChannel.QueueBindAsync(
            queue: _options.DlqQueueName,
            exchange: _options.DlxExchangeName,
            routingKey: "#",
            arguments: null,
            cancellationToken: cancellationToken);

        var queueArguments = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = _options.DlxExchangeName
        };

        await setupChannel.QueueDeclareAsync(
            queue: _options.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: queueArguments,
            cancellationToken: cancellationToken);

        await setupChannel.QueueBindAsync(
            queue: _options.QueueName,
            exchange: _options.ExchangeName,
            routingKey: _options.RoutingKeyPattern,
            arguments: null,
            cancellationToken: cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await _connectionLock.WaitAsync();
        try
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            if (_connection != null)
            {
                try
                {
                    await _connection.CloseAsync();
                    await _connection.DisposeAsync();
                }
                catch (Exception ex)
                {
                    LogDisposeError(_logger, ex);
                }

                _connection = null;
            }
        }
        finally
        {
            _connectionLock.Release();
            _connectionLock.Dispose();
        }
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "Connecting to RabbitMQ broker at {Host}:{Port}...")]
    private static partial void LogConnecting(ILogger logger, string host, int port);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Information, Message = "Successfully connected to RabbitMQ broker at {Host}:{Port}")]
    private static partial void LogConnected(ILogger logger, string host, int port);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Warning, Message = "Error occurred while closing RabbitMQ connection")]
    private static partial void LogDisposeError(ILogger logger, Exception ex);
}
