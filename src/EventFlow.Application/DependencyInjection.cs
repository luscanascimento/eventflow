using EventFlow.Application.Abstractions;
using EventFlow.Application.Common;
using EventFlow.Application.Events.Commands;
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

        return services;
    }
}
