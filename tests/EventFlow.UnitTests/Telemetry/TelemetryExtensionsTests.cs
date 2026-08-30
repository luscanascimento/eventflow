using EventFlow.Infrastructure.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventFlow.UnitTests.Telemetry;

public class TelemetryExtensionsTests
{
    [Fact]
    public void AddEventFlowTelemetry_ShouldRegisterOpenTelemetryServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenTelemetry:ServiceName"] = "EventFlow.TestApp",
                ["OpenTelemetry:ServiceVersion"] = "1.0.0",
                ["OpenTelemetry:EnableConsoleExporter"] = "false"
            })
            .Build();

        var services = new ServiceCollection();

        services.AddEventFlowTelemetry(configuration, "DefaultService");

        var serviceProvider = services.BuildServiceProvider();
        serviceProvider.Should().NotBeNull();
    }
}
