using Microsoft.AspNetCore.DataProtection;

namespace Conduit.Web.Authentication;

/// <summary>
/// <see cref="ISecretProtector"/> backed by ASP.NET Core Data Protection. The purpose
/// string isolates this protector's keys from any other Data Protection consumer in
/// the app, so an OIDC ClientSecret can only be unprotected by this exact purpose.
/// </summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    // Sentinel prefix so we can tell an already-encrypted value apart from a freshly
    // typed plaintext secret coming off the admin form. Lets the save path be
    // idempotent: re-saving a provider without retyping the secret won't double-encrypt.
    public const string Prefix = "dp::";

    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("Conduit.IdentityProviders.ClientSecret.v1");
    }

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        return Prefix + _protector.Protect(plaintext);
    }

    public string? Unprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        if (!ciphertext.StartsWith(Prefix)) return null;
        try
        {
            return _protector.Unprotect(ciphertext.Substring(Prefix.Length));
        }
        catch
        {
            // Wrong key (e.g. keyring lost), tampered, or not real ciphertext.
            return null;
        }
    }

    /// <summary>True if the value already looks like our ciphertext (don't re-encrypt).</summary>
    public static bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix);
}
