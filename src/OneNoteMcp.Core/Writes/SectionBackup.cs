using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Settings;

namespace OneNoteMcp.Core.Writes;

/// <summary>What <see cref="SectionBackup.TakeAsync"/> stored: the <c>onenoteBackup</c> entry and its backup's file name (no folder).</summary>
public sealed record SectionBackupResult(JournalWritten Entry, string BackupFile);

/// <summary>
/// AD-7 step 6 for a section's permanent delete (AD-8 Backups; story 5.6): a <c>Publish</c> <c>pfOneNote</c> backup in the
/// journal's <c>backups</c> folder, then its <c>onenoteBackup</c> entry, in that order, all before the COM delete.
/// <list type="number">
/// <item>The entry's name is fixed first (<see cref="JournalStore.EntryName"/>), and the journal folder read once, so the
/// backup and the entry land in the same journal under one name.</item>
/// <item>The <c>backups</c> folder is created (<c>Publish</c> into a missing folder raises a modal and blocks).</item>
/// <item><c>Publish</c> to <c>backups\&lt;entry name&gt;.one</c>, a new file (<c>Publish</c> refuses an existing one), under
/// <c>timeouts.publishSeconds</c>, never retried.</item>
/// <item>The backup counts as done only once the file exists, isn't empty, and its size is unchanged across two polls
/// <see cref="PollInterval"/> apart, waited for up to <c>timeouts.publishSeconds</c> from the <c>Publish</c> call's start,
/// within the pipeline's deadline (the call's token).</item>
/// <item>Then the entry, with <c>backupFile</c>, written last so an entry never names a file that isn't there; a crash in
/// between leaves an orphan backup the pruner removes after retention.</item>
/// </list>
/// Any failure of the folder, the <c>Publish</c> call (its HRESULT kept) or the wait is <see cref="ErrorCode.BackupFailed"/>,
/// and the delete doesn't run. The entry's own write failure stays <see cref="ErrorCode.JournalFailed"/>. Messages name the
/// section's ID and the failure's code or kind, never a path (AD-9, AD-13). The pipeline's deadline or a cancel stays an
/// <see cref="OperationCanceledException"/>, which the pipeline reports.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SectionBackup
{
    /// <summary>The default gap between the polls that check a published file's size.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IOneNoteGateway gateway;
    private readonly JournalStore store;
    private readonly Func<SettingsSnapshot> settings;
    private readonly TimeProvider clock;

    /// <param name="gateway">Publishes the section.</param>
    /// <param name="store">The journal the entry goes to; its folder holds <c>backups</c>.</param>
    /// <param name="settings">Reads <c>settings.json</c> for <c>timeouts.publishSeconds</c>, once per backup.</param>
    /// <param name="clock">The clock the wait runs on.</param>
    /// <param name="pollInterval">The gap between size polls; <see cref="DefaultPollInterval"/> when null.</param>
    public SectionBackup(IOneNoteGateway gateway, JournalStore store, Func<SettingsSnapshot> settings, TimeProvider? clock = null, TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        this.gateway = gateway;
        this.store = store;
        this.settings = settings;
        this.clock = clock ?? TimeProvider.System;
        PollInterval = pollInterval ?? DefaultPollInterval;
    }

    public TimeSpan PollInterval { get; }

    /// <summary>
    /// Publishes section <paramref name="sectionId"/>, waits for its file, then stores <paramref name="entry"/> (an
    /// unstored <c>onenoteBackup</c> entry) with <c>backupFile</c> set. Returns what was stored.
    /// </summary>
    public async Task<SectionBackupResult> TakeAsync(string sectionId, JournalEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        sectionId ??= string.Empty;

        var folder = store.CurrentFolder();
        var backupName = JournalFiles.BackupName(JournalStore.EntryName(entry));
        string target;
        try
        {
            var backups = Path.Combine(folder, JournalFiles.BackupsFolderName);
            Directory.CreateDirectory(backups);
            target = Path.Combine(Path.GetFullPath(backups), backupName);
            if (File.Exists(target))
            {
                throw Failed(sectionId, "a file of the backup's name already exists");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw Failed(sectionId, $"the backup folder couldn't be prepared ({ex.GetType().Name})", ex);
        }

        var started = clock.GetTimestamp();
        var limit = settings().Timeouts.Publish;
        try
        {
            await gateway.PublishAsync(sectionId, target, ct).ConfigureAwait(false);
        }
        catch (OneNoteException ex) when (!ct.IsCancellationRequested)
        {
            throw Failed(
                sectionId,
                ex.ComHResult is { } hr
                    ? string.Create(CultureInfo.InvariantCulture, $"Publish failed with {ex.Code} (0x{hr:X8})")
                    : $"Publish failed with {ex.Code}",
                ex,
                ex.ComHResult);
        }

        await WaitForFileAsync(sectionId, target, started, limit, ct).ConfigureAwait(false);

        var written = store.WriteTo(folder, entry with { BackupFile = backupName });
        return new SectionBackupResult(written, backupName);
    }

    /// <summary>
    /// Polls <paramref name="target"/> until it exists, isn't empty and kept its size across two polls, or
    /// <paramref name="limit"/> has passed since <paramref name="started"/> (<see cref="ErrorCode.BackupFailed"/>).
    /// </summary>
    private async Task WaitForFileAsync(string sectionId, string target, long started, TimeSpan limit, CancellationToken ct)
    {
        long? last = null;
        while (true)
        {
            var size = Size(target);
            if (size is > 0 && size == last)
            {
                return;
            }

            if (clock.GetElapsedTime(started) >= limit)
            {
                throw Failed(sectionId, size switch
                {
                    null => "its file never appeared",
                    0 => "its file stayed empty",
                    _ => "its file was still growing",
                });
            }

            last = size;
            await Task.Delay(PollInterval, clock, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The file's size, or null when it isn't there or can't be read.</summary>
    private static long? Size(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static OneNoteException Failed(string sectionId, string why, Exception? inner = null, int? hresult = null) =>
        new(
            ErrorCode.BackupFailed,
            $"The backup of section {sectionId} failed: {why}. Nothing was deleted.",
            hresult,
            inner,
            userAction: "Nothing was deleted. Check that no dialog is open in OneNote and that the journal folder (journal.path in settings.json) is writable and has free space, then retry.");
}
