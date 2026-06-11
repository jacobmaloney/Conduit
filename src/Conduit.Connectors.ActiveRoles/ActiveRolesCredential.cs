using System.Text.Json.Serialization;

namespace Conduit.Connectors.ActiveRoles;

/// <summary>
/// JSON credential DTO persisted (AES-GCM) in ConnectionCredentials under the
/// "ars" credential name. Mirrors the field set declared by
/// <see cref="ActiveRolesAdapter.CredentialTypes"/>.
///
/// Phase 1 uses ONLY <see cref="ArsServiceHost"/>, <see cref="BindUser"/> and
/// <see cref="BindPassword"/> — the EDMS:// bind routes every read/write through
/// the AR Administration Service so policies / workflows / virtual attributes
/// fire. The remaining fields are declared-but-unused, reserved for the Phase 2
/// fast direct-read path (raw AD LDAP + ARS SQL CVSAValues for virtual attrs).
/// </summary>
public sealed class ActiveRolesCredential
{
    // ─── Phase 1: through-ARS path (EDMS:// via the AR ADSI provider) ────────

    /// <summary>
    /// The Active Roles Administration Service host the EDMS:// provider binds
    /// through (e.g. "ars01.domain.local"). Used to construct the EDMS:// path
    /// when the connector is not relying on the provider's default service.
    /// </summary>
    [JsonPropertyName("arsServiceHost")]
    public string? ArsServiceHost { get; set; }

    /// <summary>AR-side bind account, DOMAIN\\user or UPN.</summary>
    [JsonPropertyName("bindUser")]
    public string? BindUser { get; set; }

    /// <summary>AR-side bind password (secret).</summary>
    [JsonPropertyName("bindPassword")]
    public string? BindPassword { get; set; }

    // ─── Phase 2: fast direct-read path (declared but UNUSED in Phase 1) ─────

    /// <summary>Phase 2: a DC to read raw LDAP from for the fast source path.</summary>
    [JsonPropertyName("adHost")]
    public string? AdHost { get; set; }

    /// <summary>Phase 2: read-only conn string to the ARS config DB (CVSAValues).</summary>
    [JsonPropertyName("arsSqlConnString")]
    public string? ArsSqlConnString { get; set; }

    /// <summary>Phase 2: "fast" (direct LDAP+SQL) | "policy" (through the AR service).</summary>
    [JsonPropertyName("readMode")]
    public string? ReadMode { get; set; }
}
