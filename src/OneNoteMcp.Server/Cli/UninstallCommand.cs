using System.Text.RegularExpressions;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Server.Logging;

namespace OneNoteMcp.Server.Cli;

/// <summary>
/// Where <c>uninstall --purge</c> looks (AD-19). <see cref="Resolve"/> uses the same resolvers as the server, so the
/// test overrides of the settings and log paths apply here too.
/// </summary>
/// <param name="SettingsPath">The settings file, from <see cref="SettingsStore.ResolvePath"/>.</param>
/// <param name="LogDirectory">The log folder, from <see cref="FileLog.ResolveDirectory"/>.</param>
/// <param name="DefaultJournalPath">The journal folder when <c>settings.json</c> doesn't set one or can't be read.</param>
/// <param name="SystemFolders">Folders a journal path must never be: the profile, AppData, Desktop, Documents, Windows, Program Files.</param>
public sealed record UninstallPaths(string SettingsPath, string LogDirectory, string DefaultJournalPath, IReadOnlyList<string> SystemFolders)
{
    public static UninstallPaths Resolve(Func<string, string?> getEnvironmentVariable) => new(
        SettingsStore.ResolvePath(getEnvironmentVariable),
        FileLog.ResolveDirectory(getEnvironmentVariable),
        JournalSettings.Default().Path,
        DefaultSystemFolders());

    public static IReadOnlyList<string> DefaultSystemFolders() =>
        new[]
        {
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
        }
        .Select(Environment.GetFolderPath)
        .Where(p => !string.IsNullOrEmpty(p))
        .ToList();
}

/// <summary>
/// <c>OneNoteMcp uninstall --purge</c> (AD-19): removes the files the server leaves on disk, and only those. That is the
/// settings file and its <c>.settings.*.tmp</c> leftovers, the <c>onenote-mcp-*.log</c> files, and journal-named files
/// (<c>{yyyyMMddTHHmmssfff}-{pid}-{guid}.json.gz</c>). It never recurses, removes a folder only when nothing is left in
/// it, and refuses a journal folder that looks like a root or system folder. It never removes the exe, the plugin, or
/// Claude Code's own <c>tool-results</c> files. It starts no COM.
/// <para>Interactive, it asks the user to type <c>purge</c>; otherwise it needs <c>--yes</c>.</para>
/// </summary>
public sealed partial class UninstallCommand
{
    public const string Usage =
        "Usage:\n"
        + "  OneNoteMcp uninstall --purge [--yes]   remove OneNoteMcp's settings (read grants included), logs and journal\n"
        + "It asks you to type 'purge' first; --yes skips that, and is required when it can't ask.\n"
        + "It doesn't remove OneNoteMcp.exe or the plugin: do that with /plugin uninstall, or delete the exe.";

    private const string ConfirmWord = "purge";
    private const string OwnFolderName = "OneNoteMcp";

    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly bool interactive;
    private readonly UninstallPaths paths;

    public UninstallCommand(TextReader input, TextWriter output, bool interactive, UninstallPaths paths)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(paths);
        this.input = input;
        this.output = output;
        this.interactive = interactive;
        this.paths = paths;
    }

    /// <summary>Runs <c>uninstall</c> with the arguments after it. Returns the exit code: 0 done, 1 something refused or failed, 2 bad usage.</summary>
    public int Run(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var purge = false;
        var yes = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--purge":
                    purge = true;
                    break;
                case "--yes":
                    yes = true;
                    break;
                default:
                    output.WriteLine($"Unknown argument: {arg}");
                    output.WriteLine(Usage);
                    return 2;
            }
        }

        if (!purge)
        {
            output.WriteLine(Usage);
            return 2;
        }

        if (!yes && !interactive)
        {
            output.WriteLine("OneNoteMcp uninstall --purge can't ask you to confirm here. Run it in a terminal, or add --yes. Nothing removed.");
            return 2;
        }

        var plan = Plan();
        foreach (var note in plan.Notes)
        {
            output.WriteLine(note);
        }

        foreach (var refusal in plan.Refusals)
        {
            output.WriteLine($"Refused: {refusal}");
        }

        if (plan.Files.Count == 0 && plan.Folders.Count == 0)
        {
            output.WriteLine("Nothing to remove.");
            return plan.Refusals.Count == 0 ? 0 : 1;
        }

        if (plan.Files.Count > 0)
        {
            output.WriteLine("This removes:");
            foreach (var file in plan.Files)
            {
                output.WriteLine($"  {file}");
            }
        }

        output.WriteLine("These folders are removed too, if nothing else is left in them:");
        foreach (var folder in plan.Folders)
        {
            output.WriteLine($"  {folder}");
        }

        if (!yes)
        {
            output.Write($"Type {ConfirmWord} to remove them: ");
            output.Flush();
            if (!string.Equals(input.ReadLine()?.Trim(), ConfirmWord, StringComparison.Ordinal))
            {
                output.WriteLine("Nothing removed.");
                return 1;
            }
        }

        var failed = plan.Refusals.Count > 0;
        foreach (var file in plan.Files)
        {
            failed |= !Delete(file);
        }

        foreach (var folder in plan.Folders)
        {
            failed |= !RemoveIfEmpty(folder);
        }

        output.WriteLine(failed
            ? "Done, but something above was refused or couldn't be removed."
            : "Done.");
        output.WriteLine("OneNoteMcp.exe and the plugin are still installed, and Claude Code's own tool-results files aren't touched; see the README's Uninstall section.");
        return failed ? 1 : 0;
    }

    private sealed record PurgePlan(List<string> Files, List<string> Folders, List<string> Refusals, List<string> Notes);

    private PurgePlan Plan()
    {
        var plan = new PurgePlan([], [], [], []);

        // Settings: the file itself and the writer's temp-file leftovers beside it.
        var settingsPath = Path.GetFullPath(paths.SettingsPath);
        var snapshot = new SettingsStore(settingsPath).Read();
        if (File.Exists(settingsPath))
        {
            plan.Files.Add(settingsPath);
        }

        var settingsFolder = Path.GetDirectoryName(settingsPath);
        if (settingsFolder is not null)
        {
            AddMatching(plan, settingsFolder, SettingsTempFile());
        }

        // Logs.
        var logFolder = Path.GetFullPath(paths.LogDirectory);
        AddMatching(plan, logFolder, LogFile());

        // Journal: settings' journal.path, else the default (and say why when the file can't be read).
        var journalFolder = Path.GetFullPath(JournalFolder(snapshot, plan.Notes));
        var journalRefusal = RootLikeReason(journalFolder, paths.SystemFolders);
        if (journalRefusal is not null)
        {
            plan.Refusals.Add($"{journalFolder}: the journal folder is {journalRefusal}, so nothing in it is removed.");
        }
        else
        {
            AddMatching(plan, journalFolder, JournalFile());
        }

        // OneNoteMcp's own folders, in removal order: children before the OneNoteMcp folders that hold them.
        var candidates = new List<string>();
        if (settingsFolder is not null)
        {
            candidates.Add(settingsFolder);
        }

        candidates.Add(logFolder);
        if (journalRefusal is null)
        {
            candidates.Add(journalFolder);
        }

        foreach (var child in candidates.ToList())
        {
            if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(child)) is { } parent
                && string.Equals(Path.GetFileName(parent), OwnFolderName, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(parent);
            }
        }

        foreach (var folder in candidates.Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(folder) && RootLikeReason(folder, paths.SystemFolders) is null)
            {
                plan.Folders.Add(folder);
            }
        }

        // Remove a child folder before its parent.
        plan.Folders.Sort((a, b) => b.Length.CompareTo(a.Length));
        plan.Files.Sort(StringComparer.OrdinalIgnoreCase);
        return plan;
    }

    private string JournalFolder(SettingsSnapshot snapshot, List<string> notes)
    {
        switch (snapshot.Status)
        {
            case SettingsStatus.Invalid:
                notes.Add($"settings.json couldn't be read ({snapshot.Reason ?? "invalid"}), so the journal folder checked is the default: {paths.DefaultJournalPath}");
                return paths.DefaultJournalPath;
            case SettingsStatus.NewerSchema:
                notes.Add($"settings.json is from a newer OneNoteMcp, so the journal folder checked is the default: {paths.DefaultJournalPath}");
                return paths.DefaultJournalPath;
            case SettingsStatus.Absent:
                return paths.DefaultJournalPath;
            default:
                // A journal.path that isn't set reads as the store's own default; resolve it through ours instead.
                return string.Equals(snapshot.Journal.Path, JournalSettings.Default().Path, StringComparison.OrdinalIgnoreCase)
                    ? paths.DefaultJournalPath
                    : snapshot.Journal.Path;
        }
    }

    private void AddMatching(PurgePlan plan, string folder, Regex pattern)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return;
            }

            // Top level only; matched by full name, so Win32 wildcard quirks can't widen it.
            plan.Files.AddRange(Directory.EnumerateFiles(folder)
                .Where(f => pattern.IsMatch(Path.GetFileName(f))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            plan.Refusals.Add($"{folder}: couldn't list it ({ex.GetType().Name}), so nothing in it is removed.");
        }
    }

    private bool Delete(string file)
    {
        try
        {
            File.Delete(file);
            output.WriteLine($"Removed {file}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Couldn't remove {file}: {Describe(ex)}");
            return false;
        }
    }

    private bool RemoveIfEmpty(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return true;
            }

            if (Directory.EnumerateFileSystemEntries(folder).Any())
            {
                output.WriteLine($"Kept {folder}: other files are still in it.");
                return true;
            }

            Directory.Delete(folder, recursive: false);
            output.WriteLine($"Removed {folder}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.WriteLine($"Couldn't remove {folder}: {Describe(ex)}");
            return false;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "access denied",
        IOException => "it's in use (is a OneNoteMcp server still running? Close Claude Code and try again)",
        _ => ex.GetType().Name,
    };

    /// <summary>Why <paramref name="folder"/> is too broad to hold a journal, or null when it isn't.</summary>
    public static string? RootLikeReason(string folder, IReadOnlyList<string> systemFolders)
    {
        ArgumentNullException.ThrowIfNull(systemFolders);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return "not an absolute path";
        }

        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        if (string.Equals(full, trimmedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return "a drive or share root";
        }

        if (systemFolders.Any(s => string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetFullPath(s)), StringComparison.OrdinalIgnoreCase)))
        {
            return "a system or profile folder";
        }

        var below = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return below.Length < 2 ? "fewer than two folders below its root" : null;
    }

    [GeneratedRegex(@"^\.settings\..+\.tmp$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SettingsTempFile();

    [GeneratedRegex(@"^onenote-mcp-.*\.log$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LogFile();

    [GeneratedRegex(
        @"^\d{8}T\d{9}-\d+-(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.json\.gz$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JournalFile();
}
