using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// Story 3.8, Phill's Q3 (a) (2026-11-18): where <c>open_package</c> unpacks copies. The folder is outside OneDrive by
/// default (the OneDrive client converts a notebook folder unpacked through COM into an online notebook, and OneNote then
/// closes: the 3.8 crash probe), it is a protected root like the journal's backups folder (AD-21), and a named destination
/// may only be inside it (follow-up (i)). Each copy's folder holds <see cref="ExportRootGuard.MarkerFileName"/>; only
/// such folders are ever cleaned up.
/// </summary>
public static class OpenedCopies
{
    /// <summary>Test-only override of the folder (Conventions: env vars only for test overrides of paths).</summary>
    public const string DirectoryOverrideVariable = "ONENOTE_MCP_OPENED_DIR";

    /// <summary>The folder's name beside the journal's default.</summary>
    public const string FolderName = "opened";

    /// <summary>
    /// The opened-copies folder: <see cref="DirectoryOverrideVariable"/> when set; else an <c>opened</c> folder beside the
    /// file <see cref="SettingsStore.PathOverrideVariable"/> names, when that is set; else
    /// <c>%LOCALAPPDATA%\OneNoteMcp\opened</c>. The same rule as the journal's default folder, so a test that overrides
    /// the settings path never touches the user's copies.
    /// </summary>
    public static string DefaultFolder(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var opened = getEnvironmentVariable(DirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(opened))
        {
            return Path.GetFullPath(opened);
        }

        var settings = getEnvironmentVariable(SettingsStore.PathOverrideVariable);
        if (!string.IsNullOrWhiteSpace(settings)
            && Path.GetDirectoryName(Path.GetFullPath(settings)) is { Length: > 0 } settingsFolder)
        {
            return Path.Combine(settingsFolder, FolderName);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneNoteMcp", FolderName);
    }

    /// <summary>
    /// The copies in <paramref name="folder"/>: its direct subfolders that hold the marker file and aren't links or
    /// junctions. Empty when the folder is missing or can't be read.
    /// </summary>
    public static IReadOnlyList<string> MarkedCopies(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        try
        {
            if (!Directory.Exists(folder))
            {
                return [];
            }

            return
            [
                .. Directory.EnumerateDirectories(folder)
                    .Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0
                        && File.Exists(Path.Combine(d, ExportRootGuard.MarkerFileName)))
                    .Order(StringComparer.OrdinalIgnoreCase),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// True when one of <paramref name="openNotebookPaths"/> is <paramref name="copy"/>, lies inside it, or holds it, in any
    /// of their forms (plain and canonical, <see cref="PathUnder.Prefixes"/>): OneNote is given the canonical path, while
    /// copies are listed through the plain folder, which may go through a link (the review of 6fe92b3). Paths that aren't
    /// local (a URL) never match.
    /// </summary>
    public static bool IsOpen(string copy, IEnumerable<string> openNotebookPaths)
    {
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(openNotebookPaths);
        var copyForms = PathUnder.Prefixes(copy);
        if (copyForms.Count == 0)
        {
            return false;
        }

        foreach (var notebookForms in openNotebookPaths.Select(PathUnder.Prefixes))
        {
            // Each form ends in a separator, so a prefix test is "the same folder, or inside it", at a segment boundary.
            if (copyForms.Any(c => notebookForms.Any(n => n.StartsWith(c, StringComparison.OrdinalIgnoreCase) || c.StartsWith(n, StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Story 3.8, Phill's cleanup answer (b) (2026-11-18): when the server starts, a copy in the opened-copies folder is deleted
/// when it holds the marker, its notebook isn't open in OneNote, and nothing in it changed for more than <see cref="MaxAge"/>
/// (backlog 21, Phill 2026-11-19: age is the last change, never the folder's creation time alone).
/// OneNote is read only when some copy is old enough. Only a successful read of OneNote's open notebooks counts: a failed
/// read, OneNote not running (a closed OneNote still lists its notebooks and reopens them; "not running" also covers one
/// with no window yet) included, deletes nothing (the review of 6fe92b3). A copy that can't be deleted is left for the
/// next start.
/// D186 (2026-11-27): a copy is deleted entry by entry, its marker last and then its empty folder, each step tried for
/// about half a second (Windows briefly refuses a file an antivirus or the indexer still holds, or a folder a junction was
/// just removed from). An entry that stays refused stops that copy with its marker still in place, so the next sweep
/// still sees it; a single recursive delete could take the marker first and leave a folder no sweep would touch again.
/// </summary>
public static class OpenedCopySweep
{
    /// <summary>How long a copy must go unchanged before it is deleted.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>How many times each delete is tried, and how long the sweep waits between tries: about half a second (D186).</summary>
    internal const int DeleteAttempts = 10;

    internal static readonly TimeSpan DeleteDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Deletes the expired copies in <paramref name="folder"/> as of <paramref name="now"/>; returns how many went.</summary>
    public static Task<int> SweepAsync(IOneNoteGateway gateway, string folder, DateTimeOffset now, CancellationToken ct) =>
        SweepAsync(gateway, folder, now, DeleteEntry, ct);

    /// <summary>
    /// <see cref="SweepAsync(IOneNoteGateway, string, DateTimeOffset, CancellationToken)"/> with
    /// <paramref name="deleteEntry"/> standing in for <see cref="DeleteEntry"/> (D186's test seam). It is given each entry
    /// of a copy, then the marker, then the copy's own (by then empty) folder.
    /// </summary>
    internal static async Task<int> SweepAsync(IOneNoteGateway gateway, string folder, DateTimeOffset now, Action<string> deleteEntry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deleteEntry);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(folder);
        var old = OpenedCopies.MarkedCopies(folder).Where(c => IsOld(c, now)).ToList();
        if (old.Count == 0)
        {
            return 0;
        }

        IReadOnlyList<string> open;
        try
        {
            var xml = await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, ct).ConfigureAwait(false);
            open = [.. HierarchyNode.Notebooks(xml).Select(n => n.Path)];
        }
        catch (Exception ex) when (ex is OneNoteException or OneNoteXmlException)
        {
            return 0;
        }

        var deleted = 0;
        foreach (var copy in old.Where(c => !OpenedCopies.IsOpen(c, open)))
        {
            if (await DeleteCopyAsync(copy, deleteEntry, ct).ConfigureAwait(false))
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>
    /// Deletes <paramref name="copy"/>: every entry but the marker, then the marker, then the empty folder, each tried
    /// <see cref="DeleteAttempts"/> times. False when a step still fails; the copy is then left for the next start, with
    /// its marker: the folder itself is only ever deleted empty, and when that is refused the marker is written back, so
    /// the next sweep still sees the copy.
    /// </summary>
    private static async Task<bool> DeleteCopyAsync(string copy, Action<string> deleteEntry, CancellationToken ct)
    {
        var marker = Path.Combine(copy, ExportRootGuard.MarkerFileName);
        try
        {
            // The entries are listed as entries: a link or junction is deleted itself, never followed.
            var entries = new DirectoryInfo(copy).EnumerateFileSystemInfos("*", OneLevel)
                .Select(e => e.FullName)
                .Where(e => !string.Equals(e, marker, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var entry in entries)
            {
                if (!await TryDeleteAsync(entry, deleteEntry, ct).ConfigureAwait(false))
                {
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The copy couldn't be listed: left whole, marker included, for the next start.
            return false;
        }

        if (!await TryDeleteAsync(marker, deleteEntry, ct).ConfigureAwait(false))
        {
            return false;
        }

        // The folder goes only while it is empty: anything that appeared since the entries were listed keeps it.
        if (await TryDeleteAsync(copy, path => DeleteEmptyFolder(path, deleteEntry), ct).ConfigureAwait(false))
        {
            return true;
        }

        RestoreMarker(marker);
        return false;
    }

    /// <summary>Calls <paramref name="deleteEntry"/> on the copy's folder only while it holds nothing; otherwise refuses like Windows.</summary>
    private static void DeleteEmptyFolder(string folder, Action<string> deleteEntry)
    {
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            throw new IOException("The opened copy's folder isn't empty.");
        }

        deleteEntry(folder);
    }

    /// <summary>Writes the empty marker back into a copy whose folder couldn't be deleted, so a later sweep still finds it. Best effort.</summary>
    private static void RestoreMarker(string marker)
    {
        try
        {
            using (new FileStream(marker, FileMode.OpenOrCreate, FileAccess.Write))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The folder may be half gone or held; it is left as it is.
        }
    }

    /// <summary>Calls <paramref name="deleteEntry"/> on <paramref name="path"/> up to <see cref="DeleteAttempts"/> times; true once it succeeds.</summary>
    private static async Task<bool> TryDeleteAsync(string path, Action<string> deleteEntry, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                deleteEntry(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= DeleteAttempts)
                {
                    return false;
                }
            }

            await Task.Delay(DeleteDelay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes one entry of a copy, or the copy's empty folder: a file; a link or junction itself, never what it points at;
    /// a plain folder with everything in it (.NET's recursive delete removes a link or junction inside without following
    /// it). An entry already gone counts as deleted.
    /// </summary>
    internal static void DeleteEntry(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var info = new FileInfo(path);
        if (!info.Exists && !Directory.Exists(path))
        {
            return;
        }

        if ((info.Attributes & FileAttributes.Directory) == 0)
        {
            File.Delete(path);
        }
        else
        {
            Directory.Delete(path, recursive: (info.Attributes & FileAttributes.ReparsePoint) == 0);
        }
    }

    /// <summary>
    /// True when the copy's last change is more than <see cref="MaxAge"/> before <paramref name="now"/>. The last change is
    /// the newest creation or last-write time of the copy's folder and of every file and folder inside it (a file copied
    /// in keeps its source's write time, so its creation time counts too). Links and junctions count by their own times and
    /// are never followed. A time that can't be read keeps the copy.
    /// </summary>
    private static bool IsOld(string copy, DateTimeOffset now)
    {
        try
        {
            return now.UtcDateTime - LastChange(new DirectoryInfo(copy)) > MaxAge;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>One folder's entries, links and junctions included as entries; recursion is <see cref="LastChange"/>'s.</summary>
    private static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>The newest time of <paramref name="folder"/> and everything below it, never descending through a reparse point.</summary>
    private static DateTime LastChange(DirectoryInfo folder)
    {
        var newest = Newest(folder);
        foreach (var entry in folder.EnumerateFileSystemInfos("*", OneLevel))
        {
            newest = Max(newest, Newest(entry));
            if (entry is DirectoryInfo inner && !inner.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                newest = Max(newest, LastChange(inner));
            }
        }

        return newest;
    }

    private static DateTime Newest(FileSystemInfo entry) => Max(entry.CreationTimeUtc, entry.LastWriteTimeUtc);

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
