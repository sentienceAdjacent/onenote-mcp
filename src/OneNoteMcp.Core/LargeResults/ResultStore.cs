using OneNoteMcp.Core.Settings;

namespace OneNoteMcp.Core.LargeResults;

/// <summary>What one store change did, for the log (AD-13: IDs and sizes only).</summary>
/// <param name="Evicted">Entries dropped, least recently read first, to stay under <c>results.maxStoreMB</c>.</param>
/// <param name="Expired">Entries dropped because they went unread for <c>results.ttlMinutes</c>.</param>
/// <param name="TotalBytes">The store's size afterwards, in UTF-16 bytes.</param>
public sealed record StoreChange(IReadOnlyList<string> Evicted, IReadOnlyList<string> Expired, long TotalBytes);

/// <summary>
/// The AD-20 result store: oversized results, in this process's memory only. It never writes to disk, and logs nothing
/// itself. An entry expires <c>results.ttlMinutes</c> after its last read; one made under a <c>once</c> read grant
/// (AD-21) expires <c>ttlMinutes</c> after the original call instead, never extended by reads. Past <c>results.maxStoreMB</c> (UTF-16 bytes)
/// the least recently read entries go first; a single result larger than the cap is kept alone. Thread-safe.
/// </summary>
public sealed class ResultStore : IDisposable
{
    private readonly TimeProvider clock;
    private readonly Func<ResultSettings> settings;
    private readonly Dictionary<string, StoredResult> entries = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly ITimer sweeper;
    private long totalBytes;
    private long reads;

    /// <param name="clock">The clock TTLs run on; tests pass a fake one.</param>
    /// <param name="settings">Reads <c>results{}</c> (AD-10) each time it is needed, so a settings change applies to the next call.</param>
    public ResultStore(TimeProvider clock, Func<ResultSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        this.clock = clock;
        this.settings = settings;

        // Expired entries are dropped on every store call; the sweep frees their memory when no calls come.
        sweeper = clock.CreateTimer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>A store with the default settings and the system clock.</summary>
    public static ResultStore WithDefaults() => new(TimeProvider.System, () => ResultSettings.Default);

    /// <summary>The current <c>results{}</c> settings.</summary>
    public ResultSettings Settings => settings();

    public long TotalBytes
    {
        get
        {
            lock (gate)
            {
                return totalBytes;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    /// <summary>
    /// Adds <paramref name="entry"/> as just read, then evicts to stay under the cap. Returns when it expires and what
    /// changed. <paramref name="current"/> is the call's one read of <c>results{}</c>; omitted, the settings are read now.
    /// </summary>
    public (DateTimeOffset ExpiresAt, StoreChange Change) Add(StoredResult entry, ResultSettings? current = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        current ??= settings();
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var expired = SweepLocked(now, current);
            entry.LastRead = now;
            entry.ReadSequence = ++reads;
            entry.AbsoluteExpiry = entry.Grants.HasOnce ? now + current.Ttl : null;
            entries.Add(entry.ResultId, entry);
            totalBytes += entry.Bytes;

            var evicted = new List<string>();
            while (totalBytes > current.MaxStoreBytes && entries.Count > 1)
            {
                var oldest = entries.Values.Where(e => !ReferenceEquals(e, entry)).MinBy(e => e.ReadSequence)!;
                Remove(oldest);
                evicted.Add(oldest.ResultId);
            }

            return (ExpiresAt(entry, now, current), new StoreChange(evicted, expired, totalBytes));
        }
    }

    /// <summary>
    /// The entry for <paramref name="resultId"/>, marked as read now, and when it will expire. Throws
    /// <see cref="ErrorCode.ResultExpired"/> for an ID that expired, was evicted, or was never issued.
    /// <paramref name="current"/> is the call's one read of <c>results{}</c>; omitted, the settings are read now.
    /// </summary>
    public (StoredResult Entry, DateTimeOffset ExpiresAt, StoreChange Change) Get(string resultId, ResultSettings? current = null)
    {
        current ??= settings();
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var expired = SweepLocked(now, current);
            if (string.IsNullOrEmpty(resultId) || !entries.TryGetValue(resultId, out var entry))
            {
                throw new OneNoteException(
                    ErrorCode.ResultExpired,
                    $"Result {resultId} is not in the result store: it expired, was evicted, or was never issued.");
            }

            entry.LastRead = now;
            entry.ReadSequence = ++reads;
            return (entry, ExpiresAt(entry, now, current), new StoreChange([], expired, totalBytes));
        }
    }

    /// <summary>Drops every entry unread for <c>results.ttlMinutes</c>.</summary>
    public IReadOnlyList<string> Sweep()
    {
        var current = settings();
        lock (gate)
        {
            return SweepLocked(clock.GetUtcNow(), current);
        }
    }

    public void Dispose() => sweeper.Dispose();

    private List<string> SweepLocked(DateTimeOffset now, ResultSettings current)
    {
        var expired = entries.Values.Where(e => now >= e.LastRead + current.Ttl || (e.AbsoluteExpiry is { } end && now >= end)).ToList();
        foreach (var entry in expired)
        {
            Remove(entry);
        }

        return [.. expired.Select(e => e.ResultId)];
    }

    private static DateTimeOffset ExpiresAt(StoredResult entry, DateTimeOffset now, ResultSettings current)
    {
        var sliding = now + current.Ttl;
        return entry.AbsoluteExpiry is { } end && end < sliding ? end : sliding;
    }

    private void Remove(StoredResult entry)
    {
        entries.Remove(entry.ResultId);
        totalBytes -= entry.Bytes;
    }
}
