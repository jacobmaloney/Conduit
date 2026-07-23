using System.Text;
using Conduit.DataAccess.Repositories;
using Conduit.Sync.Security;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Conduit.Connectors.IdentityCenter.Tests;

/// <summary>
/// Proves the Certification Center API key is stored ENCRYPTED by the SAME
/// CredentialProtector keyring the branded path uses (the persist loop is
/// metadata-driven, so this is the exact code the connection Save runs). A real
/// CredentialProtector with a fixed Sync:CredentialKey performs real AES-GCM; the
/// captured row must be ciphertext, and the plaintext key must never appear in it.
/// </summary>
public class CredentialEncryptionTests
{
    // Fixed 32-byte key (0,1,2,...,31) so the test is deterministic.
    private static string FixedKeyB64()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)i;
        return Convert.ToBase64String(key);
    }

    private static CredentialProtector BuildProtector(InMemoryConnectionCredentialRepository repo)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sync:CredentialKey"] = FixedKeyB64() })
            .Build();
        // The keyring is never consulted — the explicit config key wins in ResolveKeyAsync.
        var keyring = new CredentialKeyringRepository(new DataAccess.DatabaseConfig());
        return new CredentialProtector(config, repo, keyring);
    }

    [Fact]
    public async Task ApiKey_is_persisted_as_ciphertext_never_plaintext()
    {
        const string apiKey = "SUPER-SECRET-CC-KEY-12345";
        var plaintext = System.Text.Json.JsonSerializer.Serialize(
            new { BaseUrl = "https://api.certification-center.com", ApiKey = apiKey });

        var repo = new InMemoryConnectionCredentialRepository();
        var protector = BuildProtector(repo);
        var tenantId = Guid.NewGuid();

        // "identitycenter" — the SAME credential name the branded adapter reuses.
        await protector.StoreAsync(tenantId, "identitycenter", plaintext);

        var row = repo.LastStored;
        Assert.NotNull(row);
        Assert.NotEmpty(row!.Ciphertext);
        Assert.Equal(12, row.Nonce.Length);   // AES-GCM 96-bit nonce
        Assert.Equal(16, row.Tag.Length);      // AES-GCM 128-bit tag

        // The raw key must NOT be recoverable from the stored bytes.
        Assert.False(Contains(row.Ciphertext, Encoding.UTF8.GetBytes(apiKey)),
            "The plaintext API key leaked into the stored ciphertext.");
        Assert.False(Contains(row.Ciphertext, Encoding.UTF8.GetBytes(plaintext)),
            "The plaintext blob leaked into the stored ciphertext.");

        // And it round-trips back to the original under the same key.
        var restored = await protector.RetrieveAsync(tenantId, "identitycenter");
        Assert.Equal(plaintext, restored);
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return true;
        }
        return false;
    }
}
