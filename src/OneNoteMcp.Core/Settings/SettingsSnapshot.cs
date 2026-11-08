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

/// <summary>
/// The search page-text cache (AD-12). JSON shape: <c>"search": {"cacheMaxMB"}</c>, a whole number in the range AD-10 sets.
/// A value out of range makes the file invalid, and reads then use this default.
/// </summary>
/// <param name="CacheMaxMB">The cache's cap, counted as UTF-16 bytes; past it the least recently used pages go first. 0 turns the cache off.</param>
public sealed record SearchSettings(int CacheMaxMB)
{
    public const int CacheMaxMBMin = 0;
    public const int CacheMaxMBMax = 1_024;

    public static SearchSettings Default { get; } = new(CacheMaxMB: 64);

    public long CacheMaxBytes => CacheMaxMB * 1024L * 1024L;
}

/// <summary>
/// Read grants (AD-21). JSON shape: <c>"readGrants": {"onceMinutes"}</c>, a whole number in the range AD-10 sets. A value
/// out of range makes the file invalid, and reads then use this default.
/// </summary>
/// <param name="OnceMinutes">How long a <c>once</c> grant from the dialog covers its section, from when the user's answer arrives.</param>
public sealed record ReadGrantSettings(int OnceMinutes)
{
    public const int OnceMinutesMin = 1;
    public const int OnceMinutesMax = 60;

    public static ReadGrantSettings Default { get; } = new(OnceMinutes: 5);

    public TimeSpan OnceWindow => TimeSpan.FromMinutes(OnceMinutes);
}

/// <summary>The folders an <c>ExternalWrite</c> target must sit under (AD-11). Every entry is an absolute path.</summary>
public sealed record ExportRoots(IReadOnlyList<string> Paths)
{
    public static ExportRoots Default() => new(
        [System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OneNote Exports")]);
}

/// <summary>
/// One stored read grant, an entry of <c>protectedSections{}</c> (AD-10, AD-21), keyed by <see cref="Key"/>, the
/// section's <c>SectionKey</c>. <see cref="Name"/>, <see cref="NotebookKey"/> and <see cref="IdAtGrant"/> are for
/// information only and never compared for access. No <see cref="ExpiresAt"/> means <c>untilRevoked</c>; with one, an
/// <c>hours</c> grant. JSON shape: <c>{name, notebookKey, idAtGrant, grantedAt, grantedVia, expiresAt?}</c>, dates UTC ISO-8601.
/// </summary>
public sealed record ProtectedSectionGrant(
    string Key,
    string Name,
    string NotebookKey,
    string IdAtGrant,
    DateTimeOffset GrantedAt,
    string GrantedVia,
    DateTimeOffset? ExpiresAt = null)
{
    /// <summary><c>hours</c> or <c>untilRevoked</c>.</summary>
    public string Lifetime => ExpiresAt is null ? Access.ReadGrantLifetimes.UntilRevoked : Access.ReadGrantLifetimes.Hours;

    /// <summary>True once an <c>hours</c> grant's <see cref="ExpiresAt"/> has passed.</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } end && now >= end;
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
        SearchSettings search,
        ExportRoots exportRoots,
        IReadOnlyList<ProtectedSectionGrant>? protectedSectionEntries = null,
        bool duplicateGrantKeysOnly = false,
        ReadGrantSettings? readGrants = null)
    {
        Path = path;
        Status = status;
        Reason = reason;
        AllowSilentActivation = allowSilentActivation;
        Timeouts = timeouts;
        Journal = journal;
        Results = results;
        Search = search;
        ExportRoots = exportRoots;
        ProtectedSectionEntries = protectedSectionEntries ?? [];
        DuplicateGrantKeysOnly = duplicateGrantKeysOnly;
        ReadGrants = readGrants ?? ReadGrantSettings.Default;

        // Stored read grants count as absent unless the file is valid (AD-21).
        var grants = new Dictionary<string, ProtectedSectionGrant>(Access.GrantKeys.Comparer);
        if (status == SettingsStatus.Ok)
        {
            foreach (var grant in ProtectedSectionEntries)
            {
                grants[grant.Key] = grant;
            }
        }

        ProtectedSections = grants;
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

    public SearchSettings Search { get; }

    /// <summary><c>readGrants{}</c> (AD-10, AD-21): the <c>once</c> grant's window.</summary>
    public ReadGrantSettings ReadGrants { get; }

    public ExportRoots ExportRoots { get; }

    /// <summary>
    /// The stored read grants by <c>SectionKey</c>, compared ignoring case (AD-21). Empty unless the status is
    /// <see cref="SettingsStatus.Ok"/>: an invalid or newer file's grants count as absent.
    /// </summary>
    public IReadOnlyDictionary<string, ProtectedSectionGrant> ProtectedSections { get; }

    /// <summary>
    /// Every <c>protectedSections{}</c> entry as written, duplicates included. Filled when the file is valid, or when
    /// <see cref="DuplicateGrantKeysOnly"/>, so <c>grant --revoke</c> can remove one (AD-6); empty otherwise.
    /// </summary>
    public IReadOnlyList<ProtectedSectionGrant> ProtectedSectionEntries { get; }

    /// <summary>
    /// True when the file is <see cref="SettingsStatus.Invalid"/> only because two <c>protectedSections{}</c> keys differ
    /// only in case (AD-6). The CLI's revoke still runs on such a file.
    /// </summary>
    public bool DuplicateGrantKeysOnly { get; }

    /// <summary>Every default, with the given status. Used for an absent, invalid or newer file.</summary>
    internal static SettingsSnapshot Defaults(string path, SettingsStatus status, string? reason = null) =>
        new(path, status, reason, allowSilentActivation: false, Timeouts.Default, JournalSettings.Default(), ResultSettings.Default, SearchSettings.Default, ExportRoots.Default());

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
