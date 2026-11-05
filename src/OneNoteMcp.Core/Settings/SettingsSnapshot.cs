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
        ExportRoots exportRoots)
    {
        Path = path;
        Status = status;
        Reason = reason;
        AllowSilentActivation = allowSilentActivation;
        Timeouts = timeouts;
        Journal = journal;
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

    public ExportRoots ExportRoots { get; }

    /// <summary>Every default, with the given status. Used for an absent, invalid or newer file.</summary>
    internal static SettingsSnapshot Defaults(string path, SettingsStatus status, string? reason = null) =>
        new(path, status, reason, allowSilentActivation: false, Timeouts.Default, JournalSettings.Default(), ExportRoots.Default());

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
