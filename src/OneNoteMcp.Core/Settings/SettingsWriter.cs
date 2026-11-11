using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OneNoteMcp.Core.Settings;

/// <summary>
/// The write side of <c>settings.json</c> (AD-10), for grant writers only: elicitation and the CLI, for read grants
/// (<c>protectedSections{}</c>, AD-21) and write grants (<c>notebooks{}</c>, AD-6). Every change
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
        Change(allowDuplicateKeys: false, root =>
        {
            var sections = Area(root, ProtectedSectionsArea);
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
        Change(allowDuplicateKeys: true, root =>
        {
            removed = RemoveKey(Area(root, ProtectedSectionsArea), sectionKey);
            return removed > 0;
        });
        return removed;
    }

    /// <summary>
    /// Stores (or replaces) the write grant <paramref name="grant"/> in <c>notebooks{}</c> under its key (AD-6); an entry
    /// whose key differs only in case is replaced too. Granting again keeps an existing <c>permanentDelete: true</c>,
    /// which only its own grant gives or takes away. Same failures as <see cref="SaveReadGrant"/>.
    /// </summary>
    public void SaveWriteGrant(NotebookGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        Change(allowDuplicateKeys: false, root =>
        {
            var notebooks = Area(root, NotebooksArea);
            var keptPermanentDelete = notebooks
                .Where(p => Access.GrantKeys.Comparer.Equals(p.Key, grant.Key))
                .Any(p => p.Value is JsonObject old && old["permanentDelete"] is JsonValue v && v.TryGetValue<bool>(out var b) && b);
            RemoveKey(notebooks, grant.Key);
            notebooks[grant.Key] = new JsonObject
            {
                ["name"] = grant.Name,
                ["idAtGrant"] = grant.IdAtGrant,
                ["write"] = grant.Write,
                ["permanentDelete"] = grant.PermanentDelete || keptPermanentDelete,
                ["grantedAt"] = Format(grant.GrantedAt),
                ["grantedVia"] = grant.GrantedVia,
            };
            return true;
        });
    }

    private const string ProtectedSectionsArea = "protectedSections";
    private const string NotebooksArea = "notebooks";

    /// <summary>The root's <paramref name="name"/> object, added when absent.</summary>
    private static JsonObject Area(JsonObject root, string name)
    {
        if (root[name] is not JsonObject area)
        {
            area = [];
            root[name] = area;
        }

        return area;
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
        var changed = change(root);
        if (root[ProtectedSectionsArea] is JsonObject sections)
        {
            changed |= PruneExpired(sections);
        }
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
            return JsonNode.Parse(stream, documentOptions: SettingsJson.Options(allowDuplicateProperties: false)) as JsonObject
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
                && SettingsJson.TryParseUtc(text, out var end)
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
