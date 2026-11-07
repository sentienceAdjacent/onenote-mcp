using System.Globalization;
using System.Text.Json;

namespace OneNoteMcp.Core.Settings;

/// <summary>
/// The one Core type that owns <c>%APPDATA%\OneNoteMcp\settings.json</c> (AD-10). This is the read side only:
/// every <see cref="Read"/> reads the file again and returns a new snapshot, nothing is cached, and the store
/// never writes the file. <c>notebooks{}</c> (grants) is left to the access stories. Unknown keys are ignored.
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
            document = JsonDocument.Parse(bytes);
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

    private static SettingsSnapshot Validate(string path, JsonElement root)
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

        if (schemaVersion > SettingsSnapshot.KnownSchemaVersion)
        {
            // A newer shape may differ in any field, so none of it is read (AD-10, AD-19).
            return SettingsSnapshot.Defaults(path, SettingsStatus.NewerSchema, "schemaVersion: newer than this server supports");
        }

        if (schemaVersion < SettingsSnapshot.KnownSchemaVersion)
        {
            throw new InvalidSettingException("schemaVersion: must be 1");
        }

        var allowSilentActivation = false;
        if (root.TryGetProperty("allowSilentActivation", out var silent))
        {
            allowSilentActivation = silent.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidSettingException("allowSilentActivation: must be true or false"),
            };
        }

        var timeouts = Timeouts.Default;
        if (root.TryGetProperty("timeouts", out var t))
        {
            RequireObject(t, "timeouts");
            timeouts = new Timeouts(
                Seconds(t, "callSeconds", timeouts.Call),
                Seconds(t, "publishSeconds", timeouts.Publish),
                Seconds(t, "activationSeconds", timeouts.Activation),
                Seconds(t, "idleReleaseSeconds", timeouts.IdleRelease));
        }

        var journal = JournalSettings.Default();
        if (root.TryGetProperty("journal", out var j))
        {
            RequireObject(j, "journal");
            journal = new JournalSettings(
                AbsolutePath(j, "path", "journal.path") ?? journal.Path,
                PositiveInt(j, "retentionDays", "journal.retentionDays") ?? journal.RetentionDays,
                PositiveInt(j, "maxSizeMB", "journal.maxSizeMB") ?? journal.MaxSizeMB);
        }

        var results = ResultSettings.Default;
        if (root.TryGetProperty("results", out var r))
        {
            RequireObject(r, "results");
            results = new ResultSettings(
                RangedInt(r, "inlineMaxChars", "results.inlineMaxChars", ResultSettings.InlineMaxCharsMin, ResultSettings.InlineMaxCharsMax) ?? results.InlineMaxChars,
                RangedInt(r, "ttlMinutes", "results.ttlMinutes", ResultSettings.TtlMinutesMin, ResultSettings.TtlMinutesMax) ?? results.TtlMinutes,
                RangedInt(r, "maxStoreMB", "results.maxStoreMB", ResultSettings.MaxStoreMBMin, ResultSettings.MaxStoreMBMax) ?? results.MaxStoreMB);
        }

        var exportRoots = ExportRoots.Default();
        if (root.TryGetProperty("exportRoots", out var roots))
        {
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

            exportRoots = new ExportRoots(paths.AsReadOnly());
        }

        return new SettingsSnapshot(path, SettingsStatus.Ok, reason: null, allowSilentActivation, timeouts, journal, results, exportRoots);
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
