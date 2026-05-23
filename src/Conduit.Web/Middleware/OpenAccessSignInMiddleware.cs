using System.Security.Claims;
using Conduit.Web.Services;

namespace Conduit.Web.Middleware;

/// <summary>
/// When <see cref="OpenAccessState.IsEnabled"/> is true, synthesize an admin
/// ClaimsPrincipal for any portal request that isn't already authenticated.
/// Strictly scoped to the Blazor portal — the SCIM, generic REST, SQL emulator,
/// and ARS proxy surfaces are exempt and continue to demand <c>scim_</c> tokens
/// via <see cref="ApiTokenAuthMiddleware"/>.
///
/// Runs after <c>UseAuthentication</c> so a real signed-in cookie is preferred
/// over the synthetic principal; only fires when there's literally no other
/// authenticated identity.
/// </summary>
public class OpenAccessSignInMiddleware
{
    private readonly RequestDelegate _next;

    public OpenAccessSignInMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context, OpenAccessState state)
    {
        if (state.IsEnabled
            && !(context.User?.Identity?.IsAuthenticated ?? false)
            && IsPortalRoute(context.Request.Path.Value))
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, "open-access"),
                new(ClaimTypes.Name, "open-access"),
                new("DisplayName", "Open Access"),
                new(ClaimTypes.Role, "Admin"),
                new("OpenAccess", "true"),
            };
            var identity = new ClaimsIdentity(claims, "OpenAccess");
            context.User = new ClaimsPrincipal(identity);
        }

        return _next(context);
    }

    private static bool IsPortalRoute(string? path)
    {
        if (string.IsNullOrEmpty(path)) return true; // root → portal
        return !(path.StartsWith("/scim/", StringComparison.OrdinalIgnoreCase)
              || path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
              || path.StartsWith("/sql/", StringComparison.OrdinalIgnoreCase)
              || path.StartsWith("/ars/", StringComparison.OrdinalIgnoreCase)
              || path.StartsWith("/health", StringComparison.OrdinalIgnoreCase));
    }
}
