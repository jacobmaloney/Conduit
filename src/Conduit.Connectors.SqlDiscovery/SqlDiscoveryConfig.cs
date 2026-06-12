using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Conduit.Sync.Security;

namespace Conduit.Connectors.SqlDiscovery;

/// <summary>
/// Enumeration mode for the SQL Discovery source. SPN = LDAP query for
/// MSSQLSvc/* service principal names against an existing Active Directory
/// connection; InstanceList = the manually-configured host list; Both = union.
/// </summary>
internal enum SqlDiscoveryMode { Spn, InstanceList, Both }

/// <summary>
/// Decrypted "sqldiscovery" credential blob. All discovery configuration lives
/// in this single per-connection blob (Conduit stores NO object data and no new
/// tables — connector config is credential-shaped by design).
/// </summary>
internal sealed class SqlDiscoveryConfig
{
    public SqlDiscoveryMode Mode { get; init; } = SqlDiscoveryMode.Spn;
    /// <summary>Name of an EXISTING ActiveDirectory Connected System used for SPN enumeration.</summary>
    public string? AdConnectionName { get; init; }
    /// <summary>Manual instance list, one entry per line: host[\instance][,port].</summary>
    public IReadOnlyList<ManualInstanceEntry> InstanceList { get; init; } = Array.Empty<ManualInstanceEntry>();
    /// <summary>"SqlAuth" (v1 default) or "WindowsAuth" (service-account integrated security).</summary>
    public string AuthType { get; init; } = "SqlAuth";
    public string? SqlUsername { get; init; }
    public string? SqlPassword { get; init; }
    /// <summary>
    /// Explicit per-connection opt-in for self-signed/lab estates. Defaults to
    /// FALSE: with SqlAuth Encrypt=Mandatory, an unvalidated certificate would
    /// let a rogue discovered host harvest the scan credential.
    /// </summary>
    public bool TrustServerCertificate { get; init; }
    /// <summary>
    /// Collect server logins (sys.server_principals: name/type/enabled only) on
    /// each successful scan. Default TRUE — existing config blobs without the
    /// key keep working and gain the collection.
    /// </summary>
    public bool CollectLogins { get; init; } = true;
    /// <summary>Bounded scan parallelism. Default 16.</summary>
    public int Parallelism { get; init; } = 16;
    /// <summary>Per-server SQL connect timeout in seconds. Default 8.</summary>
    public int ConnectTimeoutSeconds { get; init; } = 8;
    /// <summary>
    /// IC-side source connection name stamped on instance-list hosts that are not
    /// in AD (auto-seeds a connection IC-side). SPN-enumerated domain hosts are
    /// stamped with the AD connection's name instead so the scan ENRICHES the
    /// already-synced computer object via the (SourceConnectionId, SourceUniqueId)
    /// upsert key rather than creating a duplicate.
    /// </summary>
    public string DiscoverySourceName { get; init; } = "SQLDiscovery";
}

/// <summary>One parsed line of the manual instance list: host[\instance][,port].</summary>
internal sealed class ManualInstanceEntry
{
    public string Host { get; init; } = string.Empty;
    public string? Instance { get; init; }
    public int? Port { get; init; }
}

internal static class SqlDiscoveryConfigReader
{
    public const string CredentialName = "sqldiscovery";

    public static async Task<SqlDiscoveryConfig?> ReadAsync(CredentialProtector protector, Guid tenantId)
    {
        var name = CredentialNameContext.Resolve(CredentialName, CredentialSide.Source);
        var raw = await protector.RetrieveAsync(tenantId, name);
        if (string.IsNullOrEmpty(raw) && !string.Equals(name, CredentialName, StringComparison.OrdinalIgnoreCase))
            raw = await protector.RetrieveAsync(tenantId, CredentialName);
        if (string.IsNullOrEmpty(raw)) return null;

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        string? Str(string key) =>
            root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        bool Bool(string key, bool fallback)
        {
            if (!root.TryGetProperty(key, out var el)) return fallback;
            return el.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(el.GetString(), out var b) ? b : fallback,
                _ => fallback
            };
        }
        int Int(string key, int fallback, int min, int max)
        {
            var s = Str(key);
            if (!int.TryParse(s, out var v)) return fallback;
            return Math.Clamp(v, min, max);
        }

        var mode = (Str("Mode") ?? "SPN").Trim() switch
        {
            var m when m.Equals("InstanceList", StringComparison.OrdinalIgnoreCase) => SqlDiscoveryMode.InstanceList,
            var m when m.Equals("Both", StringComparison.OrdinalIgnoreCase) => SqlDiscoveryMode.Both,
            _ => SqlDiscoveryMode.Spn
        };

        return new SqlDiscoveryConfig
        {
            Mode = mode,
            AdConnectionName = Str("AdConnectionName")?.Trim(),
            InstanceList = ParseInstanceList(Str("InstanceList")),
            AuthType = string.Equals(Str("AuthType"), "WindowsAuth", StringComparison.OrdinalIgnoreCase) ? "WindowsAuth" : "SqlAuth",
            SqlUsername = Str("SqlUsername"),
            SqlPassword = Str("SqlPassword"),
            TrustServerCertificate = Bool("TrustServerCertificate", false),
            CollectLogins = Bool("CollectLogins", true),
            Parallelism = Int("Parallelism", 16, 1, 64),
            ConnectTimeoutSeconds = Int("ConnectTimeoutSeconds", 8, 2, 60),
            DiscoverySourceName = string.IsNullOrWhiteSpace(Str("DiscoverySourceName")) ? "SQLDiscovery" : Str("DiscoverySourceName")!.Trim()
        };
    }

    /// <summary>
    /// Parses the manual list. One entry per line, format host[\instance][,port].
    /// Blank lines and lines starting with '#' are ignored. Invalid port suffixes
    /// are treated as no-port rather than dropping the line.
    /// </summary>
    internal static IReadOnlyList<ManualInstanceEntry> ParseInstanceList(string? raw)
    {
        var list = new List<ManualInstanceEntry>();
        if (string.IsNullOrWhiteSpace(raw)) return list;

        foreach (var line in raw.Split('\n'))
        {
            var entry = line.Trim().TrimEnd('\r');
            if (entry.Length == 0 || entry.StartsWith("#", StringComparison.Ordinal)) continue;

            int? port = null;
            var comma = entry.IndexOf(',');
            if (comma >= 0)
            {
                var portPart = entry[(comma + 1)..].Trim();
                if (int.TryParse(portPart, out var p) && p is > 0 and < 65536) port = p;
                entry = entry[..comma].Trim();
            }

            string? instance = null;
            var slash = entry.IndexOf('\\');
            if (slash >= 0)
            {
                instance = entry[(slash + 1)..].Trim();
                if (instance.Length == 0) instance = null;
                entry = entry[..slash].Trim();
            }

            if (entry.Length == 0) continue;
            list.Add(new ManualInstanceEntry { Host = entry, Instance = instance, Port = port });
        }
        return list;
    }
}
