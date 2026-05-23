using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Conduit.Emulator.GoogleWorkspace.Infrastructure;
using Conduit.Emulator.GoogleWorkspace.Models;

namespace Conduit.Emulator.GoogleWorkspace.Auth;

[ApiController]
[Route("oauth2/v4/token")]
[Route("token")]                   // older SDKs hit /token
public sealed class OAuth2TokenController : ControllerBase
{
    private const string JwtBearerGrant = "urn:ietf:params:oauth:grant-type:jwt-bearer";

    private readonly ServiceAccountValidator _validator;
    private readonly ServiceAccountStore _store;
    private readonly GoogleWorkspaceOptions _options;
    private readonly ILogger<OAuth2TokenController> _logger;

    public OAuth2TokenController(
        ServiceAccountValidator validator,
        ServiceAccountStore store,
        IOptions<GoogleWorkspaceOptions> options,
        ILogger<OAuth2TokenController> logger)
    {
        _validator = validator;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    [HttpPost]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Token([FromForm] TokenRequest request)
    {
        if (!string.Equals(request.Grant_Type, JwtBearerGrant, StringComparison.Ordinal))
            return OAuth2Err("unsupported_grant_type", $"Only '{JwtBearerGrant}' is supported.");

        if (string.IsNullOrWhiteSpace(request.Assertion))
            return OAuth2Err("invalid_request", "Missing 'assertion' parameter.");

        var result = await _validator.ValidateAsync(request.Assertion);
        if (!result.IsValid)
        {
            var (oauthError, http) = MapFailure(result.Failure!.Value);
            _logger.LogWarning("OAuth2 assertion rejected ({Kind}): {Detail}", result.Failure, result.Detail);
            return new ObjectResult(new OAuth2Error { Error = oauthError, ErrorDescription = result.Detail })
            {
                StatusCode = http
            };
        }

        var issued = new IssuedToken(
            Token: IdGenerator.NewAccessToken(),
            ClientEmail: result.Account!.ClientEmail,
            Subject: result.Subject,
            Scopes: string.Join(' ', result.Scopes),
            IssuedAt: DateTime.UtcNow,
            ExpiresAt: DateTime.UtcNow.AddSeconds(_options.AccessTokenTtlSeconds));

        await _store.StoreAccessTokenAsync(issued);

        return Ok(new TokenResponse
        {
            AccessToken = issued.Token,
            ExpiresIn = _options.AccessTokenTtlSeconds,
            TokenType = "Bearer",
            Scope = issued.Scopes
        });
    }

    private static (string, int) MapFailure(AssertionFailureKind kind) => kind switch
    {
        AssertionFailureKind.MalformedJwt => ("invalid_request", 400),
        AssertionFailureKind.UnknownIssuer => ("invalid_client", 401),
        AssertionFailureKind.DisabledAccount => ("invalid_client", 401),
        AssertionFailureKind.BadSignature => ("invalid_grant", 400),
        AssertionFailureKind.ExpiredOrNotYetValid => ("invalid_grant", 400),
        AssertionFailureKind.InvalidAudience => ("invalid_grant", 400),
        AssertionFailureKind.MissingClaim => ("invalid_grant", 400),
        AssertionFailureKind.TooLongLifetime => ("invalid_grant", 400),
        AssertionFailureKind.InvalidScope => ("invalid_scope", 400),
        _ => ("invalid_request", 400)
    };

    private IActionResult OAuth2Err(string error, string description)
        => new ObjectResult(new OAuth2Error { Error = error, ErrorDescription = description })
        {
            StatusCode = error == "invalid_client" ? 401 : 400
        };

    public sealed class TokenRequest
    {
        public string Grant_Type { get; set; } = string.Empty;
        public string Assertion { get; set; } = string.Empty;
    }

    public sealed class TokenResponse
    {
        [Newtonsoft.Json.JsonProperty("access_token")] public string AccessToken { get; set; } = string.Empty;
        [Newtonsoft.Json.JsonProperty("expires_in")] public int ExpiresIn { get; set; }
        [Newtonsoft.Json.JsonProperty("token_type")] public string TokenType { get; set; } = "Bearer";
        [Newtonsoft.Json.JsonProperty("scope")] public string? Scope { get; set; }
    }
}
