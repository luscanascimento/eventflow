using EventFlow.Api.Endpoints;
using EventFlow.Api.Infrastructure;
using EventFlow.Api.Middleware;
using EventFlow.Application;
using EventFlow.Application.Abstractions;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel limits defensively
builder.WebHost.ConfigureKestrel(options =>
{
    // Restrict request body size to 256 KB to mitigate DoS payload attacks
    options.Limits.MaxRequestBodySize = 256 * 1024;
});

// Add services to the container
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();

// Add Application layer services
builder.Services.AddApplication();

// Register In-Memory Store for Phase 1 (Replaced by PostgreSQL EF Core in Phase 2)
builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();

var app = builder.Build();

// Configure the HTTP request pipeline
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Map Endpoints
app.MapHealthEndpoints();
app.MapEventEndpoints();

app.Run();

// Required for WebApplicationFactory in integration tests
public partial class Program;
