using EventFlow.Application.Abstractions;
using EventFlow.Infrastructure.Messaging;
using EventFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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

        // 2. RabbitMQ Messaging Configuration
        services.Configure<RabbitMqOptions>(
            configuration.GetSection(RabbitMqOptions.SectionName));

        services.AddSingleton<IRabbitMqConnection, RabbitMqConnection>();
        services.AddScoped<IEventPublisher, RabbitMqEventPublisher>();
        services.AddSingleton<IRabbitMqConsumer, RabbitMqConsumer>();

        return services;
    }
}
