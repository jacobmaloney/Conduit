using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Conduit.DataAccess.Repositories;

namespace Conduit.Web.Controllers;

/// <summary>
/// Issues the OpenIdConnect challenge for an external provider. Kept as an MVC
/// controller (not Blazor) so it has a real HttpContext to challenge from, exactly
/// like the local Login Razor Page has one to SignInAsync from.
///
/// GET /login/sso?provider={Name}&returnUrl={local}
///   - Validates that {provider} is an ENABLED provider whose OIDC scheme is
///     registered, then challenges it. The challenge's RedirectUri is a LOCAL url
///     only (open-redirect guard). On success the OnTokenValidated gate in the
///     registrar decides whether a session is issued.
/// </summary>
[AllowAnonymous]
[Route("login/sso")]
[EnableRateLimiting("auth")]
public class SsoController : Controller
{
    private readonly IdentityProviderRepository _providers;
    private readonly ILogger<SsoController> _logger;

    public SsoController(IdentityProviderRepository providers, ILogger<SsoController> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Challenge(string provider, string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return Redirect("/login");
        }

        // Confirm the provider is enabled. A scheme that exists but is disabled, or a
        // name that isn't a registered scheme, must not be challengeable.
        IdentityProvider? row = null;
        try
        {
            row = await _providers.GetByNameAsync(provider);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSO challenge: provider lookup failed for '{Provider}'.", provider);
        }

        if (row is null || !row.IsEnabled)
        {
            _logger.LogWarning("SSO challenge: '{Provider}' is not an enabled provider.", provider);
            return Redirect("/login?sso_error=1");
        }

        // Open-redirect guard: only ever bounce back to a local URL.
        var safeReturn = (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            ? returnUrl!
            : "/";

        var props = new AuthenticationProperties { RedirectUri = safeReturn };
        // Challenge by the provider's scheme name (== row.Name). If the scheme isn't
        // registered (e.g. enabled after the last restart), the auth middleware throws;
        // catch it and route the user to a generic error rather than a 500.
        try
        {
            return Challenge(props, row.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSO challenge: scheme '{Provider}' not registered (restart required after enabling?).", row.Name);
            return Redirect("/login?sso_error=1");
        }
    }
}
