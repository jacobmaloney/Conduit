using System.Text.Json;
using System.Text.Json.Nodes;
using Conduit.Sync.Security;

namespace Conduit.Web.Services;

/// <summary>
/// Self-healing migration of secrets out of the world-readable Program Files
/// appsettings files into the ACL-restricted secrets.json. Runs as the FIRST
/// statement of Program.cs — before configuration is built — so the same boot
/// that migrates also reads the migrated values.
///
/// Moves, per source file (appsettings.json, appsettings.Production.json,
/// resolved against AppContext.BaseDirectory):
///   - ConnectionStrings:DefaultConnection (only when non-blank and not a placeholder)
///   - Jwt:SecretKey
///   - the entire Provision section (installer stamp)
///   - Enroll:Code (Enroll:Url is not a secret and may remain)
///
/// Write order is the safety contract: merge into secrets.json (ACL-first,
/// existing secrets.json values win over stale appsettings copies), VERIFY by
/// re-reading the file, and only then rewrite the source files with the migrated
/// keys removed (all other content preserved). Any write/verify failure leaves
/// the originals untouched — one loud warning, boot continues on the old values.
/// A second boot after a successful migration finds nothing to move and no-ops.
/// Skipped entirely in Development.
/// </summary>
public static class SecretsRelocator
{
    public static void Run() =>
        Run(AppContext.BaseDirectory,
            SecretsFile.DefaultPath,
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            message => Console.Error.WriteLine(message));

    /// <summary>Testable core. Returns true when a migration was performed.</summary>
    public static bool Run(string baseDirectory, string secretsPath, string? environment, Action<string>? warn = null)
    {
        warn ??= _ => { };
        try
        {
            if (string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase))
                return false;

            var sourcePaths = new[] { "appsettings.json", "appsettings.Production.json" }
                .Select(name => Path.Combine(baseDirectory, name))
                .Where(File.Exists);

            var migrated = new JsonObject();
            var cleanedSources = new List<(string Path, JsonObject Root)>();

            foreach (var sourcePath in sourcePaths)
            {
                JsonObject? root;
                try
                {
                    root = JsonNode.Parse(File.ReadAllText(sourcePath)) as JsonObject;
                }
                catch (Exception ex)
                {
                    warn($"WARNING: secrets relocation could not parse '{sourcePath}' ({ex.Message}); leaving it untouched.");
                    continue;
                }
                if (root is null)
                    continue;

                if (ExtractSecrets(root, migrated))
                    cleanedSources.Add((sourcePath, root));
            }

            if (cleanedSources.Count == 0 || migrated.Count == 0)
                return false;

            // Merge into secrets.json — existing secrets.json values always win
            // (secrets.json already outranks appsettings at runtime, so a differing
            // appsettings copy is dead config, not the live value).
            var secrets = SecretsFile.Read(secretsPath);
            foreach (var (section, key) in EnumerateLeaves(migrated))
            {
                if (secrets[section] is not JsonObject target)
                {
                    target = new JsonObject();
                    secrets[section] = target;
                }
                if (target[key] is null)
                    target[key] = migrated[section]![key]!.DeepClone();
            }

            var directory = Path.GetDirectoryName(secretsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            RestrictedFileWriter.Write(secretsPath, secrets.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            // VERIFY by re-reading before any source is touched: every migrated leaf
            // must be present in the file we just wrote.
            var verification = JsonNode.Parse(File.ReadAllText(secretsPath)) as JsonObject
                ?? throw new InvalidOperationException("secrets.json did not read back as a JSON object.");
            foreach (var (section, key) in EnumerateLeaves(migrated))
            {
                var expected = (secrets[section] as JsonObject)?[key];
                var actual = (verification[section] as JsonObject)?[key];
                if (expected is null || actual is null || expected.ToJsonString() != actual.ToJsonString())
                    throw new InvalidOperationException($"verification failed for {section}:{key} after writing secrets.json.");
            }

            // Only now remove the migrated keys from the world-readable sources.
            foreach (var (path, root) in cleanedSources)
            {
                try
                {
                    File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    // secrets.json already holds (and outranks) the value; the leftover
                    // copy is re-attempted on the next boot.
                    warn($"WARNING: secrets were copied to '{secretsPath}' but '{path}' could not be rewritten ({ex.Message}); its copy will be removed on a later boot.");
                }
            }

            warn($"Relocated secrets from the application directory into '{secretsPath}'.");
            return true;
        }
        catch (Exception ex)
        {
            warn($"WARNING: secrets relocation failed ({ex.Message}). The existing configuration files were left untouched; the app will boot on them.");
            return false;
        }
    }

    /// <summary>
    /// Removes secret-bearing keys from <paramref name="root"/> and records them in
    /// <paramref name="migrated"/>. Returns true when anything was extracted.
    /// </summary>
    private static bool ExtractSecrets(JsonObject root, JsonObject migrated)
    {
        var changed = false;

        if (root["ConnectionStrings"] is JsonObject connectionStrings &&
            TryGetNonEmptyString(connectionStrings, "DefaultConnection", out var connection) &&
            !SetupService.IsPlaceholderConnectionString(connection))
        {
            Record(migrated, "ConnectionStrings", "DefaultConnection", connection);
            connectionStrings.Remove("DefaultConnection");
            changed = true;
        }

        if (root["Jwt"] is JsonObject jwt && TryGetNonEmptyString(jwt, "SecretKey", out var jwtSecret))
        {
            Record(migrated, "Jwt", "SecretKey", jwtSecret);
            jwt.Remove("SecretKey");
            changed = true;
        }

        if (root["Provision"] is JsonObject provision && provision.Count > 0)
        {
            foreach (var pair in provision.ToList())
            {
                if (pair.Value is not null)
                    Record(migrated, "Provision", pair.Key, pair.Value.DeepClone());
            }
            root.Remove("Provision");
            changed = true;
        }

        if (root["Enroll"] is JsonObject enroll && TryGetNonEmptyString(enroll, "Code", out var enrollCode))
        {
            Record(migrated, "Enroll", "Code", enrollCode);
            enroll.Remove("Code");
            changed = true;
        }

        return changed;
    }

    private static bool TryGetNonEmptyString(JsonObject section, string key, out string value)
    {
        value = string.Empty;
        if (section[key] is JsonValue node && node.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
        {
            value = text;
            return true;
        }
        return false;
    }

    private static void Record(JsonObject migrated, string section, string key, JsonNode value)
    {
        if (migrated[section] is not JsonObject target)
        {
            target = new JsonObject();
            migrated[section] = target;
        }
        // Sources are processed in configuration order (base, then Production) and
        // the LAST one wins — matching the runtime precedence in which Production
        // overrides base, so the migrated value is the one that was actually live.
        target[key] = value;
    }

    private static IEnumerable<(string Section, string Key)> EnumerateLeaves(JsonObject twoLevel)
    {
        foreach (var section in twoLevel)
        {
            if (section.Value is not JsonObject inner)
                continue;
            foreach (var leaf in inner)
                yield return (section.Key, leaf.Key);
        }
    }
}
