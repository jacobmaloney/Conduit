using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Conduit.Web.Services;
using Conduit.DataAccess.Repositories;

namespace Conduit.Web.Pages
{
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    public class LoginModel : PageModel
    {
        private readonly LoginService _loginService;
        private readonly OpenAccessState _openAccess;
        private readonly IdentityProviderRepository _providers;

        public LoginModel(LoginService loginService, OpenAccessState openAccess,
            IdentityProviderRepository providers)
        {
            _loginService = loginService;
            _openAccess = openAccess;
            _providers = providers;
        }

        /// <summary>Enabled external IdPs to render as "Sign in with ..." buttons.</summary>
        public List<IdentityProvider> ExternalProviders { get; private set; } = new();

        /// <summary>Set when an SSO attempt was denied/failed (generic — no enumeration).</summary>
        public bool SsoError { get; private set; }

        private async Task LoadExternalProvidersAsync()
        {
            try
            {
                ExternalProviders = await _providers.GetEnabledAsync();
            }
            catch
            {
                // Table may not exist on a fresh/un-migrated DB — SSO is optional.
                ExternalProviders = new List<IdentityProvider>();
            }
        }

        [BindProperty]
        public string Username { get; set; } = "";

        [BindProperty]
        public string Password { get; set; } = "";

        [BindProperty(SupportsGet = true)]
        public string? ReturnUrl { get; set; }

        public string? ErrorMessage { get; set; }

        public async Task<IActionResult> OnGetAsync(int? sso_error = null)
        {
            // When portal open-access is on there's nothing to authenticate to —
            // any portal page the operator visits will sign them in automatically
            // via OpenAccessSignInMiddleware. Showing a login form would just be
            // confusing.
            if (_openAccess.IsEnabled)
            {
                return !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
                    ? LocalRedirect(ReturnUrl)
                    : LocalRedirect("/");
            }
            SsoError = sso_error == 1;
            await LoadExternalProvidersAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var outcome = await _loginService.ValidateAsync(Username, Password, ip);

            if (outcome.LockedOut)
            {
                var seconds = Math.Max(1, (int)outcome.RetryAfter.TotalSeconds);
                ErrorMessage = $"Too many failed attempts. Try again in {seconds} seconds.";
                Password = "";
                Response.Headers["Retry-After"] = seconds.ToString();
                Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await LoadExternalProvidersAsync();
                return Page();
            }
            if (!outcome.Success)
            {
                ErrorMessage = "Invalid username or password.";
                Password = "";
                await LoadExternalProvidersAsync();
                return Page();
            }
            var result = outcome.Result!;

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, result.Id.ToString()),
                new(ClaimTypes.Name, result.UserName),
                new("DisplayName", result.DisplayName),
                new(ClaimTypes.Role, "Admin")
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
                new AuthenticationProperties { IsPersistent = true });

            if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
            {
                return LocalRedirect(ReturnUrl);
            }
            return LocalRedirect("/");
        }
    }
}
