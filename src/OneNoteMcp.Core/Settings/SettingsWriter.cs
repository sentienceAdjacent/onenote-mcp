using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneNoteMcp.Core.Settings;

/// <summary>
/// The write side of <c>settings.json</c> (AD-10), for grant writers only: elicitation and the CLI (AD-21). Every change
/// is one read-modify-write inside the named mutex <c>Local\OneNoteMcp.Settings</c>: re-read, change, write a temporary
/// file beside it, then <c>File.Replace</c>. Members it doesn't know are kept as they were. Inside the same write it
/// drops <c>protectedSections{}</c> entries whose <c>expiresAt</c> has passed, the one automatic pruning AD-10 allows. The
/// mutex is held only for the file work, never across a prompt or a dialog.
/// </summary>
public sealed class SettingsWriter
{
    public const string MutexName = @"Local\OneNoteMcp.Settings";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly SettingsStore store;
    private readonly TimeProvider clock;
    private readonly TimeSpan lockTimeout;

    public SettingsWriter(SettingsStore store, TimeProvider clock, TimeSpan? lockTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        this.store = store;
        this.clock = clock;
        this.lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(10);
    }

    public SettingsStore Store => store;

    /// <summary>
    /// Stores (or replaces) the read grant <paramref name="grant"/> under its key; an entry whose key differs only in case
    /// is replaced too. Throws <see cref="ErrorCode.SettingsInvalid"/> when the file is invalid or newer than this server,
    /// and <see cref="ErrorCode.ComBusy"/> when another process holds the settings lock too long.
    /// </summary>
    public void SaveReadGrant(ProtectedSectionGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        Change(allowDuplicateKeys: false, sections =>
        {
            RemoveKey(sections, grant.Key);
            var entry = new JsonObject
            {
                ["name"] = grant.Name,
                ["notebookKey"] = grant.NotebookKey,
                ["idAtGrant"] = grant.IdAtGrant,
                ["grantedAt"] = Format(grant.GrantedAt),
                ["grantedVia"] = grant.GrantedVia,
            };
            if (grant.ExpiresAt is { } expires)
            {
                entry["expiresAt"] = Format(expires);
            }

            sections[grant.Key] = entry;
            return true;
        });
    }

    /// <summary>
    /// Removes every read grant whose key equals <paramref name="sectionKey"/> ignoring case. Runs on a file that is
    /// invalid only because of case-only duplicate keys, so the user can remove one (AD-6). Returns how many it removed.
    /// </summary>
    public int RevokeReadGrant(string sectionKey)
    {
        ArgumentNullException.ThrowIfNull(sectionKey);
        var removed = 0;
        Change(allowDuplicateKeys: true, sections =>
        {
            removed = RemoveKey(sections, sectionKey);
            return removed > 0;
        });
        return removed;
    }

    /// <summary>UTC ISO-8601 with milliseconds and <c>Z</c> (Consistency Conventions).</summary>
    public static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static int RemoveKey(JsonObject sections, string key)
    {
        var matches = sections.Select(p => p.Key).Where(k => Access.GrantKeys.Comparer.Equals(k, key)).ToList();
        foreach (var match in matches)
        {
            sections.Remove(match);
        }

        return matches.Count;
    }

    private void Change(bool allowDuplicateKeys, Func<JsonObject, bool> change)
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(lockTimeout);
        }
        catch (AbandonedMutexException)
        {
            // A writer that died holding it: the lock is ours (as AD-18 treats the write lock).
            owned = true;
        }

        if (!owned)
        {
            throw new OneNoteException(ErrorCode.ComBusy, "Another OneNoteMcp process is writing settings.json.", userAction: "Retry in a moment.");
        }

        try
        {
            ChangeLocked(allowDuplicateKeys, change);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private void ChangeLocked(bool allowDuplicateKeys, Func<JsonObject, bool> change)
    {
        // Re-read inside the lock, so a change another process made since is never lost.
        var snapshot = store.Read();
        var canWrite = snapshot.Status is SettingsStatus.Ok or SettingsStatus.Absent
            || (allowDuplicateKeys && snapshot.DuplicateGrantKeysOnly);
        if (!canWrite)
        {
            snapshot.EnsureWritable();
            throw new OneNoteException(ErrorCode.SettingsInvalid, $"settings.json can't be written ({snapshot.Reason}).");
        }

        var root = snapshot.Status == SettingsStatus.Absent ? new JsonObject { ["schemaVersion"] = SettingsSnapshot.KnownSchemaVersion } : Load();
        if (root["protectedSections"] is not JsonObject sections)
        {
            sections = [];
            root["protectedSections"] = sections;
        }

        var changed = change(sections);
        changed |= PruneExpired(sections);
        if (!changed)
        {
            return;
        }

        Write(root);
    }

    private JsonObject Load()
    {
        try
        {
            using var stream = new FileStream(store.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) as JsonObject
                ?? throw new OneNoteException(ErrorCode.SettingsInvalid, "settings.json: (root) must be a JSON object.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            // The message may quote the file; name only the failure (AD-13).
            throw new OneNoteException(ErrorCode.SettingsInvalid, $"settings.json couldn't be read for writing ({ex.GetType().Name}).", innerException: ex);
        }
    }

    /// <summary>Drops entries whose <c>expiresAt</c> has passed. An entry it can't read is left alone.</summary>
    private bool PruneExpired(JsonObject sections)
    {
        var now = clock.GetUtcNow();
        var expired = sections
            .Where(p => p.Value is JsonObject entry
                && entry["expiresAt"] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var end)
                && now >= end)
            .Select(p => p.Key)
            .ToList();
        foreach (var key in expired)
        {
            sections.Remove(key);
        }

        return expired.Count > 0;
    }

    private void Write(JsonObject root)
    {
        var path = store.Path;
        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, $".settings.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temp, root.ToJsonString(Indented) + Environment.NewLine, new System.Text.UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new OneNoteException(ErrorCode.SettingsInvalid, $"settings.json couldn't be written ({ex.GetType().Name}).", innerException: ex, userAction: "Check that settings.json and its folder are writable, then retry.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stray temporary file is harmless.
        }
    }
}
