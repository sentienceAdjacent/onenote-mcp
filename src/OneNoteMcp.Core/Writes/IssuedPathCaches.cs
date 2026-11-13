using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// The <c>pathCache</c> values this server's page reads returned (Phill, 2026-11-12, after the 2.9/2.10 live sitting's
/// B3). OneNote names a new temp copy of an attachment on every read, so a caller resending an attachment unchanged
/// always sends a temp name the write's own read doesn't show. A value remembered here for the same page, owner and
/// element isn't new (AD-7), so the user isn't asked again about OneNote's own copy. In memory only, for this server's
/// lifetime, at most <see cref="Capacity"/> values, the oldest forgotten first. Values never go into messages or logs.
/// </summary>
public sealed class IssuedPathCaches
{
    /// <summary>The most values held at once.</summary>
    public const int Capacity = 4096;

    private readonly Lock gate = new();
    private readonly HashSet<string> keys = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();

    /// <summary>How many values are held.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return keys.Count;
            }
        }
    }

    /// <summary>
    /// Remembers every <c>pathCache</c> on a <c>one:InsertedFile</c> or <c>one:MediaFile</c> with an owner in
    /// <paramref name="pageXml"/>, a read of page <paramref name="pageId"/> about to be returned. XML that isn't well
    /// formed adds nothing.
    /// </summary>
    public void Remember(string pageId, string pageXml)
    {
        ArgumentNullException.ThrowIfNull(pageId);
        if (pageXml is null || !pageXml.Contains("pathCache", StringComparison.Ordinal))
        {
            return;
        }

        IReadOnlyList<SentFilePath> paths;
        try
        {
            paths = PageWriteScan.FilePaths(pageXml);
        }
        catch (OneNoteXmlException)
        {
            return;
        }

        lock (gate)
        {
            foreach (var path in paths)
            {
                if (path is { Attribute: "pathCache", OwnerObjectId: not null } && keys.Add(Key(pageId, path)))
                {
                    order.Enqueue(Key(pageId, path));
                    if (order.Count > Capacity)
                    {
                        keys.Remove(order.Dequeue());
                    }
                }
            }
        }
    }

    /// <summary>True when a read of page <paramref name="pageId"/> returned <paramref name="path"/>, a <c>pathCache</c>, on the same owner and element.</summary>
    public bool Issued(string pageId, SentFilePath path)
    {
        ArgumentNullException.ThrowIfNull(pageId);
        ArgumentNullException.ThrowIfNull(path);
        if (path is not { Attribute: "pathCache", OwnerObjectId: not null })
        {
            return false;
        }

        lock (gate)
        {
            return keys.Contains(Key(pageId, path));
        }
    }

    private static string Key(string pageId, SentFilePath path) => string.Join('\n', pageId, path.OwnerObjectId, path.Element, path.Value);
}
