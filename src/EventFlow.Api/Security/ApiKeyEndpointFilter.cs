using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EventFlow.Api.Security;

/// <summary>
/// Endpoint filter enforcing constant-time API Key authentication on protected route groups.
/// Implements fail-closed behavior: requests are rejected if the key is missing or server misconfigured.
/// </summary>
public sealed class ApiKeyEndpointFilter : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var options = httpContext.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value;
        var validator = httpContext.RequestServices.GetRequiredService<IApiKeyValidator>();

        // Fail-closed: if the expected API key is not configured, reject requests
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Authentication Misconfiguration",
                detail: "API key authentication is active but server key is not configured.",
                instance: httpContext.Request.Path);
        }

        string? providedKey = httpContext.Request.Headers[HeaderName].FirstOrDefault();

        if (string.IsNullOrWhiteSpace(providedKey) || !validator.ValidateApiKey(providedKey, options.ApiKey))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: $"Invalid or missing API key in the '{HeaderName}' header.",
                instance: httpContext.Request.Path);
        }

        return await next(context);
    }
}
