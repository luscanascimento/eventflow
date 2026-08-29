using EventFlow.Application;
using EventFlow.Application.Outbox;
using EventFlow.Infrastructure;
using EventFlow.Worker.BackgroundServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Configure Outbox processor options
builder.Services.Configure<OutboxProcessorOptions>(
    builder.Configuration.GetSection(OutboxProcessorOptions.SectionName));

// Add Application and Infrastructure layers (including PostgreSQL & RabbitMQ Publisher)
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Register hosted background service
builder.Services.AddHostedService<OutboxDispatcherWorker>();

var host = builder.Build();
host.Run();
