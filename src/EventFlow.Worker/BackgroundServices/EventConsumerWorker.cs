using EventFlow.Application.Events.Consumers;
using EventFlow.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EventFlow.Worker.BackgroundServices;

/// <summary>
/// Hosted background service managing the lifecycle of the asynchronous event consumer listener,
/// dispatching messages to scoped application handlers.
/// </summary>
public sealed partial class EventConsumerWorker : BackgroundService
{
    private readonly IRabbitMqConsumer _consumer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EventConsumerWorker> _logger;

    public EventConsumerWorker(
        IRabbitMqConsumer consumer,
        IServiceScopeFactory scopeFactory,
        ILogger<EventConsumerWorker> logger)
    {
        _consumer = consumer;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogWorkerStarting(_logger);

        try
        {
            await _consumer.StartAsync(async (context, ct) =>
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IEventProcessor>();
                return await processor.ProcessAsync(context, ct);
            }, stoppingToken);

            LogWorkerStarted(_logger);

            // Keep the background worker active until cancellation is signaled
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            LogWorkerStopping(_logger);
        }
        catch (Exception ex)
        {
            LogWorkerFatalError(_logger, ex);
            throw;
        }
        finally
        {
            await _consumer.StopAsync(CancellationToken.None);
            LogWorkerStopped(_logger);
        }
    }

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information, Message = "EventConsumerWorker is starting RabbitMQ consumer listener...")]
    private static partial void LogWorkerStarting(ILogger logger);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Information, Message = "EventConsumerWorker successfully started listening for events")]
    private static partial void LogWorkerStarted(ILogger logger);

    [LoggerMessage(EventId = 8003, Level = LogLevel.Information, Message = "EventConsumerWorker received shutdown signal. Stopping consumer...")]
    private static partial void LogWorkerStopping(ILogger logger);

    [LoggerMessage(EventId = 8004, Level = LogLevel.Information, Message = "EventConsumerWorker gracefully stopped")]
    private static partial void LogWorkerStopped(ILogger logger);

    [LoggerMessage(EventId = 8005, Level = LogLevel.Critical, Message = "Fatal error in EventConsumerWorker execution")]
    private static partial void LogWorkerFatalError(ILogger logger, Exception ex);
}
