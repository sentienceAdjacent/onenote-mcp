using System.Globalization;
using System.Text.RegularExpressions;
using OneNoteMcp.Core.Settings;

namespace OneNoteMcp.Core.Journal;

/// <summary>
/// The journal's file names and its folder (AD-8, AD-10): the one place an entry's name is made and recognised, shared by
/// the writer, the pruner and <c>uninstall --purge</c>, and the one resolver of the default folder.
/// <para>An entry is <c>{takenAtUtc:yyyyMMddTHHmmssfff}-{pid}-{guid}.json.gz</c> (the GUID as 32 hex digits; the dashed
/// form is recognised too). It is written once, to <c>.{name}.tmp</c> in the same folder, then renamed.</para>
/// </summary>
public static partial class JournalFiles
{
    /// <summary>Test-only override of the default journal folder (Conventions: env vars only for test overrides of paths).</summary>
    public const string DirectoryOverrideVariable = "ONENOTE_MCP_JOURNAL_DIR";

    /// <summary>The entry file's extension.</summary>
    public const string Extension = ".json.gz";

    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfff";

    /// <summary>
    /// The journal folder when <c>settings.json</c> sets no <c>journal.path</c> (story 2.8 decision Q1):
    /// <see cref="DirectoryOverrideVariable"/> when set; else a <c>journal</c> folder beside the file
    /// <see cref="SettingsStore.PathOverrideVariable"/> names, when that is set; else
    /// <c>%LOCALAPPDATA%\OneNoteMcp\journal</c>. The server's settings, the pruner and <c>uninstall --purge</c> all use it,
    /// so a test that overrides the settings path never touches the real journal.
    /// </summary>
    public static string DefaultFolder(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var journal = getEnvironmentVariable(DirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(journal))
        {
            return Path.GetFullPath(journal);
        }

        var settings = getEnvironmentVariable(SettingsStore.PathOverrideVariable);
        if (!string.IsNullOrWhiteSpace(settings)
            && Path.GetDirectoryName(Path.GetFullPath(settings)) is { Length: > 0 } settingsFolder)
        {
            return Path.Combine(settingsFolder, "journal");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneNoteMcp", "journal");
    }

    /// <summary>The entry file's name for an entry taken at <paramref name="takenAtUtc"/> by process <paramref name="processId"/>.</summary>
    public static string EntryName(DateTime takenAtUtc, int processId, Guid entryId) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{takenAtUtc.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture)}-{processId}-{entryId:N}{Extension}");

    /// <summary>The temporary name an entry is written to before the rename: <c>.{name}.tmp</c>.</summary>
    public static string TempName(string entryName)
    {
        ArgumentException.ThrowIfNullOrEmpty(entryName);
        return "." + entryName + ".tmp";
    }

    /// <summary>
    /// The full path of entry <paramref name="entryId"/>'s file in <paramref name="folder"/>, found by its name
    /// (<c>*-{entryId}.json.gz</c>, 32 hex digits or dashed) with no scan of the entries' contents; null when there is
    /// none, or the folder doesn't exist (story 2.11). Temporary files never match. When more than one file matches (only
    /// a copied file could do that), the first by ordinal name is taken. I/O failures propagate.
    /// </summary>
    public static string? Find(string folder, Guid entryId)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        if (!Directory.Exists(folder))
        {
            return null;
        }

        string[] suffixes =
        [
            "-" + entryId.ToString("N", CultureInfo.InvariantCulture) + Extension,
            "-" + entryId.ToString("D", CultureInfo.InvariantCulture) + Extension,
        ];
        return suffixes
            .SelectMany(suffix => Directory.EnumerateFiles(folder, "*" + suffix, SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path) is var name && IsEntry(name) && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>True when <paramref name="fileName"/> (a name, not a path) is a journal entry's.</summary>
    public static bool IsEntry(string fileName) => EntryPattern().IsMatch(fileName ?? string.Empty);

    /// <summary>True when <paramref name="fileName"/> is an entry's temporary name, left by a write that didn't finish.</summary>
    public static bool IsTemp(string fileName) => TempPattern().IsMatch(fileName ?? string.Empty);

    /// <summary>
    /// The UTC time in an entry's name, or in its temporary name; false for any other name, or one whose timestamp isn't
    /// a real date.
    /// </summary>
    public static bool TryParseTimestamp(string fileName, out DateTime takenAtUtc)
    {
        var match = EntryPattern().Match(fileName ?? string.Empty);
        if (!match.Success)
        {
            match = TempPattern().Match(fileName ?? string.Empty);
        }

        if (match.Success
            && DateTime.TryParseExact(
                match.Groups["ts"].Value,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out takenAtUtc))
        {
            return true;
        }

        takenAtUtc = default;
        return false;
    }

    /// <summary>An entry's name: a timestamp, a process ID and a GUID (32 hex digits, or dashed).</summary>
    [GeneratedRegex(
        @"^(?<ts>\d{8}T\d{9})-\d+-(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.json\.gz$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex EntryPattern();

    /// <summary>An entry's temporary name: <c>.</c>, an entry's name, <c>.tmp</c>.</summary>
    [GeneratedRegex(
        @"^\.(?<ts>\d{8}T\d{9})-\d+-(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.json\.gz\.tmp$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex TempPattern();
}
