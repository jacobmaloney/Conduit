using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Conduit.DataAccess;
using Conduit.DataAccess.Repositories;
using Conduit.Web.Services;

namespace Conduit.Web.Authentication;

/// <summary>
/// Registers one OpenIdConnect authentication scheme per ENABLED row in the
/// IdentityProviders table, at startup. Mirrors IdentityCenter's
/// DynamicAuthenticationService, adapted to Conduit's SmartAuth stack.
///
/// Design constraints honored:
///   - These OIDC schemes are ADDITIONAL named schemes. They are never the default
///     scheme, so the SmartAuth Bearer→JWT / else→Cookie selector and the SCIM/JWT
///     API path are completely unaffected. OIDC is only ever reached by an explicit
///     Challenge (see SsoController) on /login/sso?provider=Name.
///   - The sign-in cookie produced on success is the SAME "scim.admin" cookie
///     principal LoginService issues (NameIdentifier/Name/DisplayName/Role=Admin),
///     so the rest of the app can't tell a local login from an SSO login.
///   - PROVISIONING GATE: a validated OIDC token only becomes a session if the mapped
///     username/email matches an EXISTING ACTIVE PortalAdmin. No auto-create. The gate
///     runs in OnTokenValidated — server side, before any cookie is issued — so a
///     crafted callback cannot bypass it.
///   - Fresh / un-migrated DB (no IdentityProviders table yet) logs a warning and
///     continues with local login only. SSO is optional.
/// </summary>
public static class DynamicAuthenticationRegistrar
{
    public const string SsoCallbackPath = "/signin-oidc";
    public const string SsoSignedOutCallbackPath = "/signout-callback-oidc";

    /// <summary>
    /// Reads enabled providers using a throwaway scope and adds an OIDC scheme for each.
    /// Called from Program.cs while building the AuthenticationBuilder. Must not throw —
    /// a broken/missing provider table cannot be allowed to take down startup.
    /// </summary>
    public static void AddDynamicExternalProviders(
        this AuthenticationBuilder authBuilder,
        ISecretProtector secretProtector,
        IConfiguration configuration,
        ILogger logger)
    {
        List<IdentityProvider> providers;
        try
        {
            var connStr = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(connStr))
            {
                logger.LogInformation("SSO: no connection string configured yet; skipping external providers.");
                return;
            }

            // Direct, dependency-free read so this works before DI is fully built and
            // tolerates the table simply not existing on a fresh install.
            providers = ReadEnabledProviders(connStr, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SSO: could not load external identity providers (table may not exist yet). Local login only.");
            return;
        }

        if (providers.Count == 0)
        {
            logger.LogInformation("SSO: no enabled external identity providers. Local login only.");
            return;
        }

        foreach (var provider in providers)
        {
            try
            {
                RegisterOne(authBuilder, provider, secretProtector, logger);
                logger.LogInformation("SSO: registered external provider '{Name}' (type {Type}).", provider.Name, provider.Type);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SSO: failed to register provider '{Name}'; skipping it.", provider.Name);
            }
        }
    }

    private static List<IdentityProvider> ReadEnabledProviders(string connStr, ILogger logger)
    {
        var repo = new IdentityProviderRepository(new DatabaseConfig { ConnectionString = connStr });
        // Synchronous wait is acceptable here — this runs once during startup composition.
        return repo.GetEnabledAsync().GetAwaiter().GetResult();
    }

    private static void RegisterOne(
        AuthenticationBuilder authBuilder,
        IdentityProvider provider,
        ISecretProtector? secretProtector,
        ILogger logger)
    {
        var cfg = JsonSerializer.Deserialize<IdentityProviderConfig>(provider.Configuration)
                  ?? new IdentityProviderConfig();

        // Decrypt the client secret ONLY here, in memory, at registration time.
        var clientSecret = secretProtector?.Unprotect(cfg.ClientSecret) ?? cfg.ClientSecret;

        var isAzureAd = string.Equals(provider.Type, "AzureAD", StringComparison.OrdinalIgnoreCase);

        // For AzureAD, refuse the multi-tenant authorities. A blank tenant or 'common' /
        // 'organizations' / 'consumers' accepts tokens from ANY Azure tenant, which would let
        // any Microsoft account whose preferred_username/UPN equals an admin's username sign in.
        // Require a specific tenant. Skip registering an insecure scheme rather than weaken auth.
        string? azureTenant = null;
        if (isAzureAd)
        {
            azureTenant = cfg.TenantId?.Trim();
            if (string.IsNullOrWhiteSpace(azureTenant) || IsMultiTenantAuthority(azureTenant))
            {
                throw new InvalidOperationException(
                    $"Provider '{provider.Name}' (AzureAD) must specify a single tenant GUID; " +
                    $"'{(string.IsNullOrWhiteSpace(azureTenant) ? "(blank)" : azureTenant)}' is a multi-tenant authority and is not allowed.");
            }
        }

        var authority = ResolveAuthority(provider, cfg);
        if (string.IsNullOrWhiteSpace(authority))
        {
            throw new InvalidOperationException($"Provider '{provider.Name}' has no resolvable authority.");
        }

        // Each provider gets a distinct callback path so multiple OIDC schemes can
        // coexist without colliding on /signin-oidc.
        var schemeCallback = $"{SsoCallbackPath}/{Slug(provider.Name)}";

        authBuilder.AddOpenIdConnect(provider.Name, displayName: provider.Name, options =>
        {
            options.Authority = authority;
            options.ClientId = cfg.ClientId;
            options.ClientSecret = clientSecret;

            // Authorization-code flow with PKCE. Do NOT request the implicit id_token in
            // the redirect; use the back-channel token endpoint.
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.UsePkce = true;
            options.ResponseMode = "query";

            options.CallbackPath = schemeCallback;
            options.SignedOutCallbackPath = SsoSignedOutCallbackPath;

            // Persist the OIDC handshake (nonce + correlation/state) in dedicated
            // short-lived cookies. SameSite=None is required because the IdP redirects
            // back cross-site; per the spec SameSite=None demands Secure, so we force
            // SecurePolicy=Always — the SSO callback MUST be served over HTTPS in
            // production. These are handshake cookies only; they are independent of and
            // do NOT weaken the "scim.admin" session cookie (Lax, HttpOnly, 8h).
            options.NonceCookie.SameSite = SameSiteMode.None;
            options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
            options.CorrelationCookie.SameSite = SameSiteMode.None;
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;

            // Tokens are validated against the IdP metadata. Nonce + state validation are
            // ON by default — we deliberately do not disable them.
            options.GetClaimsFromUserInfoEndpoint = true;
            options.SaveTokens = false;

            // For AzureAD, pin issuer validation to THIS tenant. We do not rely on the
            // default metadata-derived issuer (which, for a 'common' authority, validates
            // against the templated multi-tenant issuer). azureTenant is guaranteed to be a
            // specific tenant here because RegisterOne rejected the multi-tenant values above.
            if (isAzureAd)
            {
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidIssuer =
                    $"https://login.microsoftonline.com/{azureTenant}/v2.0";
            }

            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.Scope.Add("email");
            foreach (var extra in cfg.Scopes)
            {
                if (!string.IsNullOrWhiteSpace(extra)) options.Scope.Add(extra.Trim());
            }

            // When this scheme signs in, it deposits the principal into the cookie scheme —
            // the same scheme local login uses — but ONLY after the gate below approves.
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

            options.Events = new OpenIdConnectEvents
            {
                // THE GATE. Runs server-side after the IdP token is validated and before
                // any cookie is issued. We replace the OIDC principal with a Conduit admin
                // principal IF AND ONLY IF the mapped identity matches an active PortalAdmin.
                OnTokenValidated = async ctx =>
                {
                    var sp = ctx.HttpContext.RequestServices;
                    var admins = sp.GetRequiredService<PortalAdminRepository>();
                    var audit = sp.GetRequiredService<AuditLogService>();
                    var evtLogger = sp.GetRequiredService<ILogger<OpenIdConnectEvents>>();
                    var ip = ctx.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                    var extracted = ExtractUsername(ctx.Principal, cfg.UsernameClaim);
                    var mappedName = extracted.Value;

                    if (string.IsNullOrWhiteSpace(mappedName))
                    {
                        evtLogger.LogWarning("SSO[{Provider}]: token had no usable username/email claim.", provider.Name);
                        await SafeAudit(audit, evtLogger, "Login.Failed", "(sso:unknown)", ip,
                            details: $"method=SSO provider={provider.Name}; no username/email claim.");
                        ctx.Fail("No usable identity claim.");
                        ctx.Response.Redirect("/login?sso_error=1");
                        ctx.HandleResponse();
                        return;
                    }

                    // When the matched value came from an email-type claim, the IdP MUST have
                    // asserted email_verified=true. An unverified (or absent) email is forgeable
                    // at many IdPs, so we will not bind it to an admin identity. Fail closed.
                    if (extracted.IsEmailClaim && !IsEmailVerified(ctx.Principal))
                    {
                        evtLogger.LogWarning("SSO[{Provider}]: email claim used for match but email_verified was not true; refusing.", provider.Name);
                        await SafeAudit(audit, evtLogger, "Login.Failed", mappedName, ip,
                            details: $"method=SSO provider={provider.Name}; email claim '{extracted.SourceClaimType}' not verified (email_verified missing/false).");
                        ctx.Fail("Email not verified.");
                        ctx.Response.Redirect("/login?sso_error=1");
                        ctx.HandleResponse();
                        return;
                    }

                    PortalAdmin? admin = null;
                    try
                    {
                        admin = await admins.GetByUserNameAsync(mappedName.Trim());
                    }
                    catch (Exception ex)
                    {
                        evtLogger.LogError(ex, "SSO[{Provider}]: admin lookup threw.", provider.Name);
                    }

                    // MATCH-EXISTING-ADMIN-ONLY. No auto-provisioning. Generic deny.
                    if (admin is null || !admin.Active)
                    {
                        evtLogger.LogWarning("SSO[{Provider}]: no active PortalAdmin matched the federated identity.", provider.Name);
                        await SafeAudit(audit, evtLogger, "Login.Failed", mappedName, ip,
                            details: $"method=SSO provider={provider.Name}; no matching active PortalAdmin.");
                        ctx.Fail("No matching admin.");
                        ctx.Response.Redirect("/login?sso_error=1");
                        ctx.HandleResponse();
                        return;
                    }

                    // Rebuild the principal to the EXACT shape LoginService issues, so SSO
                    // sessions are indistinguishable from local ones downstream.
                    var claims = new List<Claim>
                    {
                        new(ClaimTypes.NameIdentifier, admin.Id.ToString()),
                        new(ClaimTypes.Name, admin.UserName),
                        new("DisplayName", admin.DisplayName ?? admin.UserName),
                        new(ClaimTypes.Role, "Admin"),
                        new("AuthMethod", "SSO"),
                    };
                    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                    ctx.Principal = new ClaimsPrincipal(identity);
                    ctx.Properties!.IsPersistent = true;

                    try { await admins.MarkLoggedInAsync(admin.Id); } catch { /* non-fatal */ }
                    await SafeAudit(audit, evtLogger, "Login.Succeeded", admin.UserName, ip,
                        userId: admin.Id.ToString(),
                        details: $"method=SSO provider={provider.Name}");
                    evtLogger.LogInformation("SSO[{Provider}]: admin '{User}' signed in from {Ip}.", provider.Name, admin.UserName, ip);
                },

                OnRemoteFailure = ctx =>
                {
                    var evtLogger = ctx.HttpContext.RequestServices.GetRequiredService<ILogger<OpenIdConnectEvents>>();
                    evtLogger.LogWarning(ctx.Failure, "SSO[{Provider}]: remote failure.", provider.Name);
                    ctx.Response.Redirect("/login?sso_error=1");
                    ctx.HandleResponse();
                    return Task.CompletedTask;
                },

                OnAccessDenied = ctx =>
                {
                    ctx.Response.Redirect("/login?sso_error=1");
                    ctx.HandleResponse();
                    return Task.CompletedTask;
                }
            };
        });
    }

    private static string? ResolveAuthority(IdentityProvider provider, IdentityProviderConfig cfg)
    {
        if (string.Equals(provider.Type, "AzureAD", StringComparison.OrdinalIgnoreCase))
        {
            // RegisterOne has already rejected blank / multi-tenant values, so the tenant
            // here is a specific tenant. No 'common' fallback.
            var tenant = cfg.TenantId!.Trim();
            return $"https://login.microsoftonline.com/{tenant}/v2.0";
        }
        return cfg.Authority?.Trim();
    }

    private static bool IsMultiTenantAuthority(string tenant) =>
        string.Equals(tenant, "common", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tenant, "organizations", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tenant, "consumers", StringComparison.OrdinalIgnoreCase);

    private readonly record struct ExtractedName(string? Value, bool IsEmailClaim, string? SourceClaimType);

    /// <summary>
    /// Resolves the value we match against PortalAdmins.UserName, along with whether that
    /// value came from an email-type claim (the caller enforces email_verified in that case).
    ///
    /// If a specific claim is configured, use it (operator's choice). Otherwise probe in a
    /// security-conscious order: NON-email, IdP-asserted identifiers FIRST
    /// (preferred_username, then upn), and treat email / name only as a last resort.
    /// </summary>
    private static ExtractedName ExtractUsername(ClaimsPrincipal? principal, string? configuredClaim)
    {
        if (principal is null) return new ExtractedName(null, false, null);

        if (!string.IsNullOrWhiteSpace(configuredClaim))
        {
            var v = principal.FindFirst(configuredClaim)?.Value;
            if (!string.IsNullOrWhiteSpace(v))
            {
                return new ExtractedName(v, IsEmailClaimType(configuredClaim), configuredClaim);
            }
        }

        foreach (var claimType in new[]
        {
            "preferred_username",
            ClaimTypes.Upn,
            "upn",
            ClaimTypes.Email,
            "email",
            "emails",
            ClaimTypes.Name,
            "name",
        })
        {
            var v = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(v))
            {
                return new ExtractedName(v, IsEmailClaimType(claimType), claimType);
            }
        }
        return new ExtractedName(null, false, null);
    }

    private static bool IsEmailClaimType(string claimType) =>
        string.Equals(claimType, ClaimTypes.Email, StringComparison.OrdinalIgnoreCase)
        || string.Equals(claimType, "email", StringComparison.OrdinalIgnoreCase)
        || string.Equals(claimType, "emails", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True only if the token carries an email_verified claim equal to true (OIDC emits it
    /// as the JSON boolean true, which surfaces here as the string "true"). Absent or any
    /// other value is treated as NOT verified — fail closed.
    /// </summary>
    private static bool IsEmailVerified(ClaimsPrincipal? principal)
    {
        var v = principal?.FindFirst("email_verified")?.Value;
        return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string Slug(string name)
    {
        var chars = name.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }

    private static async Task SafeAudit(AuditLogService audit, ILogger logger, string action,
        string userName, string ip, string? userId = null, string? details = null)
    {
        try
        {
            await audit.LogAsync(action, resourceType: "PortalAdmin", resourceId: userId,
                userId: userId, userName: userName, ipAddress: ip,
                statusCode: action == "Login.Succeeded" ? 200 : 401, details: details);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SSO audit write failed for {Action}", action);
        }
    }
}
