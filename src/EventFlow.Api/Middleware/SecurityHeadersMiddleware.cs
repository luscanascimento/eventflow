using Microsoft.AspNetCore.Http;

namespace EventFlow.Api.Middleware;

/// <summary>
/// Middleware enforcing strict OWASP security response headers across all HTTP requests.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        // 1. Prevent MIME-sniffing
        headers["X-Content-Type-Options"] = "nosniff";

        // 2. Prevent Clickjacking / Framing
        headers["X-Frame-Options"] = "DENY";

        // 3. Strict Content Security Policy
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none';";

        // 4. Force HTTPS (HSTS)
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains; preload";

        // 5. Referrer Policy
        headers["Referrer-Policy"] = "no-referrer";

        // 6. Disable unnecessary browser features/APIs
        headers["Permissions-Policy"] = "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";

        // 7. Modern XSS Protection Header (0 disables buggy legacy filters)
        headers["X-XSS-Protection"] = "0";

        // 8. Strip server identification headers
        headers.Remove("Server");
        headers.Remove("X-Powered-By");

        await _next(context);
    }
}
