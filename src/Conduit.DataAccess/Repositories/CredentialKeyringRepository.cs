using System.Threading.Tasks;

namespace Conduit.DataAccess.Repositories;

/// <summary>
/// Reads the single active AES credential key from the shared Conduit DB so that
/// every server connecting to that DB can auto-decrypt stored credentials with
/// zero per-box setup (turnkey).
///
/// ACCEPTED RISK (per Jacob's decision — option b): the key material is intentionally
/// co-located with the ciphertext in the same database. This is a deliberate tradeoff
/// for turnkey deployment. Consequently:
///   * This repository must NEVER be exposed to a controller, export, or admin endpoint.
///   * KeyMaterial must NEVER be logged.
///   * There is intentionally NO method that bulk-dumps the table.
/// </summary>
public class CredentialKeyringRepository : BaseRepository
{
    public CredentialKeyringRepository(DatabaseConfig config) : base(config) { }

    /// <summary>
    /// Returns the base64-encoded material of the active key, or null if none is seeded.
    /// </summary>
    public Task<string?> GetActiveKeyMaterialAsync() =>
        QuerySingleOrDefaultAsync<string?>(@"
            SELECT TOP 1 KeyMaterial
              FROM CredentialKeyring
             WHERE IsActive = 1
             ORDER BY CreatedAt DESC");

    /// <summary>
    /// True if an active key is present in the DB keyring.
    /// </summary>
    public async Task<bool> HasActiveKeyAsync()
    {
        var count = await ExecuteScalarAsync<int>(@"
            SELECT COUNT(1)
              FROM CredentialKeyring
             WHERE IsActive = 1");
        return count > 0;
    }
}
