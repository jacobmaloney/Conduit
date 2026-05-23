using Microsoft.Extensions.Primitives;

namespace Conduit.Web.Middleware;

/// <summary>
/// Adds the standard OWASP security-header bundle to every response. The CSP is the
/// only header that needs to vary by route — JSON API responses don't need one (they
/// can't host script), while the Blazor portal needs a CSP that permits its inline
/// bootstrap script and the SignalR / WebSocket connection back to itself.
/// </summary>
public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _env;

    // Blazor Server requires inline script + WebSocket back to self. 'unsafe-inline' here
    // is the framework's published recommendation; tightening to a nonce-based CSP would
    // require pre-rendering hooks we don't have today.
    private const string PortalCsp =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob: https:; " +
        "font-src 'self' data:; " +
        "connect-src 'self' ws: wss:; " +
        "frame-ancestors 'none'; " +
        "form-action 'self'; " +
        "base-uri 'self'";

    // Strict CSP for JSON API responses — no scripts of any kind, no embedding.
    private const string ApiCsp =
        "default-src 'none'; frame-ancestors 'none'";

    public SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment env)
    {
        _next = next;
        _env = env;
    }

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            var path = context.Request.Path.Value ?? string.Empty;
            var isApi = path.StartsWith("/scim/", StringComparison.OrdinalIgnoreCase)
                     || path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                     || path.StartsWith("/sql/", StringComparison.OrdinalIgnoreCase)
                     || path.StartsWith("/ars/", StringComparison.OrdinalIgnoreCase)
                     || path.StartsWith("/health", StringComparison.OrdinalIgnoreCase);

            // Always-on headers
            Set(headers, "X-Content-Type-Options", "nosniff");
            Set(headers, "X-Frame-Options", "DENY");
            Set(headers, "Referrer-Policy", "strict-origin-when-cross-origin");
            Set(headers, "Permissions-Policy", "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()");
            Set(headers, "Cross-Origin-Opener-Policy", "same-origin");
            Set(headers, "Cross-Origin-Resource-Policy", "same-origin");

            // Content-Security-Policy depends on whether we're rendering HTML or returning JSON
            Set(headers, "Content-Security-Policy", isApi ? ApiCsp : PortalCsp);

            // HSTS only when we actually arrived over HTTPS — sending it over HTTP is a
            // protocol violation that some clients reject loudly.
            if (context.Request.IsHttps)
            {
                Set(headers, "Strict-Transport-Security", "max-age=31536000; includeSubDomains");
            }

            // Strip headers that disclose framework / server fingerprinting.
            headers.Remove("X-Powered-By");
            headers.Remove("X-AspNet-Version");
            headers.Remove("X-AspNetMvc-Version");

            return Task.CompletedTask;
        });

        return _next(context);
    }

    private static void Set(IHeaderDictionary headers, string name, StringValues value)
    {
        // Don't override if downstream already set the header — lets controllers opt out.
        if (!headers.ContainsKey(name))
        {
            headers[name] = value;
        }
    }
}
