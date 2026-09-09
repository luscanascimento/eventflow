using EventFlow.Application.Abstractions;
using EventFlow.Application.Common;
using EventFlow.Application.Events.Commands;
using EventFlow.Application.Events.Consumers;
using EventFlow.Application.Events.Handlers;
using EventFlow.Application.Outbox;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace EventFlow.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.AddValidatorsFromAssemblyContaining<IngestEventValidator>();
        services.AddScoped<IngestEventUseCase>();
        services.AddScoped<OutboxProcessor>();
        services.AddScoped<IEventProcessor, EventProcessor>();
        services.AddScoped<IEventConsumerHandler, PaymentSettledHandler>();
        services.AddScoped<IEventConsumerHandler, WebhookDeliveryHandler>();

        return services;
    }
}
