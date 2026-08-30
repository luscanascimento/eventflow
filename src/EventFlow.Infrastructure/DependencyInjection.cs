using EventFlow.Application.Abstractions;
using EventFlow.Infrastructure.DeadLetter;
using EventFlow.Infrastructure.Idempotency;
using EventFlow.Infrastructure.Messaging;
using EventFlow.Infrastructure.Persistence;
using EventFlow.Infrastructure.Resilience;
using Microsoft.EntityFrameworkCore;
using Polly;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EventFlow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // 1. PostgreSQL Persistence Configuration
        string connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' was not found in configuration.");

        services.AddDbContext<EventFlowDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null);
                npgsqlOptions.MigrationsAssembly(typeof(EventFlowDbContext).Assembly.FullName);
            });
        });

        services.AddScoped<IEventStore, EfEventStore>();
        services.AddScoped<IOutboxStore, EfOutboxStore>();

        // 2. RabbitMQ Messaging & DLQ Configuration
        services.Configure<RabbitMqOptions>(
            configuration.GetSection(RabbitMqOptions.SectionName));

        services.AddSingleton<IRabbitMqConnection, RabbitMqConnection>();
        services.AddScoped<IEventPublisher, RabbitMqEventPublisher>();
        services.AddSingleton<IRabbitMqConsumer, RabbitMqConsumer>();
        services.AddScoped<IDeadLetterManager, RabbitMqDeadLetterManager>();

        // 3. Redis & Hybrid Idempotency Configuration
        services.Configure<RedisOptions>(
            configuration.GetSection(RedisOptions.SectionName));

        services.AddSingleton<IRedisConnection, RedisConnection>();
        services.AddScoped<IIdempotencyManager, HybridIdempotencyManager>();

        // 4. Resilience & External HTTP Dispatching (Polly v8)
        services.Configure<ResilienceOptions>(
            configuration.GetSection(ResilienceOptions.SectionName));

        services.AddSingleton<ResiliencePipeline<HttpResponseMessage>>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ResilienceOptions>>().Value;
            return ResiliencePipelineFactory.CreateExternalDispatchPipeline(options);
        });

        services.AddHttpClient<IExternalDispatcher, HttpExternalDispatcher>();

        return services;
    }
}
