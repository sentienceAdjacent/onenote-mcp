using OneNoteMcp.Core.Settings;

namespace OneNoteMcp.Core.Journal;

/// <summary>
/// What one prune did, in counts and sizes only (AD-13). <see cref="Kept"/> and <see cref="KeptBytes"/> are the entries
/// left; <see cref="Failed"/> counts files that couldn't be removed (in use, access denied), which stay for the next prune.
/// <see cref="SkippedReason"/> is set, and nothing was removed, when <c>settings.json</c> is invalid
/// (<c>settingsInvalid</c>) or from a newer server (<c>settingsNewerSchema</c>).
/// </summary>
public sealed record JournalPruneResult(
    int Kept,
    long KeptBytes,
    int RemovedByAge,
    int RemovedBySize,
    long RemovedBytes,
    int RemovedTemp,
    int Failed)
{
    public string? SkippedReason { get; init; }
}

/// <summary>
/// Prunes the journal (AD-8): each process runs it at startup and then daily. <c>journal{}</c> is re-read from
/// <c>settings.json</c> at every prune. In order:
/// <list type="number">
/// <item>Retention: every entry whose name's timestamp is more than <c>journal.retentionDays</c> old goes.</item>
/// <item>Size cap: while the entries left total more than <c>journal.maxSizeMB</c>, the oldest goes, but never the newest
/// entry, so the snapshot just taken survives a cap smaller than itself.</item>
/// <item>Leftovers: a temporary file (<c>.{name}.tmp</c>) whose name's timestamp is more than a day old goes.</item>
/// </list>
/// A <c>PermanentDelete</c> backup in the <c>backups</c> folder (AD-8, story 5.6) belongs to the entry of the same name: its
/// size counts toward the cap with its entry's, and it goes when its entry goes. A backup with no entry (left by a delete
/// that failed after <c>Publish</c>, or a crash before the entry was written) goes once its name's timestamp is past the
/// retention age, and counts in <see cref="JournalPruneResult.RemovedByAge"/>.
/// Only journal-named files at the folder's top level, and backup-named files at the top level of its <c>backups</c>
/// folder, are ever touched (<see cref="JournalFiles"/>). Other processes write
/// and prune the same folder, so a file that vanishes meanwhile is skipped, and a file that can't be removed is counted
/// in <see cref="JournalPruneResult.Failed"/> and left. It never throws for a file or the folder; a missing folder is an
/// empty journal.
/// </summary>
public sealed class JournalPruner
{
    /// <summary>How old a temporary file must be before a prune removes it: no write takes this long.</summary>
    public static readonly TimeSpan TempMaxAge = TimeSpan.FromDays(1);

    private const long BytesPerMB = 1024L * 1024L;

    private readonly Func<SettingsSnapshot> settings;
    private readonly TimeProvider clock;

    /// <param name="settings">Reads <c>settings.json</c>, once per prune, for <c>journal{}</c>.</param>
    /// <param name="clock">The clock retention is measured on.</param>
    public JournalPruner(Func<SettingsSnapshot> settings, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Test seam: runs before each removal with the file's path, so a test can make a file vanish mid-prune.</summary>
    internal Action<string>? BeforeRemove { get; set; }

    public JournalPruneResult Prune()
    {
        // An invalid or newer settings.json reads as the defaults, which may be shorter or elsewhere than the user's
        // journal{}: removing on them could delete what the user meant to keep, irreversibly. Skip until it's fixed (AD-10).
        var snapshot = settings();
        if (snapshot.Status is SettingsStatus.Invalid or SettingsStatus.NewerSchema)
        {
            return new JournalPruneResult(0, 0, 0, 0, 0, 0, 0)
            {
                SkippedReason = snapshot.Status == SettingsStatus.Invalid ? "settingsInvalid" : "settingsNewerSchema",
            };
        }

        var journal = snapshot.Journal;
        var now = clock.GetUtcNow().UtcDateTime;
        var entries = new List<(string Path, string Name, DateTime TakenAtUtc, long Bytes, string? Backup)>();
        var temps = new List<(string Path, DateTime TakenAtUtc)>();

        // Backups, by the name of the entry each belongs to (AD-8).
        var backups = new Dictionary<string, (string Path, DateTime TakenAtUtc, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in List(Path.Combine(journal.Path, JournalFiles.BackupsFolderName)))
        {
            var name = Path.GetFileName(path);
            if (JournalFiles.EntryNameOfBackup(name) is { } entryName
                && JournalFiles.TryParseTimestamp(name, out var takenAtUtc)
                && Size(path) is { } bytes)
            {
                backups[entryName] = (path, takenAtUtc, bytes);
            }
        }

        foreach (var path in List(journal.Path))
        {
            var name = Path.GetFileName(path);
            if (!JournalFiles.TryParseTimestamp(name, out var takenAtUtc))
            {
                continue;
            }

            if (JournalFiles.IsTemp(name))
            {
                temps.Add((path, takenAtUtc));
            }
            else if (JournalFiles.IsEntry(name) && Size(path) is { } bytes)
            {
                if (backups.Remove(name, out var backup))
                {
                    entries.Add((path, name, takenAtUtc, bytes + backup.Bytes, backup.Path));
                }
                else
                {
                    entries.Add((path, name, takenAtUtc, bytes, null));
                }
            }
        }

        // Oldest first; the name breaks a tie, so every process orders the same way.
        entries.Sort((a, b) => a.TakenAtUtc != b.TakenAtUtc
            ? a.TakenAtUtc.CompareTo(b.TakenAtUtc)
            : string.CompareOrdinal(a.Name, b.Name));

        int removedByAge = 0, removedBySize = 0, removedTemp = 0, failed = 0;
        long removedBytes = 0;

        // 1. Retention, by the name's timestamp.
        // A retention longer than the calendar reaches back keeps everything rather than overflowing.
        var cutoff = journal.RetentionDays < (now - DateTime.MinValue).TotalDays
            ? now - TimeSpan.FromDays(journal.RetentionDays)
            : DateTime.MinValue;
        var kept = new List<(string Path, string Name, DateTime TakenAtUtc, long Bytes, string? Backup)>();
        foreach (var entry in entries)
        {
            if (entry.TakenAtUtc >= cutoff)
            {
                kept.Add(entry);
            }
            else if (Remove(entry.Path) is { } removed)
            {
                failed += RemoveBackup(entry.Backup);
                if (removed)
                {
                    removedByAge++;
                    removedBytes += entry.Bytes;
                }
            }
            else
            {
                failed++;
                kept.Add(entry);
            }
        }

        // A backup whose entry is gone, once it is past the retention age.
        foreach (var orphan in backups.Values.Where(b => b.TakenAtUtc < cutoff))
        {
            switch (Remove(orphan.Path))
            {
                case true:
                    removedByAge++;
                    removedBytes += orphan.Bytes;
                    break;
                case null:
                    failed++;
                    break;
            }
        }

        // 2. The size cap, oldest first, never the newest entry.
        var cap = journal.MaxSizeMB * BytesPerMB;
        var total = kept.Sum(e => e.Bytes);
        var index = 0;
        while (total > cap && index < kept.Count - 1)
        {
            var entry = kept[index];
            switch (Remove(entry.Path))
            {
                case true:
                    failed += RemoveBackup(entry.Backup);
                    removedBySize++;
                    removedBytes += entry.Bytes;
                    total -= entry.Bytes;
                    kept.RemoveAt(index);
                    break;
                case false:
                    // Vanished: another process pruned it.
                    failed += RemoveBackup(entry.Backup);
                    total -= entry.Bytes;
                    kept.RemoveAt(index);
                    break;
                default:
                    failed++;
                    index++;
                    break;
            }
        }

        // 3. Temporary files a write left behind.
        foreach (var temp in temps.Where(t => t.TakenAtUtc < now - TempMaxAge))
        {
            switch (Remove(temp.Path))
            {
                case true:
                    removedTemp++;
                    break;
                case null:
                    failed++;
                    break;
            }
        }

        return new JournalPruneResult(kept.Count, kept.Sum(e => e.Bytes), removedByAge, removedBySize, removedBytes, removedTemp, failed);
    }

    /// <summary>Removes an entry's backup, if it has one, with its entry; returns 1 when it couldn't be removed (it is then an orphan for a later prune).</summary>
    private int RemoveBackup(string? backup) => backup is not null && Remove(backup) is null ? 1 : 0;

    /// <summary>The folder's top-level files; none when it doesn't exist or can't be listed.</summary>
    private static List<string> List(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>The file's size, or null when it vanished or can't be read.</summary>
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

    /// <summary>True when removed; false when it had already vanished; null when it couldn't be removed.</summary>
    private bool? Remove(string path)
    {
        try
        {
            BeforeRemove?.Invoke(path);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
