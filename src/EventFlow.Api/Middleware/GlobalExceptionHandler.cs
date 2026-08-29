using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace EventFlow.Api.Middleware;

/// <summary>
/// Global exception handler producing RFC 7807 ProblemDetails without leaking internal stack traces.
/// Uses source-generated LoggerMessage delegates for zero-allocation high-performance structured logging
/// and delegates serialization to <see cref="IProblemDetailsService"/>.
/// </summary>
public sealed partial class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _environment;
    private readonly IProblemDetailsService _problemDetailsService;

    public GlobalExceptionHandler(
        ILogger<GlobalExceptionHandler> logger,
        IHostEnvironment environment,
        IProblemDetailsService problemDetailsService)
    {
        _logger = logger;
        _environment = environment;
        _problemDetailsService = problemDetailsService;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        string traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        LogUnhandledException(_logger, exception, httpContext.Request.Path, traceId);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        return await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected internal server error occurred.",
                Type = "https://datatracker.ietf.org/doc/html/rfc7807#section-6.6.1",
                Instance = httpContext.Request.Path,
                Detail = _environment.IsDevelopment()
                    ? exception.Message
                    : "An unexpected error occurred. Please contact support referencing the trace ID.",
                Extensions =
                {
                    ["traceId"] = traceId
                }
            }
        });
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Error,
        Message = "Unhandled exception occurred while processing request {Path} with TraceId {TraceId}")]
    private static partial void LogUnhandledException(
        ILogger logger,
        Exception exception,
        string path,
        string traceId);
}
