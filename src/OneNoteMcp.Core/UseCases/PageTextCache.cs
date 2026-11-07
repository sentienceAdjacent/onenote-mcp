using OneNoteMcp.Core.Settings;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// find_pages' page-text cache (AD-12): each candidate page's extracted text (<c>PageText.Of</c>, never the XML), keyed by
/// page ID plus the page's <c>lastModifiedTime</c> from the search XML, so a later search reads only pages that changed.
/// In this process's memory only, never on disk (AD-13). Sized by <see cref="SizeOf"/> (UTF-16 bytes plus a per-entry overhead) against <c>search.cacheMaxMB</c> (AD-10),
/// least recently used first out; 0 turns it off. Thread-safe.
/// </summary>
/// <remarks>
/// Known gap (spike 1): a page edited through COM while it isn't open in OneNote keeps its old <c>lastModifiedTime</c>,
/// so its old text stays cached until the page next changes, the entry is evicted or the server restarts.
/// </remarks>
public sealed class PageTextCache
{
    private readonly Func<SearchSettings> settings;
    private readonly Dictionary<string, LinkedListNode<Entry>> entries = new(StringComparer.Ordinal);

    // Most recently used first.
    private readonly LinkedList<Entry> order = new();
    private readonly Lock gate = new();
    private long totalBytes;

    /// <param name="settings">Reads <c>search{}</c> (AD-10); a call reads it once, so a settings change applies to the next call.</param>
    public PageTextCache(Func<SearchSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
    }

    /// <summary>A cache on the default settings.</summary>
    public static PageTextCache WithDefaults() => new(() => SearchSettings.Default);

    /// <summary>The current <c>search{}</c> settings.</summary>
    public SearchSettings Settings => settings();

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
    /// A fixed estimate of what each entry costs besides its strings' characters: the dictionary slot, the list node,
    /// the entry object and the two string headers.
    /// </summary>
    public const long EntryOverheadBytes = 160;

    /// <summary>
    /// An entry's estimated size, as the cap counts it: <see cref="EntryOverheadBytes"/> plus the UTF-16 bytes of
    /// <paramref name="pageId"/> and <paramref name="text"/>. So an entry with empty text still counts.
    /// </summary>
    public static long SizeOf(string pageId, string text) => EntryOverheadBytes + ((pageId?.Length ?? 0) + (text?.Length ?? 0)) * 2L;

    /// <summary>
    /// The cached text of <paramref name="pageId"/> when it was cached at <paramref name="lastModifiedTime"/>, marked as
    /// used now. An entry cached at another time is stale: it is dropped and the lookup misses.
    /// </summary>
    public bool TryGet(string pageId, DateTimeOffset lastModifiedTime, out string text)
    {
        lock (gate)
        {
            if (entries.TryGetValue(pageId, out var node))
            {
                if (node.Value.LastModifiedTime == lastModifiedTime)
                {
                    order.Remove(node);
                    order.AddFirst(node);
                    text = node.Value.Text;
                    return true;
                }

                Drop(node);
            }

            text = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Caches <paramref name="text"/> for <paramref name="pageId"/> at <paramref name="lastModifiedTime"/>, replacing any
    /// older entry, then drops the least recently used entries until the total is at most <paramref name="maxBytes"/>.
    /// An entry whose <see cref="SizeOf"/> alone is over <paramref name="maxBytes"/> isn't cached. Returns how many entries were evicted.
    /// </summary>
    public int Set(string pageId, DateTimeOffset lastModifiedTime, string text, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(pageId);
        ArgumentNullException.ThrowIfNull(text);
        lock (gate)
        {
            if (entries.TryGetValue(pageId, out var existing))
            {
                Drop(existing);
            }

            var bytes = SizeOf(pageId, text);
            if (bytes > maxBytes)
            {
                return 0;
            }

            entries.Add(pageId, order.AddFirst(new Entry(pageId, lastModifiedTime, text, bytes)));
            totalBytes += bytes;
            return TrimLocked(maxBytes);
        }
    }

    /// <summary>Drops <paramref name="pageId"/>'s entry, if any (a page that no longer exists).</summary>
    public void Remove(string pageId)
    {
        lock (gate)
        {
            if (entries.TryGetValue(pageId, out var node))
            {
                Drop(node);
            }
        }
    }

    /// <summary>Drops the least recently used entries until the total is at most <paramref name="maxBytes"/>; 0 empties the cache. Returns how many went.</summary>
    public int Trim(long maxBytes)
    {
        lock (gate)
        {
            return TrimLocked(maxBytes);
        }
    }

    private int TrimLocked(long maxBytes)
    {
        var evicted = 0;
        while (totalBytes > maxBytes && order.Last is { } oldest)
        {
            Drop(oldest);
            evicted++;
        }

        // Empty texts weigh nothing, so a cap of 0 drops them explicitly.
        if (maxBytes <= 0)
        {
            evicted += entries.Count;
            entries.Clear();
            order.Clear();
            totalBytes = 0;
        }

        return evicted;
    }

    private void Drop(LinkedListNode<Entry> node)
    {
        order.Remove(node);
        entries.Remove(node.Value.PageId);
        totalBytes -= node.Value.Bytes;
    }

    private sealed record Entry(string PageId, DateTimeOffset LastModifiedTime, string Text, long Bytes);
}

/// <summary>
/// How one find_pages call used the page-text cache, for the log only (AD-13: counts and sizes, never IDs or text).
/// Never on the wire.
/// </summary>
/// <param name="Hits">Candidates matched against cached text, with no read.</param>
/// <param name="Misses">Candidates read with <c>GetPageContent</c> (the cache was on, the page wasn't cached at its time).</param>
/// <param name="Evicted">Entries dropped to stay under the cap during the call.</param>
/// <param name="Entries">Entries in the cache after the call.</param>
/// <param name="Bytes">The cache's UTF-16 size after the call.</param>
public sealed record PageTextCacheUse(int Hits, int Misses, int Evicted, int Entries, long Bytes);
