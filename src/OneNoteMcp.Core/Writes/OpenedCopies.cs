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
/// </summary>
public static class OpenedCopySweep
{
    /// <summary>How long a copy must go unchanged before it is deleted.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>Deletes the expired copies in <paramref name="folder"/> as of <paramref name="now"/>; returns how many went.</summary>
    public static async Task<int> SweepAsync(IOneNoteGateway gateway, string folder, DateTimeOffset now, CancellationToken ct)
    {
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
            try
            {
                // Recursive delete removes a link or junction inside the copy without following it.
                Directory.Delete(copy, recursive: true);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next start.
            }
        }

        return deleted;
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
