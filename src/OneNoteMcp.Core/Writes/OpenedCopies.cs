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
    /// True when one of <paramref name="openNotebookPaths"/> is <paramref name="copy"/>, lies inside it, or holds it.
    /// Paths that aren't local (a URL) never match.
    /// </summary>
    public static bool IsOpen(string copy, IEnumerable<string> openNotebookPaths)
    {
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(openNotebookPaths);
        var full = PathUnder.Full(copy)?.TrimEnd(Path.DirectorySeparatorChar);
        if (full is null)
        {
            return false;
        }

        return openNotebookPaths
            .Select(p => PathUnder.Full(p)?.TrimEnd(Path.DirectorySeparatorChar))
            .OfType<string>()
            .Any(n => string.Equals(n, full, StringComparison.OrdinalIgnoreCase) || PathUnder.IsUnder(n, [full]) || PathUnder.IsUnder(full, [n]));
    }
}

/// <summary>
/// Story 3.8, Phill's cleanup answer (b) (2026-11-18): when the server starts, a copy in the opened-copies folder is deleted
/// when it holds the marker, its notebook isn't open in OneNote, and its folder is more than <see cref="MaxAge"/> old.
/// OneNote is read only when some copy is old enough; OneNote not running means nothing is open; any other failed read
/// deletes nothing. A copy that can't be deleted is left for the next start.
/// </summary>
public static class OpenedCopySweep
{
    /// <summary>How old a copy's folder must be before it is deleted.</summary>
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
        catch (OneNoteException ex) when (ex.Code == ErrorCode.OneNoteNotRunning)
        {
            open = [];
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

    private static bool IsOld(string copy, DateTimeOffset now)
    {
        try
        {
            return now.UtcDateTime - Directory.GetCreationTimeUtc(copy) > MaxAge;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
