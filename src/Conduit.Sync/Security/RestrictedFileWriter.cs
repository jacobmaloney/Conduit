using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Conduit.Sync.Security;

/// <summary>
/// ACL-first, ATOMIC writes for files that hold secrets (credential.key,
/// secrets.json).
///
/// ACL-first: restrictive permissions are in force BEFORE any secret byte lands
/// on disk. ProgramData grants BUILTIN\Users read by inheritance, so writing
/// first and tightening after would leave a window in which the secret is
/// readable by every local user. The (empty) temp file is created under the
/// locked-down descriptor — inheritance disabled, exactly owner + SYSTEM +
/// Administrators — the descriptor is re-asserted on the open handle (Win32
/// CREATE_ALWAYS retains a pre-existing file's descriptor), and only then is
/// the content written. POSIX: the temp file is created 0600 before content.
///
/// Atomic: the content is written to a sibling temp file and promoted with
/// File.Replace/File.Move, so a failed rewrite (disk full, crash mid-write)
/// can never truncate or destroy the ONLY copy of an existing secret — on any
/// failure only the temp file is deleted; a pre-existing target is left
/// exactly as it was. After promotion the locked descriptor is re-asserted on
/// the final path (ReplaceFile can carry the replaced file's — possibly lax —
/// DACL onto the new content).
///
/// NOTE: hand-mirrored into the Installer repo at
/// Installer.Core/Services/Conduit/RestrictedFileWriter.cs (the repos share no
/// code). Keep both copies in sync.
/// </summary>
public static class RestrictedFileWriter
{
    public static void Write(string path, string content)
    {
        var targetPreExisted = File.Exists(path);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var security = BuildLockedDownSecurity();

                // Create the TEMP file with the locked-down ACL already in force,
                // THEN write. The temp name is a fresh GUID, so Win32
                // CREATE_ALWAYS can never retain a pre-existing (lax)
                // descriptor here — the descriptor passed at creation is the
                // one in force. The pre-existing-target case is handled by the
                // post-promote SetAccessControl below.
                using (var fs = new FileInfo(tempPath).Create(
                    FileMode.Create, FileSystemRights.WriteData | FileSystemRights.ReadData,
                    FileShare.None, 4096, FileOptions.None, security))
                {
                    var bytes = Encoding.UTF8.GetBytes(content);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }

                if (targetPreExisted)
                    File.Replace(tempPath, path, destinationBackupFileName: null);
                else
                    File.Move(tempPath, path);

                // ReplaceFile propagates the REPLACED file's descriptor — a lax
                // pre-existing secrets file must not keep its old ACL over the
                // new content. (Open handles to the OLD file keep seeing the old
                // bytes; replace-by-rename gives the new content a new file.)
                new FileInfo(path).SetAccessControl(security);
            }
            catch (Exception ex)
            {
                TryDelete(tempPath);
                if (!targetPreExisted)
                    TryDelete(path);
                throw new InvalidOperationException(
                    $"Failed to write '{path}' with restrictive ACLs. " +
                    (targetPreExisted
                        ? "The pre-existing file was left untouched; only the temp file was removed."
                        : "Any partial file was removed to avoid leaving an unprotected secret on disk."),
                    ex);
            }
        }
        else
        {
            try
            {
                // Create the TEMP file 0600 BEFORE writing the secret, so the
                // content is never present under a group/world-readable mode.
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    var bytes = Encoding.UTF8.GetBytes(content);
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }

                File.Move(tempPath, path, overwrite: true); // atomic rename on POSIX
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                TryDelete(tempPath);
                if (!targetPreExisted)
                    TryDelete(path);
                throw new InvalidOperationException(
                    $"Failed to write '{path}' with 0600 permissions. " +
                    (targetPreExisted
                        ? "The pre-existing file was left untouched; only the temp file was removed."
                        : "Any partial file was removed to avoid leaving an unprotected secret on disk."),
                    ex);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity BuildLockedDownSecurity()
    {
        var security = new FileSecurity();

        // Disable inheritance and drop any inherited ACEs — start from a clean slate.
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var owner = WindowsIdentity.GetCurrent().User;
        if (owner is not null)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                owner, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new FileSystemAccessRule(
            system, FileSystemRights.FullControl, AccessControlType.Allow));

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        security.AddAccessRule(new FileSystemAccessRule(
            admins, FileSystemRights.FullControl, AccessControlType.Allow));

        return security;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
