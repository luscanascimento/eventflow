using EventFlow.Application;
using EventFlow.Application.Outbox;
using EventFlow.Infrastructure;
using EventFlow.Infrastructure.Telemetry;
using EventFlow.Worker.BackgroundServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Configure Outbox processor options
builder.Services.Configure<OutboxProcessorOptions>(
    builder.Configuration.GetSection(OutboxProcessorOptions.SectionName));

// Add Application, Infrastructure, and Telemetry layers
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddEventFlowTelemetry(builder.Configuration, "EventFlow.Worker");

// Register hosted background services
builder.Services.AddHostedService<OutboxDispatcherWorker>();
builder.Services.AddHostedService<EventConsumerWorker>();

var host = builder.Build();
host.Run();
