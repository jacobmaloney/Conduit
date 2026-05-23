using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Conduit.Emulator.GoogleWorkspace.Infrastructure;

namespace Conduit.Emulator.GoogleWorkspace.Auth;

public enum AssertionFailureKind
{
    MalformedJwt,
    UnknownIssuer,
    DisabledAccount,
    BadSignature,
    ExpiredOrNotYetValid,
    InvalidAudience,
    InvalidScope,
    MissingClaim,
    TooLongLifetime
}

public sealed record AssertionValidationResult(
    bool IsValid,
    ServiceAccountRecord? Account,
    string? Subject,
    IReadOnlyList<string> Scopes,
    AssertionFailureKind? Failure,
    string? Detail)
{
    public static AssertionValidationResult Fail(AssertionFailureKind kind, string detail)
        => new(false, null, null, Array.Empty<string>(), kind, detail);

    public static AssertionValidationResult Ok(ServiceAccountRecord acct, string? sub, IReadOnlyList<string> scopes)
        => new(true, acct, sub, scopes, null, null);
}

public sealed class ServiceAccountValidator
{
    // Google's token endpoint aud — emulator accepts either the canonical URL or localhost loopbacks.
    private static readonly string[] AllowedAudiences =
    {
        "https://oauth2.googleapis.com/token",
        "https://accounts.google.com/o/oauth2/token",
        "https://admin.googleapis.com/"
    };

    private readonly ServiceAccountStore _store;
    private readonly GoogleWorkspaceOptions _options;

    public ServiceAccountValidator(ServiceAccountStore store, IOptions<GoogleWorkspaceOptions> options)
    {
        _store = store;
        _options = options.Value;
    }

    public async Task<AssertionValidationResult> ValidateAsync(string assertion)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        JwtSecurityToken jwt;
        try { jwt = handler.ReadJwtToken(assertion); }
        catch { return AssertionValidationResult.Fail(AssertionFailureKind.MalformedJwt, "Assertion is not a well-formed JWT."); }

        if (!string.Equals(jwt.Header.Alg, "RS256", StringComparison.Ordinal))
            return AssertionValidationResult.Fail(AssertionFailureKind.BadSignature, "Only RS256 is accepted for service-account assertions.");

        var iss = jwt.Payload.Iss;
        if (string.IsNullOrWhiteSpace(iss))
            return AssertionValidationResult.Fail(AssertionFailureKind.MissingClaim, "Missing 'iss' claim.");

        var account = await _store.GetByClientEmailAsync(iss);
        if (account is null)
            return AssertionValidationResult.Fail(AssertionFailureKind.UnknownIssuer, $"No service account registered for '{iss}'.");

        if (account.Disabled)
            return AssertionValidationResult.Fail(AssertionFailureKind.DisabledAccount, "Service account is disabled.");

        // Enforce 'kid' match if present
        var kid = jwt.Header.Kid;
        if (!string.IsNullOrEmpty(kid) && !string.Equals(kid, account.PrivateKeyId, StringComparison.Ordinal))
            return AssertionValidationResult.Fail(AssertionFailureKind.BadSignature, "Header 'kid' does not match any registered key for this service account.");

        // Validate signature + iat/exp + aud using Microsoft.IdentityModel
        using var rsa = KeyPairFactory.LoadPublicKey(account.PublicKeyPem);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = account.ClientEmail,
            ValidateAudience = true,
            ValidAudiences = AllowedAudiences,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(_options.MaxAssertionClockSkewSeconds),
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new RsaSecurityKey(rsa.ExportParameters(false)),
            RequireExpirationTime = true,
            RequireSignedTokens = true
        };

        try
        {
            handler.ValidateToken(assertion, parameters, out _);
        }
        catch (SecurityTokenInvalidAudienceException)
        {
            return AssertionValidationResult.Fail(AssertionFailureKind.InvalidAudience, "Audience is not valid for this emulator.");
        }
        catch (SecurityTokenExpiredException)
        {
            return AssertionValidationResult.Fail(AssertionFailureKind.ExpiredOrNotYetValid, "Assertion is expired.");
        }
        catch (SecurityTokenNotYetValidException)
        {
            return AssertionValidationResult.Fail(AssertionFailureKind.ExpiredOrNotYetValid, "Assertion is not yet valid.");
        }
        catch (SecurityTokenInvalidSignatureException)
        {
            return AssertionValidationResult.Fail(AssertionFailureKind.BadSignature, "Signature verification failed.");
        }
        catch (SecurityTokenException ex)
        {
            return AssertionValidationResult.Fail(AssertionFailureKind.BadSignature, ex.Message);
        }

        // Google caps assertion lifetime at 1 hour.
        var iatUnix = jwt.Payload.IssuedAt;
        var expUnix = jwt.Payload.ValidTo;
        if (expUnix != DateTime.MinValue && iatUnix != DateTime.MinValue && (expUnix - iatUnix) > TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1))
            return AssertionValidationResult.Fail(AssertionFailureKind.TooLongLifetime, "Assertion lifetime exceeds 1 hour.");

        // Scope claim is space-delimited in the assertion.
        var scopeClaim = jwt.Payload["scope"] as string ?? string.Empty;
        var requestedScopes = scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToArray();
        if (requestedScopes.Length == 0)
            return AssertionValidationResult.Fail(AssertionFailureKind.InvalidScope, "Missing 'scope' claim.");

        var accountScopes = account.AllowedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var tenantScopes = _options.AllowedScopes.ToHashSet(StringComparer.Ordinal);

        foreach (var s in requestedScopes)
        {
            if (!accountScopes.Contains(s))
                return AssertionValidationResult.Fail(AssertionFailureKind.InvalidScope, $"Scope '{s}' not granted to this service account.");
            if (!tenantScopes.Contains(s))
                return AssertionValidationResult.Fail(AssertionFailureKind.InvalidScope, $"Scope '{s}' not enabled on this tenant.");
        }

        // 'sub' claim (domain-wide delegation impersonation target) is optional.
        var sub = jwt.Payload["sub"] as string;

        return AssertionValidationResult.Ok(account, sub, requestedScopes);
    }
}
