using EventFlow.Application;
using EventFlow.Application.Abstractions;
using EventFlow.Application.Outbox;
using EventFlow.Infrastructure;
using EventFlow.Worker.BackgroundServices;
using EventFlow.Worker.Publishers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Configure Outbox options
builder.Services.Configure<OutboxProcessorOptions>(
    builder.Configuration.GetSection(OutboxProcessorOptions.SectionName));

// Add Application and Infrastructure layers
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Register event publisher (Phase 3 logging publisher; replaced by RabbitMQ publisher in Phase 4)
builder.Services.AddScoped<IEventPublisher, LoggingEventPublisher>();

// Register hosted background service
builder.Services.AddHostedService<OutboxDispatcherWorker>();

var host = builder.Build();
host.Run();
