using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Conduit.DataAccess.Repositories;
using Microsoft.Extensions.Configuration;

namespace Conduit.Sync.Security;

/// <summary>
/// AES-GCM encrypt/decrypt of per-tenant credential strings. The key is read
/// from configuration key Sync:CredentialKey at startup (base64-encoded 32
/// bytes). If unset, a per-process random key is generated and a warning logged
/// — credentials encrypted under that key won't survive restart.
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
        var keyB64 = config["Sync:CredentialKey"];
        if (!string.IsNullOrWhiteSpace(keyB64))
        {
            try
            {
                _key = Convert.FromBase64String(keyB64);
                if (_key.Length != 32)
                {
                    // Wrong length — fall back to random.
                    _key = RandomBytes(32);
                    Console.Error.WriteLine(
                        "WARNING: Sync:CredentialKey is not 32 bytes after base64 decode. " +
                        "Generated a random per-process key. Credentials won't survive restart.");
                }
            }
            catch
            {
                _key = RandomBytes(32);
                Console.Error.WriteLine(
                    "WARNING: Sync:CredentialKey could not be base64-decoded. " +
                    "Generated a random per-process key. Credentials won't survive restart.");
            }
        }
        else
        {
            _key = RandomBytes(32);
            Console.Error.WriteLine(
                "WARNING: Sync:CredentialKey is not configured. " +
                "Generated a random per-process key. Credentials won't survive restart.");
        }
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
