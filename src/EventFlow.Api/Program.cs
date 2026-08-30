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

// 1. Configure Kestrel limits defensively (Max 256 KB payload to mitigate memory exhaustion DoS)
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 256 * 1024;
});

// 2. Add services to the container
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();

// 3. Security: Defensive Rate Limiting (Partitioned by Tenant ID / Client IP)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
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
        string partitionKey = httpContext.Request.Headers["X-Tenant-Id"].FirstOrDefault()
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";

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

// 4. Security: Constant-Time API Key Validator
builder.Services.AddSingleton<IApiKeyValidator, ApiKeyValidator>();

// 5. Add Application, Infrastructure, and Telemetry layers
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddEventFlowTelemetry(builder.Configuration, "EventFlow.Api");

var app = builder.Build();

// 6. Security Headers & Exception Handling Pipeline
app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseRateLimiter();

// 7. Map Endpoints
app.MapHealthEndpoints();
app.MapEventEndpoints();
app.MapDeadLetterEndpoints();

app.Run();

// Required for WebApplicationFactory in integration tests
public partial class Program;
