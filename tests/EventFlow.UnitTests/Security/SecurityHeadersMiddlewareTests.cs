using EventFlow.Api.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EventFlow.UnitTests.Security;

public class SecurityHeadersMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ShouldSetStrictSecurityHeaders()
    {
        var context = new DefaultHttpContext();
        context.Response.Headers["Server"] = "Kestrel";
        context.Response.Headers["X-Powered-By"] = "ASP.NET";

        var middleware = new SecurityHeadersMiddleware(innerContext =>
        {
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        var headers = context.Response.Headers;

        headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        headers["X-Frame-Options"].ToString().Should().Be("DENY");
        headers["Content-Security-Policy"].ToString().Should().Contain("default-src 'none'");
        headers["Strict-Transport-Security"].ToString().Should().Contain("max-age=31536000");
        headers["Referrer-Policy"].ToString().Should().Be("no-referrer");
        headers["Permissions-Policy"].ToString().Should().Contain("camera=()");
        headers["X-XSS-Protection"].ToString().Should().Be("0");

        headers.ContainsKey("Server").Should().BeFalse();
        headers.ContainsKey("X-Powered-By").Should().BeFalse();
    }
}
