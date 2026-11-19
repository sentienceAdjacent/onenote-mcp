namespace OneNoteMcp.Core.Writes;

/// <summary>
/// Backlog 13: how AD-21's path rule reads a path OneNote reports as a web address (a notebook in OneDrive, after a
/// restart: <c>https://d.docs.live.net/&lt;cid&gt;/Documents/…</c>). Only <c>http</c> and <c>https</c> count. The query and
/// fragment are dropped, the rest is percent-decoded (an invalid escape is left as it is), and then split into segments
/// at <c>/</c>, empty ones dropped, so a trailing <c>/</c> doesn't matter. A <c>%2F</c> that decodes into a new segment
/// boundary may make a URL look deeper than it is; for the protection rule that only ever over-protects.
/// </summary>
public static class UrlPath
{
    /// <summary>
    /// True when <paramref name="path"/> is an <c>http</c> or <c>https</c> address: <paramref name="host"/> is its host
    /// and <paramref name="segments"/> its decoded path segments. Never throws.
    /// </summary>
    public static bool TryParse(string? path, out string host, out IReadOnlyList<string> segments)
    {
        host = string.Empty;
        segments = [];
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        int start;
        if (path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            start = "https://".Length;
        }
        else if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            start = "http://".Length;
        }
        else
        {
            return false;
        }

        var rest = path[start..];
        var cut = rest.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            rest = rest[..cut];
        }

        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        host = slash < 0 ? rest : rest[..slash];
        if (slash >= 0)
        {
            segments = Decode(rest[(slash + 1)..]).Split('/', StringSplitOptions.RemoveEmptyEntries);
        }

        return true;
    }

    /// <summary>
    /// The segments of the local path <paramref name="fullPath"/> below its drive or share root (if any), empty ones
    /// dropped (<c>C:\a\b\</c> gives <c>a</c>, <c>b</c>).
    /// </summary>
    public static IReadOnlyList<string> LocalSegments(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        return fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// The first index at which <paramref name="tail"/> appears in <paramref name="segments"/> as whole segments, compared
    /// case-insensitively, with at least one segment after it (strictly inside); -1 when there is none.
    /// </summary>
    public static int IndexOfTail(IReadOnlyList<string> segments, IReadOnlyList<string> tail)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(tail);
        for (var i = 0; i + tail.Count < segments.Count; i++)
        {
            var match = true;
            for (var j = 0; j < tail.Count && match; j++)
            {
                match = string.Equals(segments[i + j], tail[j], StringComparison.OrdinalIgnoreCase);
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// <paramref name="folder"/> with <paramref name="below"/> appended, made full, or null when the result escapes
    /// <paramref name="folder"/> (a decoded <c>..</c>) or isn't a valid path.
    /// </summary>
    public static string? Below(string folder, IEnumerable<string> below)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(below);
        var parts = below.ToList();
        if (PathUnder.Full(folder) is not { } full)
        {
            return null;
        }

        if (parts.Count == 0)
        {
            return full.TrimEnd(Path.DirectorySeparatorChar);
        }

        if (PathUnder.Full(Path.Combine([full, .. parts])) is not { } combined)
        {
            return null;
        }

        var prefix = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
        return PathUnder.IsUnderPrefix(combined, prefix) ? combined.TrimEnd(Path.DirectorySeparatorChar) : null;
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
