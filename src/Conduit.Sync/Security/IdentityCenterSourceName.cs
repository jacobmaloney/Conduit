using System.Text.RegularExpressions;

namespace Conduit.Sync.Security;

/// <summary>
/// THE single source of truth for the IdentityCenter source-connection name.
///
/// IC's /api/objects/bulk and /api/objects/tombstones validate the per-record
/// Source field against ^[A-Za-z0-9_.\-]{1,100}$ and 400 the whole batch on any
/// violation. IC then auto-seeds (and resolves) a DirectoryConnections row whose
/// <c>Name</c> equals that Source verbatim. Form-driven write-back later looks the
/// credential up by that DirectoryConnections.Name.
///
/// Therefore the value the sink stamps as Source MUST be byte-identical to the key
/// the orchestrator writes into SinkConnectionCredentialMap. Both call
/// <see cref="Sanitize"/> on the SAME input (ctx.SourceTenant.Name) so the
/// write-key == the stamped Source == DirectoryConnections.Name by construction.
/// Do NOT fork this logic — having two copies is exactly the sanitizer/source-of-
/// truth mismatch this method exists to prevent.
/// </summary>
public static class IdentityCenterSourceName
{
    /// <summary>
    /// Collapse runs of IC-disallowed chars to a single '-', trim leading/trailing
    /// '-', cap at 100. Empty/whitespace (or an all-disallowed name) falls back to
    /// "Conduit".
    /// </summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Conduit";
        var cleaned = Regex.Replace(raw, "[^A-Za-z0-9_.\\-]+", "-").Trim('-');
        if (cleaned.Length > 100) cleaned = cleaned.Substring(0, 100).TrimEnd('-');
        return string.IsNullOrEmpty(cleaned) ? "Conduit" : cleaned;
    }
}
