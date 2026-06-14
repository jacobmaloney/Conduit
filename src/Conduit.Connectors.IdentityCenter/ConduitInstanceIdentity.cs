using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Conduit.Connectors.IdentityCenter;

/// <summary>
/// This Conduit installation's durable identity as a "job server" to IdentityCenter.
///
/// IC stamps Objects.SourceJobServerId with the Agents.Id this installation resolves
/// to, so every synced object carries the provenance of the box that pushed it. The
/// identity is a GUID generated ONCE on first use and persisted locally, then reused
/// forever -- it must survive restarts and redeploys so the same installation keeps
/// the same Agents row in IC (auto-registered on first ingest).
///
/// Persistence matches Conduit's existing data-dir convention
/// (<see cref="Conduit.Sync.Security.CredentialProtector"/> uses %PROGRAMDATA%\Conduit
/// on Windows, ~/.config/conduit on POSIX) so all of Conduit's local state lives in one
/// place: the file is <c>instance-id.json</c> next to <c>credential.key</c>.
/// </summary>
public static class ConduitInstanceIdentity
{
    private static readonly object _gate = new();
    private static Identity? _cached;

    /// <summary>
    /// The persisted instance GUID, generated-and-saved on first call. Thread-safe;
    /// the file is read/created at most once per process then cached.
    /// </summary>
    public static Guid InstanceId => Current().InstanceId;

    /// <summary>
    /// A friendly name for this installation. Defaults to the machine name; persisted
    /// alongside the id so it is stable even if the host is later renamed.
    /// </summary>
    public static string Name => Current().Name;

    private static Identity Current()
    {
        var local = _cached;
        if (local is not null) return local;

        lock (_gate)
        {
            if (_cached is not null) return _cached;
            _cached = LoadOrCreate();
            return _cached;
        }
    }

    private static Identity LoadOrCreate()
    {
        var path = DefaultPath();

        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Identity>(json);
                if (loaded is not null && loaded.InstanceId != Guid.Empty)
                {
                    if (string.IsNullOrWhiteSpace(loaded.Name))
                        loaded = loaded with { Name = DefaultName() };
                    return loaded;
                }
            }
            catch
            {
                // A corrupt/unreadable file must NOT crash a sync. Fall through and
                // mint a fresh identity, overwriting the bad file. Losing the old id
                // only means IC auto-registers a new Agents row on the next push.
            }
        }

        var created = new Identity(Guid.NewGuid(), DefaultName(), DateTime.UtcNow);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(created));
        }
        catch
        {
            // If we cannot persist (locked dir, read-only volume), still return the
            // freshly-minted identity for THIS process. It just won't be stable across
            // restarts until the write succeeds; correctness of the current sync holds.
        }
        return created;
    }

    private static string DefaultName()
    {
        try { return Environment.MachineName; }
        catch { return "conduit"; }
    }

    private static string DefaultPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programData = Environment.GetEnvironmentVariable("PROGRAMDATA");
            if (string.IsNullOrWhiteSpace(programData))
                programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Conduit", "instance-id.json");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(xdg))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            xdg = Path.Combine(home, ".config");
        }
        return Path.Combine(xdg, "conduit", "instance-id.json");
    }

    private sealed record Identity(Guid InstanceId, string Name, DateTime CreatedAt);
}
