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
/// mutex is held only for the file work, never across a prompt or a dialog. A save refuses a file with any duplicate key;
/// a revoke runs on a file invalid only for duplicate grant keys and removes every entry equal to its key, exact
/// duplicates included (2.3 plan Q7). <see cref="RunLocked"/> lends the mutex to <c>uninstall --purge</c> (retro R4).
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
        Change(root =>
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
    /// Removes every read grant whose key equals <paramref name="sectionKey"/>, exactly or ignoring case. Runs on a file
    /// that is invalid only because of duplicate grant keys, so the user can fix it (AD-6, 2.3 plan Q7). Returns how many
    /// entries it removed.
    /// </summary>
    public int RevokeReadGrant(string sectionKey)
    {
        ArgumentNullException.ThrowIfNull(sectionKey);
        return Revoke(ProtectedSectionsArea, sectionKey);
    }

    /// <summary>
    /// Removes every write grant (<c>notebooks{}</c> entry) whose key equals <paramref name="notebookKey"/>, exactly or
    /// ignoring case (AD-6). Runs on a file invalid only because of duplicate grant keys, like
    /// <see cref="RevokeReadGrant"/>. The permanent-delete grant goes with it (Phill, 2026-11-13, 5.6 plan Q2 b): a user
    /// who revokes writes expects nothing can be deleted. Returns how many matching entries it removed.
    /// </summary>
    public int RevokeWriteGrant(string notebookKey)
    {
        ArgumentNullException.ThrowIfNull(notebookKey);
        return Revoke(NotebooksArea, notebookKey);
    }

    /// <summary>
    /// Runs <paramref name="action"/> while holding <c>Local\OneNoteMcp.Settings</c> (AD-10), for work on the settings
    /// files that isn't a grant change, such as <c>uninstall --purge</c> removing them (retro R4). An abandoned mutex
    /// counts as acquired. Throws <see cref="ErrorCode.ComBusy"/> when another process holds the lock too long; then
    /// <paramref name="action"/> doesn't run. Never hold it across a prompt.
    /// </summary>
    public void RunLocked(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
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
            action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// Stores (or replaces) the write grant <paramref name="grant"/> in <c>notebooks{}</c> under its key (AD-6); an entry
    /// whose key differs only in case is replaced too. Granting writes again keeps a permanent-delete grant that is live
    /// beside an existing write grant; it never brings back one held without a write grant (an entry with
    /// <c>"write": false</c>, from a file an earlier server wrote), so a later write grant, the dialog's included, can't
    /// silently restore permanent deletes (Phill, 2026-11-13, 5.6 plan Q2 b). Same failures as <see cref="SaveReadGrant"/>.
    /// </summary>
    public void SaveWriteGrant(NotebookGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        Change(root =>
        {
            var notebooks = Area(root, NotebooksArea);
            var keptPermanentDelete = notebooks
                .Where(p => Access.GrantKeys.Comparer.Equals(p.Key, grant.Key))
                .Any(p => p.Value is JsonObject old && IsTrue(old, "write") && IsTrue(old, "permanentDelete"));
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

    /// <summary>
    /// Gives the permanent-delete grant (AD-6; Phill, 2026-11-13, 5.6 plan Q1 c, Q2 b) to the notebook keyed
    /// <paramref name="notebookKey"/>, which must already hold a write grant: <c>"permanentDelete": true</c> on that entry,
    /// every other member as it was. A notebook with no write grant is refused with <see cref="ErrorCode.WriteNotGranted"/>
    /// and nothing changes. Only the grant CLI calls it: no dialog stores this grant. Same failures as
    /// <see cref="SaveReadGrant"/>.
    /// </summary>
    public void SavePermanentDeleteGrant(string notebookKey)
    {
        ArgumentNullException.ThrowIfNull(notebookKey);
        Change(root =>
        {
            var entry = Area(root, NotebooksArea)
                .Where(p => Access.GrantKeys.Comparer.Equals(p.Key, notebookKey))
                .Select(p => p.Value as JsonObject)
                .FirstOrDefault(e => e is not null && IsTrue(e, "write"))
                ?? throw new OneNoteException(
                    ErrorCode.WriteNotGranted,
                    "That notebook has no write grant, and a permanent-delete grant counts only beside one.",
                    userAction: "Grant writes to the notebook first (grant --write), then grant permanent deletes.");
            if (IsTrue(entry, "permanentDelete"))
            {
                return false;
            }

            entry["permanentDelete"] = true;
            return true;
        });
    }

    /// <summary>
    /// Takes the permanent-delete grant away from every <c>notebooks{}</c> entry whose key equals
    /// <paramref name="notebookKey"/>, ignoring case, and leaves their write grants as they were (5.6 plan Q2 b). Returns how
    /// many entries held it. Same failures as <see cref="SaveReadGrant"/>.
    /// </summary>
    public int RevokePermanentDeleteGrant(string notebookKey)
    {
        ArgumentNullException.ThrowIfNull(notebookKey);
        var revoked = 0;
        Change(root =>
        {
            revoked = 0;
            foreach (var entry in Area(root, NotebooksArea)
                .Where(p => Access.GrantKeys.Comparer.Equals(p.Key, notebookKey))
                .Select(p => p.Value)
                .OfType<JsonObject>()
                .Where(e => IsTrue(e, "permanentDelete"))
                .ToList())
            {
                entry["permanentDelete"] = false;
                revoked++;
            }

            return revoked > 0;
        });
        return revoked;
    }

    /// <summary>True when <paramref name="entry"/>'s <paramref name="member"/> is the JSON <c>true</c>.</summary>
    private static bool IsTrue(JsonObject entry, string member) =>
        entry[member] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

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

    /// <summary>A grant change: a strict read-modify-write, refused on any file that isn't valid (or absent).</summary>
    private void Change(Func<JsonObject, bool> change) => RunLocked(() =>
    {
        // Re-read inside the lock, so a change another process made since is never lost.
        var snapshot = store.Read();
        if (snapshot.Status is not (SettingsStatus.Ok or SettingsStatus.Absent))
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

        Write(root.ToJsonString(Indented));
    });

    /// <summary>
    /// <c>grant --revoke</c> (AD-6, 2.3 plan Q7): removes every property of <paramref name="area"/> whose key equals
    /// <paramref name="key"/> ignoring case, exact duplicates included, and drops expired read grants as every grant
    /// writer does. A <see cref="JsonObject"/> can't hold a duplicate key, so the file is parsed as a
    /// <see cref="JsonDocument"/> with duplicates allowed and written back property by property: everything else is
    /// copied as it was, other duplicates included. Runs on a valid file, or one invalid only for duplicate grant keys.
    /// </summary>
    private int Revoke(string area, string key)
    {
        var removed = 0;
        RunLocked(() =>
        {
            var snapshot = store.Read();
            if (snapshot.Status == SettingsStatus.Absent)
            {
                return;
            }

            if (snapshot.Status != SettingsStatus.Ok && !snapshot.DuplicateGrantKeysOnly)
            {
                snapshot.EnsureWritable();
                throw new OneNoteException(ErrorCode.SettingsInvalid, $"settings.json can't be written ({snapshot.Reason}).");
            }

            byte[] bytes;
            try
            {
                bytes = ReadBytes();
                using var document = JsonDocument.Parse(bytes, SettingsJson.Options(allowDuplicateProperties: true));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new OneNoteException(ErrorCode.SettingsInvalid, "settings.json: (root) must be a JSON object.");
                }

                var pruned = 0;
                using var buffer = new MemoryStream();
                using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                {
                    json.WriteStartObject();
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        var isArea = property.NameEquals(area) && property.Value.ValueKind == JsonValueKind.Object;
                        var isSections = property.NameEquals(ProtectedSectionsArea) && property.Value.ValueKind == JsonValueKind.Object;
                        if (!isArea && !isSections)
                        {
                            property.WriteTo(json);
                            continue;
                        }

                        json.WritePropertyName(property.Name);
                        json.WriteStartObject();
                        foreach (var entry in property.Value.EnumerateObject())
                        {
                            if (isArea && Access.GrantKeys.Comparer.Equals(entry.Name, key))
                            {
                                // A write revoke takes the permanent-delete grant with it (5.6 plan Q2 b).
                                removed++;
                            }
                            else if (isSections && IsExpired(entry.Value))
                            {
                                pruned++;
                            }
                            else
                            {
                                entry.WriteTo(json);
                            }
                        }

                        json.WriteEndObject();
                    }

                    json.WriteEndObject();
                }

                if (removed == 0 && pruned == 0)
                {
                    return;
                }

                Write(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // The message may quote the file; name only the failure (AD-13).
                throw new OneNoteException(ErrorCode.SettingsInvalid, $"settings.json couldn't be read for writing ({ex.GetType().Name}).", innerException: ex);
            }
        });
        return removed;
    }

    private byte[] ReadBytes()
    {
        using var stream = new FileStream(store.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
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

    /// <summary><see cref="PruneExpired"/>'s test for one entry read from a <see cref="JsonDocument"/>.</summary>
    private bool IsExpired(JsonElement entry) =>
        entry.ValueKind == JsonValueKind.Object
        && entry.TryGetProperty("expiresAt", out var value)
        && value.ValueKind == JsonValueKind.String
        && SettingsJson.TryParseUtc(value.GetString(), out var end)
        && clock.GetUtcNow() >= end;

    private void Write(string json)
    {
        var path = store.Path;
        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, $".settings.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temp, json + Environment.NewLine, new System.Text.UTF8Encoding(false));
            if (File.Exists(path))
            {
                ReplaceWithRetry(temp, path);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Never delete the only copy of the new content: keep the temp file when settings.json is gone.
            if (File.Exists(path) || !File.Exists(temp))
            {
                TryDelete(temp);
            }

            throw new OneNoteException(ErrorCode.SettingsInvalid, $"settings.json couldn't be written ({ex.GetType().Name}).", innerException: ex, userAction: "Check that settings.json and its folder are writable, then retry.");
        }
    }

    /// <summary>How many times <see cref="ReplaceWithRetry"/> tries, and how long it waits between tries: about half a second in all (bug 2.15).</summary>
    internal const int ReplaceAttempts = 10;

    internal static readonly TimeSpan ReplaceDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// <c>File.Replace</c> of <paramref name="path"/> by <paramref name="temp"/>, tried for about half a second (bug
    /// 2.15). A file replaced a moment ago can still be open in another program that scans new files (an antivirus, the
    /// indexer), and Windows then refuses with "Unable to remove the file to be replaced"; this was seen in the unit
    /// tests' back-to-back grant saves. A replace can also fail after removing <paramref name="path"/>
    /// (<c>ERROR_UNABLE_TO_MOVE_REPLACEMENT</c>, 1176), leaving the new content only in <paramref name="temp"/>: then
    /// <paramref name="temp"/> is moved into place, and nothing is lost. The settings mutex is held throughout, so no other
    /// OneNoteMcp change can slip in between. <paramref name="replace"/> stands in for <c>File.Replace</c> in tests.
    /// </summary>
    internal static void ReplaceWithRetry(string temp, string path, Action<string, string>? replace = null)
    {
        replace ??= static (t, p) => File.Replace(t, p, destinationBackupFileName: null);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                replace(temp, path);
                return;
            }
            catch (IOException)
            {
                // 1176: the old file is gone and the new one never moved in. Finish the move ourselves.
                if (!File.Exists(path) && File.Exists(temp))
                {
                    File.Move(temp, path);
                    return;
                }

                if (attempt >= ReplaceAttempts)
                {
                    throw;
                }

                Thread.Sleep(ReplaceDelay);
            }
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
