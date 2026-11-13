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
