using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Settings;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// The file-system side of a new notebook's folder (story 5.3; AD-7 <c>Create</c> validation): path containment, the
/// folders OneNoteMcp keeps for itself, and whether a folder is absent or empty. Paths never go into messages or logs.
/// </summary>
public static class NotebookFolders
{
    /// <summary>
    /// <paramref name="path"/> with a full form and no trailing separator, for comparison only; null when it isn't a
    /// fully qualified local path (a URL, say).
    /// </summary>
    public static string? Normal(string? path)
    {
        if (string.IsNullOrEmpty(path) || CloudNotebookCreate.IsUrl(path))
        {
            return null;
        }

        try
        {
            var slashed = path.Replace('/', '\\');
            return Path.IsPathFullyQualified(slashed) ? Path.GetFullPath(slashed).TrimEnd('\\') : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="path"/> and <paramref name="folder"/> are the same folder, ignoring case as Windows does.</summary>
    public static bool Same(string path, string folder) =>
        Normal(path) is { } p && Normal(folder) is { } f && string.Equals(p, f, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="folder"/>, ignoring case as Windows does.</summary>
    public static bool Inside(string path, string folder) =>
        Normal(path) is { } p && Normal(folder) is { } f
        && p.Length > f.Length + 1
        && p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The folders OneNoteMcp keeps for itself, which no notebook may be created in: the folder of the settings file
    /// <paramref name="settings"/> was read from, the journal folder it names, the default <c>%APPDATA%\OneNoteMcp</c>
    /// (guarded even when the settings path is overridden, AD-6) and <c>%LOCALAPPDATA%\OneNoteMcp</c>. Each is given as
    /// written and, when it resolves, in its canonical form (links followed), so a link to one counts too.
    /// </summary>
    public static IReadOnlyList<string> OwnFolders(SettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var folders = new List<string>();
        if (Path.GetDirectoryName(settings.Path) is { Length: > 0 } settingsFolder)
        {
            folders.Add(settingsFolder);
        }

        folders.Add(settings.Journal.Path);
        folders.Add(JournalFiles.DefaultFolder(Environment.GetEnvironmentVariable));
        foreach (var special in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            if (Environment.GetFolderPath(special) is { Length: > 0 } root)
            {
                folders.Add(Path.Combine(root, "OneNoteMcp"));
            }
        }

        return WithCanonical(folders);
    }

    /// <summary><paramref name="paths"/> (local ones only), each followed by its canonical form when that resolves and differs.</summary>
    public static IReadOnlyList<string> WithCanonical(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var all = new List<string>();
        foreach (var path in paths)
        {
            if (Normal(path) is not { } normal)
            {
                continue;
            }

            all.Add(normal);
            if (FilePathGate.Canonical(normal, out _) is { } canonical && !Same(canonical, normal))
            {
                all.Add(canonical);
            }
        }

        return all;
    }

    /// <summary>The table-of-contents file every local notebook keeps at its root, such as <c>Open Notebook.onetoc2</c>.</summary>
    public const string NotebookTocPattern = "*.onetoc2";

    /// <summary>
    /// True when <paramref name="folder"/> or any folder above it holds a <c>*.onetoc2</c> file (security review, Phill,
    /// 2026-11-13): a closed notebook isn't in OneNote's open list, so its table of contents is how a folder inside one is
    /// found. Hidden and system files count, and the name is matched ignoring case. A folder that doesn't exist yet is
    /// passed over, as is one that can't be listed (it can't be shown to hold a notebook); a URL or a path that isn't
    /// fully qualified is false. Links aren't followed here, so pass a canonical path.
    /// </summary>
    public static bool InsideANotebookFolder(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (Normal(folder) is null)
        {
            return false;
        }

        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(Path.GetFullPath(folder.Replace('/', '\\')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }

        for (; current is not null; current = current.Parent)
        {
            if (HoldsANotebookToc(current.FullName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HoldsANotebookToc(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return false;
            }

            using var files = Directory.EnumerateFiles(folder, NotebookTocPattern, new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                MatchType = MatchType.Simple,
                RecurseSubdirectories = false,
            }).GetEnumerator();
            return files.MoveNext();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>What is at <paramref name="folder"/> now.</summary>
    public static FolderState State(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        try
        {
            if (File.Exists(folder))
            {
                return FolderState.File;
            }

            if (!Directory.Exists(folder))
            {
                return FolderState.Absent;
            }

            // Every entry counts, hidden and system ones included.
            using var entries = Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
            }).GetEnumerator();
            return entries.MoveNext() ? FolderState.NotEmpty : FolderState.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A folder that can't be listed can't be shown to be empty.
            return FolderState.Unreadable;
        }
    }
}

/// <summary>What <see cref="NotebookFolders.State"/> found at a folder path.</summary>
public enum FolderState
{
    Absent,
    Empty,
    NotEmpty,
    File,
    Unreadable,
}
