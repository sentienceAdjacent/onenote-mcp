namespace OneNoteMcp.Core.Settings;

/// <summary>How the last read of <c>settings.json</c> went (AD-10).</summary>
public enum SettingsStatus
{
    /// <summary>The file was read and every field it sets is valid.</summary>
    Ok,

    /// <summary>There is no file; every value is the default.</summary>
    Absent,

    /// <summary>The file is malformed or unreadable. Reads continue on the defaults; writes fail with <c>SettingsInvalid</c>.</summary>
    Invalid,

    /// <summary>The file's <c>schemaVersion</c> is newer than this server knows. The server runs read-only on the defaults.</summary>
    NewerSchema,
}

/// <summary>COM timeouts (AD-3). JSON shape: <c>"timeouts": {"callSeconds", "publishSeconds", "activationSeconds", "idleReleaseSeconds"}</c>.</summary>
public sealed record Timeouts(TimeSpan Call, TimeSpan Publish, TimeSpan Activation, TimeSpan IdleRelease)
{
    public static Timeouts Default { get; } = new(
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(300),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(300));
}

/// <summary>Recovery journal location and retention (AD-8).</summary>
public sealed record JournalSettings(string Path, int RetentionDays, int MaxSizeMB)
{
    public static JournalSettings Default() => new(
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneNoteMcp", "journal"),
        RetentionDays: 30,
        MaxSizeMB: 2048);
}

/// <summary>
/// Large-result delivery (AD-20). JSON shape: <c>"results": {"inlineMaxChars", "ttlMinutes", "maxStoreMB"}</c>, whole numbers
/// in the ranges AD-10 sets. A value out of range makes the file invalid, and reads then use these defaults.
/// </summary>
/// <param name="InlineMaxChars">The largest text block (AD-9) returned whole; a bigger result is stored and returned as an index.</param>
/// <param name="TtlMinutes">How long a stored result lives after its last read.</param>
/// <param name="MaxStoreMB">The result store's cap, counted as UTF-16 bytes; past it the least recently read results go first.</param>
public sealed record ResultSettings(int InlineMaxChars, int TtlMinutes, int MaxStoreMB)
{
    public const int InlineMaxCharsMin = 20_000;
    public const int InlineMaxCharsMax = 380_000;
    public const int TtlMinutesMin = 1;
    public const int TtlMinutesMax = 1_440;
    public const int MaxStoreMBMin = 16;
    public const int MaxStoreMBMax = 4_096;

    public static ResultSettings Default { get; } = new(InlineMaxChars: 100_000, TtlMinutes: 30, MaxStoreMB: 256);

    public TimeSpan Ttl => TimeSpan.FromMinutes(TtlMinutes);

    public long MaxStoreBytes => MaxStoreMB * 1024L * 1024L;
}

/// <summary>The folders an <c>ExternalWrite</c> target must sit under (AD-11). Every entry is an absolute path.</summary>
public sealed record ExportRoots(IReadOnlyList<string> Paths)
{
    public static ExportRoots Default() => new(
        [System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OneNote Exports")]);
}

/// <summary>
/// One immutable read of <c>settings.json</c> (AD-10). When the status is not <see cref="SettingsStatus.Ok"/>
/// every value is the default. <see cref="Reason"/> names the offending field and never quotes the file's content.
/// </summary>
public sealed class SettingsSnapshot
{
    public const int KnownSchemaVersion = 1;

    internal SettingsSnapshot(
        string path,
        SettingsStatus status,
        string? reason,
        bool allowSilentActivation,
        Timeouts timeouts,
        JournalSettings journal,
        ResultSettings results,
        ExportRoots exportRoots)
    {
        Path = path;
        Status = status;
        Reason = reason;
        AllowSilentActivation = allowSilentActivation;
        Timeouts = timeouts;
        Journal = journal;
        Results = results;
        ExportRoots = exportRoots;
    }

    /// <summary>The settings file this snapshot was read from.</summary>
    public string Path { get; }

    public SettingsStatus Status { get; }

    /// <summary>Why the file is <see cref="SettingsStatus.Invalid"/> or <see cref="SettingsStatus.NewerSchema"/>; null otherwise.</summary>
    public string? Reason { get; }

    /// <summary>True when a newer server wrote the file: this server must not write anything (AD-10, AD-19).</summary>
    public bool ReadOnly => Status == SettingsStatus.NewerSchema;

    public bool AllowSilentActivation { get; }

    public Timeouts Timeouts { get; }

    public JournalSettings Journal { get; }

    public ResultSettings Results { get; }

    public ExportRoots ExportRoots { get; }

    /// <summary>Every default, with the given status. Used for an absent, invalid or newer file.</summary>
    internal static SettingsSnapshot Defaults(string path, SettingsStatus status, string? reason = null) =>
        new(path, status, reason, allowSilentActivation: false, Timeouts.Default, JournalSettings.Default(), ResultSettings.Default, ExportRoots.Default());

    /// <summary>Throws <see cref="ErrorCode.SettingsInvalid"/> when the file is invalid or newer than this server (AD-10).</summary>
    public void EnsureWritable()
    {
        switch (Status)
        {
            case SettingsStatus.Invalid:
                throw new OneNoteException(ErrorCode.SettingsInvalid, $"settings.json is invalid ({Reason}), so writes are disabled.");
            case SettingsStatus.NewerSchema:
                throw new OneNoteException(
                    ErrorCode.SettingsInvalid,
                    "settings.json was written by a newer version of OneNoteMcp, so this server runs read-only. Update OneNoteMcp to write.");
        }
    }
}
