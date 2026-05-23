using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Conduit.Web.Services;

namespace Conduit.Web.Pages
{
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    public class LoginModel : PageModel
    {
        private readonly LoginService _loginService;
        private readonly OpenAccessState _openAccess;

        public LoginModel(LoginService loginService, OpenAccessState openAccess)
        {
            _loginService = loginService;
            _openAccess = openAccess;
        }

        [BindProperty]
        public string Username { get; set; } = "";

        [BindProperty]
        public string Password { get; set; } = "";

        [BindProperty(SupportsGet = true)]
        public string? ReturnUrl { get; set; }

        public string? ErrorMessage { get; set; }

        public IActionResult OnGet()
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
                return Page();
            }
            if (!outcome.Success)
            {
                ErrorMessage = "Invalid username or password.";
                Password = "";
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
