using EventFlow.Api.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace EventFlow.UnitTests.Api;

public class GlobalExceptionHandlerTests
{
    private readonly ILogger<GlobalExceptionHandler> _logger = Substitute.For<ILogger<GlobalExceptionHandler>>();
    private readonly IHostEnvironment _environment = Substitute.For<IHostEnvironment>();

    private sealed class FakeProblemDetailsService : IProblemDetailsService
    {
        public ProblemDetailsContext? LastContext { get; private set; }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            LastContext = context;
            return ValueTask.FromResult(true);
        }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            LastContext = context;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task TryHandleAsync_InProduction_ShouldCallProblemDetailsServiceWithoutSensitiveInfo()
    {
        _environment.EnvironmentName.Returns(Environments.Production);
        var fakeProblemDetails = new FakeProblemDetailsService();

        var sut = new GlobalExceptionHandler(_logger, _environment, fakeProblemDetails);
        var context = new DefaultHttpContext { Request = { Path = "/api/v1/events" } };
        var exception = new InvalidOperationException("Sensitive database credentials error");

        var handled = await sut.TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);

        fakeProblemDetails.LastContext.Should().NotBeNull();
        var details = fakeProblemDetails.LastContext!.ProblemDetails;
        details.Status.Should().Be(StatusCodes.Status500InternalServerError);
        details.Title.Should().Be("An unexpected internal server error occurred.");
        details.Detail.Should().Contain("contact support");
        details.Detail.Should().NotContain("Sensitive database credentials");
        details.Extensions.Should().ContainKey("traceId");
    }

    [Fact]
    public async Task TryHandleAsync_InDevelopment_ShouldIncludeExceptionMessageInProblemDetails()
    {
        _environment.EnvironmentName.Returns(Environments.Development);
        var fakeProblemDetails = new FakeProblemDetailsService();

        var sut = new GlobalExceptionHandler(_logger, _environment, fakeProblemDetails);
        var context = new DefaultHttpContext { Request = { Path = "/api/v1/events" } };
        var exception = new InvalidOperationException("Detailed debug error message");

        var handled = await sut.TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeTrue();
        fakeProblemDetails.LastContext.Should().NotBeNull();
        fakeProblemDetails.LastContext!.ProblemDetails.Detail.Should().Be("Detailed debug error message");
    }
}
