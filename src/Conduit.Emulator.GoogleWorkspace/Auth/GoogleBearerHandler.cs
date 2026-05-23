using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Conduit.Emulator.GoogleWorkspace.Auth;

public static class GoogleBearerDefaults
{
    public const string Scheme = "GoogleBearer";
}

public sealed class GoogleBearerOptions : AuthenticationSchemeOptions { }

public sealed class GoogleBearerHandler : AuthenticationHandler<GoogleBearerOptions>
{
    private readonly ServiceAccountStore _store;

    public GoogleBearerHandler(
        IOptionsMonitor<GoogleBearerOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ServiceAccountStore store)
        : base(options, logger, encoder)
    {
        _store = store;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var auth))
            return AuthenticateResult.NoResult();

        var value = auth.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = value.Substring("Bearer ".Length).Trim();
        if (string.IsNullOrEmpty(token))
            return AuthenticateResult.Fail("Empty bearer token.");

        var issued = await _store.GetAccessTokenAsync(token);
        if (issued is null)
            return AuthenticateResult.Fail("Invalid access token.");

        if (issued.ExpiresAt <= DateTime.UtcNow)
            return AuthenticateResult.Fail("Expired access token.");

        var claims = new List<Claim>
        {
            new("client_email", issued.ClientEmail),
            new("scope", issued.Scopes)
        };
        if (!string.IsNullOrEmpty(issued.Subject))
            claims.Add(new Claim("sub", issued.Subject));

        var identity = new ClaimsIdentity(claims, GoogleBearerDefaults.Scheme, "client_email", ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), GoogleBearerDefaults.Scheme);
        return AuthenticateResult.Success(ticket);
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers["WWW-Authenticate"] = "Bearer realm=\"admin.googleapis.com\"";
        return base.HandleChallengeAsync(properties);
    }
}
