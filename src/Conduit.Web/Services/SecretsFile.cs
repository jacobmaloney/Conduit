using System.Text.Json;
using System.Text.Json.Nodes;
using Conduit.Sync.Security;

namespace Conduit.Web.Services;

/// <summary>
/// The machine-local secret store: %PROGRAMDATA%\Conduit\secrets.json on Windows,
/// $XDG_CONFIG_HOME|~/.config/conduit/secrets.json (0600) on POSIX. Plain
/// configuration JSON holding ConnectionStrings, Jwt, Provision (installer stamp),
/// and Enroll. Loaded LAST in Program.cs so it outranks the world-readable
/// appsettings*.json files under Program Files.
///
/// Every write is read-merge-rewrite through <see cref="RestrictedFileWriter"/>
/// (ACL-first) — never a whole-file replacement of content we did not read.
/// </summary>
public static class SecretsFile
{
    public static string DefaultPath => ConduitDataPaths.FilePath("secrets.json");

    public static bool Exists(string? path = null) => File.Exists(path ?? DefaultPath);

    /// <summary>
    /// Parses the current content; empty object when the file does not exist or is
    /// blank. Throws on corrupt JSON — a merge must never silently clobber content
    /// it could not read.
    /// </summary>
    public static JsonObject Read(string? path = null)
    {
        var target = path ?? DefaultPath;
        if (!File.Exists(target))
            return new JsonObject();

        var text = File.ReadAllText(target);
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject();

        return JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidOperationException($"'{target}' is not a JSON object; refusing to overwrite it.");
    }

    /// <summary>Read-merge-rewrite: parse, apply the mutation, rewrite ACL-first.</summary>
    public static void Update(Action<JsonObject> mutate, string? path = null)
    {
        var target = path ?? DefaultPath;
        var root = Read(target);
        mutate(root);

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        RestrictedFileWriter.Write(target, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
