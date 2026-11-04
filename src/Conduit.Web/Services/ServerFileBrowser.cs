namespace Conduit.Web.Services;

/// <summary>One row of a <see cref="ServerFolderListing"/>: a drive, a folder or a file on the Conduit host.</summary>
/// <param name="Name">What the row shows (a drive's root, a folder's or a file's name).</param>
/// <param name="FullPath">The absolute path; for a file this is what the field receives.</param>
/// <param name="IsFolder">True for drives and folders (opening descends), false for files (picking fills the field).</param>
/// <param name="Size">A file's size in bytes; null for folders or when it could not be read.</param>
/// <param name="ModifiedUtc">Last write time (UTC); null for drives or when it could not be read.</param>
/// <param name="NoAccess">True when the host account could not read this entry's details.</param>
public sealed record ServerFileEntry(string Name, string FullPath, bool IsFolder, long? Size, DateTime? ModifiedUtc, bool NoAccess);

/// <summary>What one folder holds, as <see cref="ServerFileBrowser.List"/> answers it.</summary>
/// <param name="Path">The folder listed; null for the drive list.</param>
/// <param name="Parent">The folder one level up; null at a drive's root (Up goes to the drive list) and for the drive list.</param>
/// <param name="Entries">Folders first, then the files that match, each sorted by name.</param>
/// <param name="Capped">True when the folder held more entries than were returned.</param>
/// <param name="Error">Why nothing could be listed (a refused path, a missing folder, no access); null on success.</param>
public sealed record ServerFolderListing(string? Path, string? Parent, IReadOnlyList<ServerFileEntry> Entries, bool Capped, string? Error);

/// <summary>
/// CSV-FILE-PICKER-01 (Jacob, 2026-11-04: "needed a way to browse the file system and choose the csv file").
/// Lists the Conduit host's own disk for the Browse button beside a connector field that names a file the
/// job server reads (CredentialFieldSpec.BrowseServerFileExtensions). It returns names, paths, sizes and
/// times, never file contents. Only absolute local paths are listed: relative paths, UNC shares, device
/// paths (\\?\, \\.\) and any "." or ".." segment are refused before the disk is touched, so a typed path
/// cannot walk anywhere the drive list does not lead. The caller (the connections page, signed in as Admin)
/// owns authorization; this class owns what may be listed.
/// </summary>
public sealed class ServerFileBrowser
{
    /// <summary>The most entries one folder returns; the rest are reported through <see cref="ServerFolderListing.Capped"/>.</summary>
    public const int MaxEntries = 500;

    private static readonly EnumerationOptions Listing = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.System,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// What <paramref name="path"/> holds: the host's ready fixed drives when it is empty, otherwise its
    /// folders and the files whose extension is in <paramref name="extensions"/> (case-insensitive; empty = every file).
    /// </summary>
    public ServerFolderListing List(string? path, IReadOnlyCollection<string> extensions, int max = MaxEntries)
    {
        if (max < 1) max = 1;
        if (string.IsNullOrWhiteSpace(path)) return Drives(max);

        if (!TryNormalise(path, out var folder, out var refusal))
            return new ServerFolderListing(path, null, Array.Empty<ServerFileEntry>(), false, refusal);

        if (!Directory.Exists(folder))
            return new ServerFolderListing(folder, ParentOf(folder), Array.Empty<ServerFileEntry>(), false, "That folder does not exist on the job server.");

        var allowed = new HashSet<string>(extensions.Select(NormaliseExtension).Where(e => e.Length > 1), StringComparer.OrdinalIgnoreCase);
        var entries = new List<ServerFileEntry>();
        var capped = false;
        try
        {
            var info = new DirectoryInfo(folder);
            foreach (var dir in info.EnumerateDirectories("*", Listing).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (entries.Count >= max) { capped = true; break; }
                entries.Add(new ServerFileEntry(dir.Name, dir.FullName, true, null, ReadTime(dir), false));
            }
            if (!capped)
            {
                foreach (var file in info.EnumerateFiles("*", Listing).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (allowed.Count > 0 && !allowed.Contains(file.Extension)) continue;
                    if (entries.Count >= max) { capped = true; break; }
                    entries.Add(FileEntry(file));
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return new ServerFolderListing(folder, ParentOf(folder), Array.Empty<ServerFileEntry>(), false, "No access: the job server's account cannot read this folder.");
        }
        return new ServerFolderListing(folder, ParentOf(folder), entries, capped, null);
    }

    /// <summary>
    /// The folder a dialog opens at for a field's current value: the folder of a typed file path, or the
    /// typed folder itself, when that is an acceptable local path that exists; otherwise null (the drive list).
    /// </summary>
    public static string? StartFolderFor(string? currentValue)
    {
        if (string.IsNullOrWhiteSpace(currentValue) || !TryNormalise(currentValue, out var full, out _)) return null;
        if (Directory.Exists(full)) return full;
        var folder = System.IO.Path.GetDirectoryName(full);
        return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : null;
    }

    /// <summary>
    /// The absolute local form of <paramref name="path"/>, or the reason it is refused: relative paths,
    /// UNC and device paths, "." or ".." segments, and anything Path.GetFullPath would rewrite.
    /// </summary>
    public static bool TryNormalise(string? path, out string full, out string? refusal)
    {
        full = string.Empty;
        refusal = null;
        var candidate = (path ?? string.Empty).Trim();
        if (candidate.Length == 0) { refusal = "Type or pick a folder."; return false; }
        if (candidate.StartsWith(@"\\", StringComparison.Ordinal) || candidate.StartsWith("//", StringComparison.Ordinal))
        {
            refusal = "Network and device paths are not browsed. Pick a folder on the job server's own drives.";
            return false;
        }
        if (!System.IO.Path.IsPathFullyQualified(candidate))
        {
            refusal = "Only full paths on the job server are browsed, such as C:\\HRFeeds.";
            return false;
        }
        var segments = candidate.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == "." || s == ".."))
        {
            refusal = "A path with . or .. segments is not browsed.";
            return false;
        }
        string resolved;
        try { resolved = System.IO.Path.GetFullPath(candidate); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException or PathTooLongException)
        {
            refusal = "That is not a valid path.";
            return false;
        }
        if (!string.Equals(Trimmed(resolved), Trimmed(candidate.Replace('/', '\\')), StringComparison.OrdinalIgnoreCase))
        {
            refusal = "That path does not name one folder plainly.";
            return false;
        }
        full = resolved;
        return true;
    }

    private static ServerFolderListing Drives(int max)
    {
        var entries = new List<ServerFileEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool usable;
            try { usable = drive.DriveType == DriveType.Fixed && drive.IsReady; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { usable = false; }
            if (!usable) continue;
            if (entries.Count >= max) return new ServerFolderListing(null, null, entries, true, null);
            entries.Add(new ServerFileEntry(drive.Name, drive.RootDirectory.FullName, true, null, null, false));
        }
        return new ServerFolderListing(null, null, entries, false, null);
    }

    private static ServerFileEntry FileEntry(FileInfo file)
    {
        try { return new ServerFileEntry(file.Name, file.FullName, false, file.Length, file.LastWriteTimeUtc, false); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return new ServerFileEntry(file.Name, file.FullName, false, null, null, true);
        }
    }

    private static DateTime? ReadTime(DirectoryInfo dir)
    {
        try { return dir.LastWriteTimeUtc; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return null; }
    }

    private static string? ParentOf(string folder) => Directory.GetParent(folder)?.FullName;

    private static string NormaliseExtension(string extension)
    {
        var e = (extension ?? string.Empty).Trim();
        return e.Length == 0 ? e : e.StartsWith('.') ? e : "." + e;
    }

    private static string Trimmed(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;
}
