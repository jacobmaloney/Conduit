using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using Conduit.DataAccess.Repositories;
using Microsoft.Extensions.Configuration;

namespace Conduit.Sync.Security;

/// <summary>
/// AES-GCM encrypt/decrypt of per-tenant credential strings.
///
/// Key resolution precedence (see <see cref="ResolveKey"/>):
///   1. Explicit config key <c>Sync:CredentialKey</c> (base64, 32 bytes) — dev / override.
///   2. A key PERSISTED to a stable machine location, generated on first run if absent.
///      Default location: <c>%PROGRAMDATA%\Conduit\credential.key</c> (Windows) or
///      <c>$XDG_CONFIG_HOME|~/.config/conduit/credential.key</c> equivalent on non-Windows.
///      The path can be overridden with <c>Sync:CredentialKeyPath</c>.
///
/// This replaces the previous behaviour of generating a RANDOM per-process key when
/// unset — which silently orphaned credentials across restarts and made Conduit
/// unusable as a standalone installable product.
///
/// Security posture (Worf):
///   * The persisted key file is written with restrictive ACLs (owner / SYSTEM /
///     Administrators only on Windows; 0600 on POSIX). It is never world-readable.
///   * The key is NEVER logged. Only the resolved key SOURCE and PATH are logged.
///   * If the persisted file exists but is malformed (bad base64 or wrong length),
///     resolution FAILS LOUDLY with an exception rather than silently regenerating —
///     regenerating would orphan every existing encrypted credential.
///
/// Portable across Win/Linux/containers per the symmetric-router decision.
/// </summary>
public sealed class CredentialProtector
{
    private readonly byte[] _key;
    private readonly ConnectionCredentialRepository _repo;

    public CredentialProtector(IConfiguration config, ConnectionCredentialRepository repo)
    {
        _repo = repo;
        _key = ResolveKey(config);
    }

    /// <summary>
    /// Resolves the 32-byte AES key per the documented precedence. Throws on a
    /// malformed explicit key or a malformed persisted key file (never silently
    /// regenerates over existing ciphertext).
    /// </summary>
    private static byte[] ResolveKey(IConfiguration config)
    {
        // (1) Explicit config override.
        var keyB64 = config["Sync:CredentialKey"];
        if (!string.IsNullOrWhiteSpace(keyB64))
        {
            byte[] explicitKey;
            try
            {
                explicitKey = Convert.FromBase64String(keyB64.Trim());
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    "Sync:CredentialKey is set but could not be base64-decoded. " +
                    "Provide a valid base64-encoded 32-byte key or unset it to use the persisted machine key.",
                    ex);
            }

            if (explicitKey.Length != 32)
            {
                throw new InvalidOperationException(
                    $"Sync:CredentialKey decodes to {explicitKey.Length} bytes; a 32-byte key is required.");
            }

            Console.Out.WriteLine("[CredentialProtector] Using explicit Sync:CredentialKey from configuration.");
            return explicitKey;
        }

        // (2) Persisted machine key (generate on first run).
        var keyPath = config["Sync:CredentialKeyPath"];
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            keyPath = DefaultKeyPath();
        }

        return LoadOrCreatePersistedKey(keyPath);
    }

    private static string DefaultKeyPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programData = Environment.GetEnvironmentVariable("PROGRAMDATA");
            if (string.IsNullOrWhiteSpace(programData))
            {
                programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            }
            return Path.Combine(programData, "Conduit", "credential.key");
        }

        // POSIX: prefer XDG_CONFIG_HOME, else ~/.config.
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(xdg))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            xdg = Path.Combine(home, ".config");
        }
        return Path.Combine(xdg, "conduit", "credential.key");
    }

    private static byte[] LoadOrCreatePersistedKey(string keyPath)
    {
        if (File.Exists(keyPath))
        {
            string contents;
            try
            {
                contents = File.ReadAllText(keyPath).Trim();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"The persisted credential key file at '{keyPath}' exists but could not be read. " +
                    "Refusing to start rather than regenerate and orphan existing credentials.",
                    ex);
            }

            byte[] persisted;
            try
            {
                persisted = Convert.FromBase64String(contents);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"The persisted credential key file at '{keyPath}' is malformed (not valid base64). " +
                    "Refusing to start rather than regenerate and orphan existing credentials. " +
                    "Restore the correct key file or remove existing ConnectionCredentials before regenerating.",
                    ex);
            }

            if (persisted.Length != 32)
            {
                throw new InvalidOperationException(
                    $"The persisted credential key file at '{keyPath}' decodes to {persisted.Length} bytes; " +
                    "a 32-byte key is required. Refusing to start rather than regenerate and orphan existing credentials.");
            }

            Console.Out.WriteLine($"[CredentialProtector] Loaded persisted credential key from '{keyPath}'.");
            return persisted;
        }

        // First run: generate a fresh CSPRNG key, persist it with restrictive ACLs.
        var key = RandomBytes(32);
        var dir = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        WriteKeyFileRestricted(keyPath, Convert.ToBase64String(key));
        Console.Out.WriteLine(
            $"[CredentialProtector] No credential key found; generated a new 32-byte key and persisted it to '{keyPath}'.");
        return key;
    }

    /// <summary>
    /// Writes the base64 key to disk and locks it down so only the owner, SYSTEM, and
    /// the local Administrators group can read it (Windows) or 0600 (POSIX). The key is
    /// never logged.
    ///
    /// IMPORTANT: the restrictive permissions are applied BEFORE any key bytes are
    /// written. ProgramData grants BUILTIN\Users read by inheritance, so writing first
    /// and tightening after would leave a brief window in which the secret is readable
    /// by every local user. We create the (empty) file under the locked-down descriptor,
    /// then write into it.
    /// </summary>
    private static void WriteKeyFileRestricted(string keyPath, string keyB64)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var security = new FileSecurity();

                // Disable inheritance and drop any inherited ACEs — start from a clean slate.
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

                var owner = WindowsIdentity.GetCurrent().User;
                if (owner is not null)
                {
                    security.AddAccessRule(new FileSystemAccessRule(
                        owner, FileSystemRights.FullControl, AccessControlType.Allow));
                }

                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                security.AddAccessRule(new FileSystemAccessRule(
                    system, FileSystemRights.FullControl, AccessControlType.Allow));

                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                security.AddAccessRule(new FileSystemAccessRule(
                    admins, FileSystemRights.FullControl, AccessControlType.Allow));

                // Create the file with the locked-down ACL already in force, THEN write the key.
                using (var fs = new FileInfo(keyPath).Create(
                    FileMode.Create, FileSystemRights.WriteData | FileSystemRights.ReadData,
                    FileShare.None, 4096, FileOptions.None, security))
                {
                    var bytes = Encoding.UTF8.GetBytes(keyB64);
                    fs.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception ex)
            {
                // If we cannot lock down the file, do not leave a readable secret on disk.
                TryDelete(keyPath);
                throw new InvalidOperationException(
                    $"Failed to create the credential key file with restrictive ACLs at '{keyPath}'. " +
                    "Any partial file was removed to avoid leaving an unprotected secret on disk.",
                    ex);
            }
        }
        else
        {
            try
            {
                // Create the empty file 0600 BEFORE writing the secret, so the key bytes
                // are never present under a group/world-readable mode.
                using (var fs = new FileStream(keyPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    var bytes = Encoding.UTF8.GetBytes(keyB64);
                    fs.Write(bytes, 0, bytes.Length);
                }
            }
            catch (Exception ex)
            {
                TryDelete(keyPath);
                throw new InvalidOperationException(
                    $"Failed to apply 0600 permissions to the credential key file at '{keyPath}'. " +
                    "The file was removed to avoid leaving an unprotected secret on disk.",
                    ex);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static byte[] RandomBytes(int n)
    {
        var buf = new byte[n];
        RandomNumberGenerator.Fill(buf);
        return buf;
    }

    public async Task StoreAsync(Guid tenantId, string credentialName, string plaintext)
    {
        var ptBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomBytes(12);
        var ciphertext = new byte[ptBytes.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_key, 16))
        {
            aes.Encrypt(nonce, ptBytes, ciphertext, tag);
        }

        await _repo.UpsertAsync(new ConnectionCredentialRepository.CredentialRow
        {
            TenantId = tenantId,
            CredentialName = credentialName,
            Ciphertext = ciphertext,
            Nonce = nonce,
            Tag = tag
        });
    }

    public async Task<string?> RetrieveAsync(Guid tenantId, string credentialName)
    {
        var row = await _repo.GetAsync(tenantId, credentialName);
        if (row is null) return null;

        var plaintext = new byte[row.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(row.Nonce, row.Ciphertext, row.Tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            // Wrong key or tampered data. Caller decides what to do.
            return null;
        }
    }

    public Task DeleteAsync(Guid tenantId, string credentialName) =>
        _repo.DeleteAsync(tenantId, credentialName);
}
