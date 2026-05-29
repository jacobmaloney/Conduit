namespace Conduit.Web.Authentication;

/// <summary>
/// Encrypts/decrypts small secret strings (e.g. an OIDC ClientSecret) for storage
/// at rest inside a larger JSON configuration blob. Implemented with ASP.NET Core
/// Data Protection so the ciphertext is a self-contained, versioned, key-rotatable
/// base64 string that can be embedded directly in the IdentityProviders.Configuration
/// JSON. No plaintext secret is ever persisted to the database or logs.
///
/// Conduit's existing CredentialProtector (AES-GCM) is row-keyed into the
/// ConnectionCredentials table and cannot encrypt an inline JSON field, which is why
/// this purpose-scoped Data Protection wrapper exists alongside it.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts a plaintext secret. Returns base64 ciphertext safe for JSON.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Decrypts ciphertext produced by <see cref="Protect"/>. Returns null if the
    /// input cannot be unprotected (wrong key, tampered, or not actually ciphertext).
    /// </summary>
    string? Unprotect(string? ciphertext);
}
