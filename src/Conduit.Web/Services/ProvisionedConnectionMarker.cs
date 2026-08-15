using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace Conduit.Web.Services;

/// <summary>
/// The server + database an authenticated admin explicitly designated, on the machine
/// where they designated it, normalized for comparison. Every part is trimmed and
/// lower-cased so a marker written from <c>Server=LOCALHOST;Database=Conduit18</c> still
/// matches a configured <c>server=localhost; database=conduit18</c>.
///
/// <paramref name="MachineName"/> exists because the server half is frequently
/// HOST-RELATIVE: <c>localhost</c>, <c>.</c>, <c>(local)</c>, and <c>(localdb)\…</c> all
/// name a different physical server depending on where they are read. Without it, an
/// install copied to another box — or a secrets.json restored from a backup — carries a
/// marker that matches on the new host and authorizes an unattended schema build against
/// whatever <c>localhost</c> means THERE. A mismatch falls through to the wizard, which is
/// the correct failure direction.
/// </summary>
public sealed record ProvisionedConnectionTarget(string DataSource, string InitialCatalog, string MachineName)
{
    // Normalization is an INVARIANT of this type, not a convention each construction site
    // must remember. Matches() compares WITHOUT re-normalizing, so a target built through
    // this constructor with stray casing or whitespace would silently fail to match one
    // built through Parse()/TryParseConnectionString() — a mismatch falls through to the
    // wizard, so the failure is safe but baffling. Enforcing it here makes that impossible
    // and makes Matches()'s "both sides are already normalized" true by construction.
    // Idempotent: TryBuildTarget already normalizes before calling this.
    public string DataSource { get; init; } = NormalizeValue(DataSource);
    public string InitialCatalog { get; init; } = NormalizeValue(InitialCatalog);
    public string MachineName { get; init; } = NormalizeValue(MachineName);

    /// <summary>Trim + lowercase. Widening this widens what a marker authorizes.</summary>
    internal static string NormalizeValue(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
}

/// <summary>
/// The <c>ConduitProvisionedConnection</c> section of secrets.json: the record that an
/// authenticated portal admin prepared ONE specific database through
/// <c>/database-settings</c> and expects it to be schema-ready after the restart.
///
/// This exists because "configured + reachable + schema absent" and "true first run,
/// nothing configured" both collapse to <see cref="DatabaseStatus.NotConfigured"/>, and
/// the second of those must keep going to the wizard. The marker is what separates them.
///
/// SECURITY: the marker is pinned to a SPECIFIC DataSource + InitialCatalog, never a
/// boolean "auto-provision" flag. Startup builds a schema only when the CURRENTLY
/// RESOLVED connection string names that exact server and database. A marker for
/// <c>SERVER-A/Conduit18</c> authorizes nothing at all against <c>SERVER-B/Conduit18</c>
/// or <c>SERVER-A/Payroll</c>. Do not relax this to "whatever happens to be configured."
///
/// Written to secrets.json (not appsettings) through <see cref="SecretsFile.Update"/>'s
/// read-merge-rewrite, alongside the connection string it describes.
/// </summary>
public static class ProvisionedConnectionMarker
{
    public const string SectionName = "ConduitProvisionedConnection";

    /// <summary>
    /// This host's identity, normalized. The marker is bound to it at write time and must
    /// match at read time.
    /// </summary>
    public static string CurrentMachineName => Normalize(Environment.MachineName);

    /// <summary>
    /// Parses a connection string into its normalized comparison key, stamped with the
    /// CURRENT machine — this is the "what is live right now" side of the comparison.
    /// False when the string is absent, unparseable, a template placeholder, or missing
    /// either the server or the database name — none of which can authorize anything.
    /// </summary>
    public static bool TryParseConnectionString(string? connectionString, out ProvisionedConnectionTarget target)
    {
        target = Empty;
        if (string.IsNullOrWhiteSpace(connectionString) ||
            SetupService.IsPlaceholderConnectionString(connectionString))
        {
            return false;
        }

        SqlConnectionStringBuilder builder;
        try
        {
            builder = new SqlConnectionStringBuilder(connectionString);
        }
        catch
        {
            return false;
        }

        return TryBuildTarget(builder.DataSource, builder.InitialCatalog, CurrentMachineName, out target);
    }

    /// <summary>
    /// Reads the marker from a secrets.json root. Null when absent or incomplete —
    /// including a marker written before machine binding existed, which has no
    /// <c>MachineName</c> and therefore cannot prove it was issued for this host. Failing
    /// those closed is deliberate: the operator re-runs prepare, which is one click.
    /// </summary>
    public static ProvisionedConnectionTarget? Parse(JsonObject root)
    {
        if (root[SectionName] is not JsonObject section)
        {
            return null;
        }

        return TryBuildTarget(
            section["DataSource"]?.GetValue<string>(),
            section["InitialCatalog"]?.GetValue<string>(),
            section["MachineName"]?.GetValue<string>(),
            out var target)
            ? target
            : null;
    }

    /// <summary>
    /// True only when the server, the database, AND the issuing machine all match.
    /// Case-insensitive and whitespace-trimmed (both sides are already normalized);
    /// nothing else is folded — widening this widens what the marker authorizes.
    /// </summary>
    public static bool Matches(ProvisionedConnectionTarget marker, ProvisionedConnectionTarget configured) =>
        !string.IsNullOrEmpty(marker.DataSource) &&
        !string.IsNullOrEmpty(marker.InitialCatalog) &&
        !string.IsNullOrEmpty(marker.MachineName) &&
        string.Equals(marker.DataSource, configured.DataSource, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(marker.InitialCatalog, configured.InitialCatalog, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(marker.MachineName, configured.MachineName, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when only the machine identity differs — the copied-install case.</summary>
    public static bool MatchesExceptMachine(ProvisionedConnectionTarget marker, ProvisionedConnectionTarget configured) =>
        !string.IsNullOrEmpty(marker.DataSource) &&
        !string.IsNullOrEmpty(marker.InitialCatalog) &&
        string.Equals(marker.DataSource, configured.DataSource, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(marker.InitialCatalog, configured.InitialCatalog, StringComparison.OrdinalIgnoreCase);

    private static readonly ProvisionedConnectionTarget Empty =
        new(string.Empty, string.Empty, string.Empty);

    /// <summary>Reads the marker from the secret store. Null when the file or section is absent.</summary>
    public static ProvisionedConnectionTarget? Read(string? secretsPath = null)
    {
        if (!SecretsFile.Exists(secretsPath))
        {
            return null;
        }

        return Parse(SecretsFile.Read(secretsPath));
    }

    /// <summary>
    /// Records the database an authenticated admin just prepared. Read-merge-rewrite;
    /// every other section of secrets.json is preserved. Throws if the connection string
    /// does not name both a server and a database — the caller must not then persist it.
    /// </summary>
    public static void Write(string connectionString, string? secretsPath = null)
    {
        if (!TryParseConnectionString(connectionString, out var target))
        {
            throw new InvalidOperationException(
                "Cannot record a provisioning marker: the connection string does not name both a server and a database.");
        }

        SecretsFile.Update(root => Apply(root, target), secretsPath);
    }

    /// <summary>
    /// Removes the marker, so it can never outlive the connection string it describes.
    /// Best-effort: a failure here is not worth failing an operation over, since a marker
    /// that matches nothing authorizes nothing.
    /// </summary>
    public static void Remove(string? secretsPath = null)
    {
        try
        {
            if (!SecretsFile.Exists(secretsPath))
            {
                return;
            }

            SecretsFile.Update(root => root.Remove(SectionName), secretsPath);
        }
        catch
        {
            // Intentionally swallowed — see summary.
        }
    }

    /// <summary>Merges the marker into a secrets.json root (other content preserved).</summary>
    public static void Apply(JsonObject root, ProvisionedConnectionTarget target)
    {
        root[SectionName] = new JsonObject
        {
            ["DataSource"] = target.DataSource,
            ["InitialCatalog"] = target.InitialCatalog,
            ["MachineName"] = target.MachineName,
            ["ProvisionedAtUtc"] = DateTime.UtcNow.ToString("O")
        };
    }

    private static bool TryBuildTarget(string? dataSource, string? initialCatalog, string? machineName,
        out ProvisionedConnectionTarget target)
    {
        var server = Normalize(dataSource);
        var database = Normalize(initialCatalog);
        var machine = Normalize(machineName);

        target = new ProvisionedConnectionTarget(server, database, machine);
        return server.Length > 0 && database.Length > 0 && machine.Length > 0;
    }

    private static string Normalize(string? value) => ProvisionedConnectionTarget.NormalizeValue(value);
}
