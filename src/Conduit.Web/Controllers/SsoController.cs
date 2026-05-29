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
    private readonly IAuthenticationSchemeProvider _schemes;
    private readonly ILogger<SsoController> _logger;

    public SsoController(IdentityProviderRepository providers, IAuthenticationSchemeProvider schemes,
        ILogger<SsoController> logger)
    {
        _providers = providers;
        _schemes = schemes;
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

        // OIDC schemes are registered at startup. A provider enabled at runtime has no
        // scheme until the app restarts; challenging it would throw an unhandled
        // InvalidOperationException from the auth middleware (the ChallengeResult runs
        // after this action returns, so a try/catch here can't catch it). Resolve the
        // scheme up front and bail to the generic error path if it isn't registered yet.
        var scheme = await _schemes.GetSchemeAsync(row.Name);
        if (scheme is null)
        {
            _logger.LogInformation(
                "SSO challenge: scheme '{Provider}' is not registered yet — an app restart is required to activate newly added providers.",
                row.Name);
            return Redirect("/login?sso_error=1");
        }

        // Open-redirect guard: only ever bounce back to a local URL.
        var safeReturn = (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            ? returnUrl!
            : "/";

        var props = new AuthenticationProperties { RedirectUri = safeReturn };
        return Challenge(props, row.Name);
    }
}
