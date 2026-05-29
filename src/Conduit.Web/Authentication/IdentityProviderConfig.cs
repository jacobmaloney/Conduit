using System.Collections.Generic;

namespace Conduit.Web.Authentication;

/// <summary>
/// Strongly-typed view of the IdentityProviders.Configuration JSON blob. Serialized
/// with System.Text.Json. The <see cref="ClientSecret"/> stored in the DB is the
/// encrypted form (dp:: prefixed); it is decrypted only at registration time.
/// </summary>
public sealed class IdentityProviderConfig
{
    public string ClientId { get; set; } = "";

    /// <summary>Encrypted at rest (dp:: ciphertext). Never logged.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>
    /// Generic OIDC authority (issuer). For AzureAD, leave blank and set TenantId —
    /// the registrar derives https://login.microsoftonline.com/{TenantId}/v2.0.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>Azure AD tenant id (or "common"/"organizations"). Used only for Type=AzureAD.</summary>
    public string? TenantId { get; set; }

    /// <summary>Extra scopes beyond openid/profile/email.</summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>
    /// Which inbound claim carries the value we match against PortalAdmins.UserName.
    /// Defaults to standard email/preferred_username probing when null.
    /// </summary>
    public string? UsernameClaim { get; set; }

    /// <summary>
    /// App-local path the IdP redirects back to after login. Must be registered as the
    /// redirect URI in the IdP app registration. Defaults to "/signin-oidc" when blank.
    /// </summary>
    public string? CallbackPath { get; set; }

    /// <summary>
    /// When true, a successful, already-verified SSO sign-in that matches no existing
    /// PortalAdmin auto-creates a new active admin from the federated identity instead of
    /// being rejected. Off by default. Only changes the outcome (create vs reject) for an
    /// identity that has ALREADY cleared every existing gate (tenant pin + email_verified).
    /// Use only with a trusted, tenant-pinned provider — every Conduit admin has full access.
    /// </summary>
    public bool AutoProvision { get; set; }
}
