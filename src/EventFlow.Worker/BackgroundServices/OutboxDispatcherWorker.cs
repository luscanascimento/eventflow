using EventFlow.Application.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EventFlow.Worker.BackgroundServices;

/// <summary>
/// Background worker that periodically polls pending outbox messages from PostgreSQL
/// and dispatches them via the configured <see cref="OutboxProcessor"/>.
/// </summary>
public sealed partial class OutboxDispatcherWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDispatcherWorker> _logger;
    private readonly OutboxProcessorOptions _options;

    public OutboxDispatcherWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxDispatcherWorker> logger,
        IOptions<OutboxProcessorOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogWorkerStarted(_logger, _options.PollingIntervalMs);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(100, _options.PollingIntervalMs)));

        while (!stoppingToken.IsCancellationRequested)
        {
            int processedCount = 0;

            try
            {
                await using (var scope = _scopeFactory.CreateAsyncScope())
                {
                    var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                    processedCount = await processor.ProcessPendingMessagesAsync(stoppingToken);

                    if (processedCount > 0)
                    {
                        LogProcessedBatch(_logger, processedCount);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal graceful shutdown requested
                break;
            }
            catch (Exception ex)
            {
                LogWorkerIterationError(_logger, ex);
            }

            // If a full batch was processed, immediately poll for remaining pending messages without waiting
            if (processedCount >= _options.BatchSize)
            {
                continue;
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        LogWorkerStopped(_logger);
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "OutboxDispatcherWorker started with polling interval {PollingIntervalMs}ms")]
    private static partial void LogWorkerStarted(ILogger logger, int pollingIntervalMs);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Debug, Message = "OutboxDispatcherWorker processed batch of {Count} messages")]
    private static partial void LogProcessedBatch(ILogger logger, int count);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Error, Message = "Unexpected error during OutboxDispatcherWorker polling iteration")]
    private static partial void LogWorkerIterationError(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Information, Message = "OutboxDispatcherWorker gracefully stopped")]
    private static partial void LogWorkerStopped(ILogger logger);
}
