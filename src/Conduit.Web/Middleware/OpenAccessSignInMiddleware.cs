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
///
/// SECURITY (defense in depth): this synthetic-admin path can ONLY activate when
/// the host is running in the Development environment. Even if the
/// <c>Portal.OpenAccess</c> flag is flipped on in a Production deployment, anonymous
/// admin is never granted. The flag alone is not sufficient — both the flag AND
/// IsDevelopment() must hold. A loud warning is logged once at first request when
/// the flag is on but the environment is non-Development, so an operator who flipped
/// it expecting it to work sees why it didn't.
/// </summary>
public class OpenAccessSignInMiddleware
{
    private readonly RequestDelegate _next;
    private int _nonDevWarningLogged;

    public OpenAccessSignInMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context, OpenAccessState state,
        IWebHostEnvironment env, ILogger<OpenAccessSignInMiddleware> logger)
    {
        var devOnly = env.IsDevelopment();

        if (state.IsEnabled && !devOnly
            && System.Threading.Interlocked.Exchange(ref _nonDevWarningLogged, 1) == 0)
        {
            logger.LogWarning(
                "Portal.OpenAccess is ON but the host environment is '{Environment}', not Development. " +
                "Open-access anonymous admin is REFUSED outside Development. Authentication is enforced.",
                env.EnvironmentName);
        }

        if (state.IsEnabled
            && devOnly
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
