using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EventFlow.Api.Security;

/// <summary>
/// Endpoint filter enforcing constant-time API Key authentication on protected route groups.
/// </summary>
public sealed class ApiKeyEndpointFilter : IEndpointFilter
{
    public const string HeaderName = "X-Api-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var configuration = httpContext.RequestServices.GetRequiredService<IConfiguration>();
        var validator = httpContext.RequestServices.GetRequiredService<IApiKeyValidator>();

        string? expectedKey = configuration["Security:ApiKey"];

        if (!string.IsNullOrWhiteSpace(expectedKey))
        {
            string? providedKey = httpContext.Request.Headers[HeaderName].FirstOrDefault();

            if (!validator.ValidateApiKey(providedKey, expectedKey))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Unauthorized",
                    detail: $"Invalid or missing API key in the '{HeaderName}' header.",
                    instance: httpContext.Request.Path);
            }
        }

        return await next(context);
    }
}
