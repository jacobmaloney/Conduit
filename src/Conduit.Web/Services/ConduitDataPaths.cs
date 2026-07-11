using System.Runtime.InteropServices;

namespace Conduit.Web.Services;

/// <summary>
/// Conduit's local data directory, matching the existing convention shared by
/// CredentialProtector (credential.key) and ConduitInstanceIdentity (instance-id.json):
/// %PROGRAMDATA%\Conduit on Windows, $XDG_CONFIG_HOME|~/.config/conduit on POSIX —
/// so all of Conduit's local state stays in one place.
/// </summary>
internal static class ConduitDataPaths
{
    public static string FilePath(string fileName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programData = Environment.GetEnvironmentVariable("PROGRAMDATA");
            if (string.IsNullOrWhiteSpace(programData))
                programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Conduit", fileName);
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(xdg))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            xdg = Path.Combine(home, ".config");
        }
        return Path.Combine(xdg, "conduit", fileName);
    }
}
