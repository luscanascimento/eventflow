using System.Globalization;
using System.Threading.RateLimiting;
using EventFlow.Api.Endpoints;
using EventFlow.Api.Middleware;
using EventFlow.Api.Security;
using EventFlow.Application;
using EventFlow.Infrastructure;
using EventFlow.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// 1. Configure Kestrel limits defensively (Max 256 KB payload to mitigate memory exhaustion DoS, strip Server header)
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = 256 * 1024;
});

// 2. Add services to the container
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();

// 3. Security: Options Validation with Fail-Fast Startup in Production
builder.Services.AddOptions<SecurityOptions>()
    .Bind(builder.Configuration.GetSection(SecurityOptions.SectionName))
    .Validate(options =>
    {
        if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(options.ApiKey) && options.ApiKey.Length >= 16;
    }, "Security:ApiKey must be configured and at least 16 characters long in Production environments.")
    .ValidateOnStart();

// 4. Security: Defensive Rate Limiting (Partitioned by Client IP and validated Tenant ID)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            context.HttpContext.Response.Headers.RetryAfter = "60";
        }

        context.HttpContext.Response.ContentType = "application/problem+json";
        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too Many Requests",
            Detail = "Rate limit exceeded. Please throttle your requests and retry later.",
            Instance = context.HttpContext.Request.Path
        };
        await context.HttpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken: token);
    };

    options.AddPolicy("events-rate-limit", httpContext =>
    {
        string ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown_ip";
        string? tenantHeader = httpContext.Request.Headers["X-Tenant-Id"].FirstOrDefault()?.Trim();
        string partitionKey = !string.IsNullOrWhiteSpace(tenantHeader) &&
                              tenantHeader.Length is >= 3 and <= 64 &&
                              tenantHeader.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')
            ? $"{ip}:{tenantHeader}"
            : ip;

        return RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey,
            key => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 10
            });
    });
});

// 5. Security: Constant-Time API Key Validator
builder.Services.AddSingleton<IApiKeyValidator, ApiKeyValidator>();

// 6. Add Application, Infrastructure, and Telemetry layers
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddEventFlowTelemetry(builder.Configuration, "EventFlow.Api");

var app = builder.Build();

// 7. Security Headers & Exception Handling Pipeline (SecurityHeaders outermost so 500 errors also include OWASP headers)
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (!string.IsNullOrWhiteSpace(app.Configuration["ASPNETCORE_HTTPS_PORT"]) ||
    (app.Configuration["ASPNETCORE_URLS"]?.Contains("https://", StringComparison.OrdinalIgnoreCase) == true))
{
    app.UseHttpsRedirection();
}

app.UseRateLimiter();

// 8. Map Endpoints
app.MapHealthEndpoints();
app.MapEventEndpoints();
app.MapDeadLetterEndpoints();

app.Run();

// Required for WebApplicationFactory in integration tests
public partial class Program;
