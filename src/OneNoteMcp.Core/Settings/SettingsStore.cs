using System.Globalization;
using System.Text.Json;

namespace OneNoteMcp.Core.Settings;

/// <summary>
/// The one Core type that owns <c>%APPDATA%\OneNoteMcp\settings.json</c> (AD-10). This is the read side only:
/// every <see cref="Read"/> reads the file again and returns a new snapshot, nothing is cached, and the store
/// never writes the file (<see cref="SettingsWriter"/> does). <c>notebooks{}</c> (write grants, AD-6) and
/// <c>protectedSections{}</c> (read grants, AD-21) are read here. Unknown keys are ignored.
/// </summary>
public sealed class SettingsStore
{
    /// <summary>Test-only override of the settings file path (Conventions: env vars only for test overrides of paths).</summary>
    public const string PathOverrideVariable = "ONENOTE_MCP_SETTINGS_PATH";

    public SettingsStore()
        : this(ResolvePath(Environment.GetEnvironmentVariable))
    {
    }

    public SettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = path;
    }

    public string Path { get; }

    /// <summary><see cref="PathOverrideVariable"/> when set, else <c>%APPDATA%\OneNoteMcp\settings.json</c>.</summary>
    public static string ResolvePath(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var overridden = getEnvironmentVariable(PathOverrideVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OneNoteMcp", "settings.json")
            : System.IO.Path.GetFullPath(overridden);
    }

    public SettingsSnapshot Read()
    {
        byte[] bytes;
        try
        {
            // No File.Exists pre-check: it returns false on a permission error, which would read as Absent.
            // A path that is a folder throws UnauthorizedAccessException, so it reads as Invalid.
            // Share everything so a future writer's File.Replace is never blocked by a reader.
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return SettingsSnapshot.Defaults(Path, SettingsStatus.Absent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SettingsSnapshot.Defaults(Path, SettingsStatus.Invalid, $"settings.json: could not be read ({ex.GetType().Name})");
        }

        return Parse(Path, bytes);
    }

    private static SettingsSnapshot Parse(string path, byte[] bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, SettingsJson.Options(allowDuplicateProperties: true));
        }
        catch (JsonException ex)
        {
            // JsonException's message can quote the offending text; report only the position.
            var line = ex.LineNumber is { } l ? (l + 1).ToString(CultureInfo.InvariantCulture) : "?";
            return SettingsSnapshot.Defaults(path, SettingsStatus.Invalid, $"settings.json: not valid JSON (line {line})");
        }

        using (document)
        {
            try
            {
                return Validate(path, document.RootElement);
            }
            catch (InvalidSettingException ex)
            {
                return SettingsSnapshot.Defaults(path, SettingsStatus.Invalid, ex.Message);
            }
        }
    }

    /// <summary>
    /// Reads every settings area in a fixed order, so the first field that fails is the one reported. A newer
    /// <c>schemaVersion</c> stops the read. <c>notebooks</c> and then <c>protectedSections</c> are read last, so a key
    /// that differs only in case is the file's only problem when it is reported.
    /// </summary>
    private static SettingsSnapshot Validate(string path, JsonElement root)
    {
        if (ReadSchemaVersion(root) > SettingsSnapshot.KnownSchemaVersion)
        {
            // A newer shape may differ in any field, so none of it is read (AD-10, AD-19).
            return SettingsSnapshot.Defaults(path, SettingsStatus.NewerSchema, "schemaVersion: newer than this server supports");
        }

        var allowSilentActivation = ReadAllowSilentActivation(root);
        var timeouts = ReadTimeouts(root);
        var journal = ReadJournal(root);
        var results = ReadResults(root);
        var search = ReadSearch(root);
        var readGrants = ReadReadGrants(root);
        var exportRoots = ReadExportRoots(root);

        // Read last, so a duplicate key is the file's only problem when it is reported (AD-6: the CLI's revoke still runs).
        var notebooks = ReadNotebooks(root, out var duplicateNotebook);
        var grants = ReadProtectedSections(root, out var duplicateSection);
        if (duplicateNotebook || duplicateSection)
        {
            var where = (duplicateNotebook, duplicateSection) switch
            {
                (true, true) => "notebooks and protectedSections",
                (true, false) => "notebooks",
                _ => "protectedSections",
            };
            return new SettingsSnapshot(
                path,
                SettingsStatus.Invalid,
                $"{where}: two keys differ only in case",
                allowSilentActivation: false,
                Timeouts.Default,
                JournalSettings.Default(),
                ResultSettings.Default,
                SearchSettings.Default,
                ExportRoots.Default(),
                grants,
                duplicateGrantKeysOnly: true,
                notebookEntries: notebooks,
                duplicateSectionKeys: duplicateSection,
                duplicateNotebookKeys: duplicateNotebook);
        }

        return new SettingsSnapshot(
            path, SettingsStatus.Ok, reason: null, allowSilentActivation, timeouts, journal, results, search, exportRoots, grants, readGrants: readGrants, notebookEntries: notebooks);
    }

    /// <summary>
    /// <c>notebooks{}</c> (AD-6, AD-10): every write grant, in file order. <paramref name="duplicate"/> is true when two
    /// keys are equal ignoring case, exact duplicates included, which the parse allows (<see cref="SettingsJson.Options"/>).
    /// </summary>
    private static IReadOnlyList<NotebookGrant> ReadNotebooks(JsonElement root, out bool duplicate)
    {
        var grants = new List<NotebookGrant>();
        duplicate = false;
        if (root.TryGetProperty("notebooks", out var notebooks))
        {
            RequireObject(notebooks, "notebooks");
            var seen = new HashSet<string>(Access.GrantKeys.Comparer);
            foreach (var entry in notebooks.EnumerateObject())
            {
                grants.Add(NotebookEntry(entry));
                duplicate |= !seen.Add(entry.Name);
            }
        }

        return grants.AsReadOnly();
    }

    /// <summary>One <c>notebooks{}</c> entry. Reasons name fields and never quote the file's values.</summary>
    private static NotebookGrant NotebookEntry(JsonProperty entry)
    {
        const string Field = "notebooks";
        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidSettingException($"{Field}: a key is empty");
        }

        var value = entry.Value;
        RequireObject(value, $"{Field} entry");
        var grantedVia = RequiredString(value, "grantedVia", Field);
        if (grantedVia is not (Access.GrantedVia.Elicitation or Access.GrantedVia.Cli or Access.GrantedVia.Created))
        {
            throw new InvalidSettingException($"{Field} entry grantedVia: must be elicitation, cli or created");
        }

        return new NotebookGrant(
            entry.Name,
            RequiredString(value, "name", Field),
            RequiredString(value, "idAtGrant", Field),
            RequiredBool(value, "write", Field),
            RequiredBool(value, "permanentDelete", Field),
            Date(value, "grantedAt", Field) ?? throw new InvalidSettingException($"{Field} entry grantedAt: missing"),
            grantedVia);
    }

    private static bool RequiredBool(JsonElement parent, string name, string field) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidSettingException($"{field} entry {name}: must be true or false");

    /// <summary><c>schemaVersion</c>: required, a whole number, and not older than this server's. A newer one is returned.</summary>
    private static long ReadSchemaVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidSettingException("(root): must be a JSON object");
        }

        if (!root.TryGetProperty("schemaVersion", out var version))
        {
            throw new InvalidSettingException("schemaVersion: missing");
        }

        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt64(out var schemaVersion))
        {
            throw new InvalidSettingException("schemaVersion: must be a whole number");
        }

        if (schemaVersion < SettingsSnapshot.KnownSchemaVersion)
        {
            throw new InvalidSettingException("schemaVersion: must be 1");
        }

        return schemaVersion;
    }

    private static bool ReadAllowSilentActivation(JsonElement root)
    {
        if (!root.TryGetProperty("allowSilentActivation", out var silent))
        {
            return false;
        }

        return silent.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidSettingException("allowSilentActivation: must be true or false"),
        };
    }

    private static Timeouts ReadTimeouts(JsonElement root)
    {
        var timeouts = Timeouts.Default;
        if (!root.TryGetProperty("timeouts", out var t))
        {
            return timeouts;
        }

        RequireObject(t, "timeouts");
        return new Timeouts(
            Seconds(t, "callSeconds", timeouts.Call),
            Seconds(t, "publishSeconds", timeouts.Publish),
            Seconds(t, "activationSeconds", timeouts.Activation),
            Seconds(t, "idleReleaseSeconds", timeouts.IdleRelease));
    }

    private static JournalSettings ReadJournal(JsonElement root)
    {
        var journal = JournalSettings.Default();
        if (!root.TryGetProperty("journal", out var j))
        {
            return journal;
        }

        RequireObject(j, "journal");
        return new JournalSettings(
            AbsolutePath(j, "path", "journal.path") ?? journal.Path,
            PositiveInt(j, "retentionDays", "journal.retentionDays") ?? journal.RetentionDays,
            PositiveInt(j, "maxSizeMB", "journal.maxSizeMB") ?? journal.MaxSizeMB);
    }

    private static ResultSettings ReadResults(JsonElement root)
    {
        var results = ResultSettings.Default;
        if (!root.TryGetProperty("results", out var r))
        {
            return results;
        }

        RequireObject(r, "results");
        return new ResultSettings(
            RangedInt(r, "inlineMaxChars", "results.inlineMaxChars", ResultSettings.InlineMaxCharsMin, ResultSettings.InlineMaxCharsMax) ?? results.InlineMaxChars,
            RangedInt(r, "ttlMinutes", "results.ttlMinutes", ResultSettings.TtlMinutesMin, ResultSettings.TtlMinutesMax) ?? results.TtlMinutes,
            RangedInt(r, "maxStoreMB", "results.maxStoreMB", ResultSettings.MaxStoreMBMin, ResultSettings.MaxStoreMBMax) ?? results.MaxStoreMB);
    }

    private static SearchSettings ReadSearch(JsonElement root)
    {
        var search = SearchSettings.Default;
        if (!root.TryGetProperty("search", out var q))
        {
            return search;
        }

        RequireObject(q, "search");
        return new SearchSettings(
            RangedInt(q, "cacheMaxMB", "search.cacheMaxMB", SearchSettings.CacheMaxMBMin, SearchSettings.CacheMaxMBMax) ?? search.CacheMaxMB);
    }

    private static ReadGrantSettings ReadReadGrants(JsonElement root)
    {
        var readGrants = ReadGrantSettings.Default;
        if (!root.TryGetProperty("readGrants", out var rg))
        {
            return readGrants;
        }

        RequireObject(rg, "readGrants");
        return new ReadGrantSettings(
            RangedInt(rg, "onceMinutes", "readGrants.onceMinutes", ReadGrantSettings.OnceMinutesMin, ReadGrantSettings.OnceMinutesMax) ?? readGrants.OnceMinutes);
    }

    private static ExportRoots ReadExportRoots(JsonElement root)
    {
        if (!root.TryGetProperty("exportRoots", out var roots))
        {
            return ExportRoots.Default();
        }

        if (roots.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidSettingException("exportRoots: must be an array of absolute paths");
        }

        var paths = new List<string>();
        var index = 0;
        foreach (var entry in roots.EnumerateArray())
        {
            paths.Add(RequireAbsolutePath(entry, $"exportRoots[{index.ToString(CultureInfo.InvariantCulture)}]"));
            index++;
        }

        return new ExportRoots(paths.AsReadOnly());
    }

    /// <summary>
    /// <c>protectedSections{}</c>: every entry, in file order. <paramref name="duplicate"/> is true when two keys differ
    /// only in case (AD-6). Two identical keys are both read too, since the parse allows them (<see cref="SettingsJson.Options"/>).
    /// </summary>
    private static IReadOnlyList<ProtectedSectionGrant> ReadProtectedSections(JsonElement root, out bool duplicate)
    {
        var grants = new List<ProtectedSectionGrant>();
        duplicate = false;
        if (root.TryGetProperty("protectedSections", out var sections))
        {
            RequireObject(sections, "protectedSections");
            var seen = new HashSet<string>(Access.GrantKeys.Comparer);
            foreach (var entry in sections.EnumerateObject())
            {
                grants.Add(Grant(entry));
                duplicate |= !seen.Add(entry.Name);
            }
        }

        return grants.AsReadOnly();
    }

    /// <summary>One <c>protectedSections{}</c> entry. Reasons name fields and never quote the file's values.</summary>
    private static ProtectedSectionGrant Grant(JsonProperty entry)
    {
        const string Field = "protectedSections";
        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidSettingException($"{Field}: a key is empty");
        }

        var value = entry.Value;
        RequireObject(value, $"{Field} entry");
        var grantedVia = RequiredString(value, "grantedVia", Field);
        if (grantedVia is not (Access.GrantedVia.Elicitation or Access.GrantedVia.Cli))
        {
            throw new InvalidSettingException($"{Field} entry grantedVia: must be elicitation or cli");
        }

        return new ProtectedSectionGrant(
            entry.Name,
            RequiredString(value, "name", Field),
            RequiredString(value, "notebookKey", Field),
            RequiredString(value, "idAtGrant", Field),
            Date(value, "grantedAt", Field) ?? throw new InvalidSettingException($"{Field} entry grantedAt: missing"),
            grantedVia,
            Date(value, "expiresAt", Field));
    }

    private static string RequiredString(JsonElement parent, string name, string field)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidSettingException($"{field} entry {name}: must be a string");
        }

        return value.GetString()!;
    }

    /// <summary>An ISO-8601 date-time, read as UTC, or null when absent.</summary>
    private static DateTimeOffset? Date(JsonElement parent, string name, string field)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || !SettingsJson.TryParseUtc(value.GetString(), out var date))
        {
            throw new InvalidSettingException($"{field} entry {name}: must be an ISO-8601 date-time");
        }

        return date;
    }

    private static void RequireObject(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidSettingException($"{field}: must be an object");
        }
    }

    /// <summary>The largest timeout whose milliseconds fit an int, the limit of CancelAfter and Task.Delay.</summary>
    private const int MaxTimeoutSeconds = int.MaxValue / 1000;

    private static TimeSpan Seconds(JsonElement parent, string name, TimeSpan fallback) =>
        PositiveInt(parent, name, $"timeouts.{name}", MaxTimeoutSeconds) is { } seconds ? TimeSpan.FromSeconds(seconds) : fallback;

    private static int? PositiveInt(JsonElement parent, string name, string field, int max = int.MaxValue)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < 1 || number > max)
        {
            throw new InvalidSettingException(max == int.MaxValue
                ? $"{field}: must be a whole number greater than 0"
                : $"{field}: must be a whole number from 1 to {max.ToString(CultureInfo.InvariantCulture)}");
        }

        return number;
    }

    /// <summary>A whole number from <paramref name="min"/> to <paramref name="max"/>, or null when absent (AD-10's ranged fields).</summary>
    private static int? RangedInt(JsonElement parent, string name, string field, int min, int max)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max)
        {
            throw new InvalidSettingException(
                $"{field}: must be a whole number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}");
        }

        return number;
    }

    private static string? AbsolutePath(JsonElement parent, string name, string field) =>
        parent.TryGetProperty(name, out var value) ? RequireAbsolutePath(value, field) : null;

    private static string RequireAbsolutePath(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidSettingException($"{field}: must be a string");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || !System.IO.Path.IsPathFullyQualified(text))
        {
            throw new InvalidSettingException($"{field}: must be an absolute path");
        }

        return text;
    }

    /// <summary>Carries a field-naming reason out of validation. Never escapes the store.</summary>
    private sealed class InvalidSettingException(string reason) : Exception(reason);
}
