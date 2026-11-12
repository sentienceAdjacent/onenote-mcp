using System.Globalization;

namespace OneNoteMcp.Core.Writes;

/// <summary>Why <see cref="FilePathGate.Canonical"/> refused a path.</summary>
public enum FilePathRefusal
{
    /// <summary>A UNC path (<c>\\server\share\…</c>), a mapped network drive, or a link that leads to one.</summary>
    Network,

    /// <summary>A device path (<c>\\?\…</c>, <c>\\.\…</c>).</summary>
    Device,

    /// <summary>Not a fully qualified local path (<c>C:\…</c>): a relative path, a drive-relative one (<c>C:x</c>), a URL.</summary>
    NotFullyQualified,

    /// <summary>
    /// A control character, an invisible formatting character (bidi overrides, zero-width marks) or a character a Windows
    /// path can't hold (<c>&lt; &gt; " | ? *</c>): such a path could disguise itself or add lines in the dialog that names it.
    /// </summary>
    InvalidCharacters,

    /// <summary>The path goes through more than <see cref="FilePathGate.MaxLinkHops"/> links or junctions, a cycle included.</summary>
    TooManyLinks,
}

/// <summary>
/// AD-7's user-gated file paths (Phill, 2026-11-12, story 2.9 security review): OneNote reads a file named in page XML
/// into the page, so every new path is made canonical, refused when it is a network or device path, and otherwise
/// confirmed by the user in a dialog before the write. This class does the file-system part: <see cref="Canonical"/>.
/// Paths never go into messages or logs: the dialog shows them to the user only.
/// </summary>
public static class FilePathGate
{
    /// <summary>
    /// The canonical form of <paramref name="path"/>: <see cref="Path.GetFullPath(string)"/>, then every existing link or
    /// junction along it followed one hop at a time, each immediate target classified before it is followed. Null with a
    /// <paramref name="refusal"/> for a device path, a UNC path, a mapped network drive or a path whose links lead to one
    /// (refused at the first such hop, which is never touched), more than <see cref="MaxLinkHops"/> hops, and anything
    /// that isn't a fully qualified local path. Only fully qualified paths are ever resolved, so the result never depends on the server's
    /// working directory.
    /// </summary>
    public static string? Canonical(string path, out FilePathRefusal refusal) => Canonical(path, FileSystemLinks.Instance, out refusal);

    /// <summary><see cref="Canonical(string, out FilePathRefusal)"/> over <paramref name="links"/>, the seam tests use.</summary>
    internal static string? Canonical(string path, ILinkReader links, out FilePathRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(links);
        refusal = default;
        if (Classify(path) is { } early)
        {
            refusal = early;
            return null;
        }

        string? resolved;
        try
        {
            resolved = ResolveLinks(Path.GetFullPath(path), links, out refusal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            refusal = FilePathRefusal.NotFullyQualified;
            return null;
        }

        if (resolved is null)
        {
            return null;
        }

        if (Classify(resolved) is { } late)
        {
            refusal = late;
            return null;
        }

        return resolved;
    }

    private static FilePathRefusal? Classify(string path)
    {
        var slashed = path.Replace('/', '\\');
        if (slashed.StartsWith(@"\\?\", StringComparison.Ordinal) || slashed.StartsWith(@"\\.\", StringComparison.Ordinal)
            || slashed.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return FilePathRefusal.Device;
        }

        if (slashed.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return FilePathRefusal.Network;
        }

        // The dialog shows the path verbatim, so nothing in it may hide characters or start a new line.
        if (path.Any(c => char.IsControl(c)
            || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            || c is '<' or '>' or '"' or '|' or '?' or '*'))
        {
            return FilePathRefusal.InvalidCharacters;
        }

        if (!Path.IsPathFullyQualified(path) || slashed.Length < 3 || !char.IsAsciiLetter(slashed[0]) || slashed[1] != ':' || slashed[2] != '\\')
        {
            return FilePathRefusal.NotFullyQualified;
        }

        return IsNetworkDrive(slashed[..3]) ? FilePathRefusal.Network : null;
    }

    private static bool IsNetworkDrive(string root)
    {
        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Walks <paramref name="full"/> from its root, one segment and one link hop at a time. At each existing link or
    /// junction it reads only the link's own immediate target (never following it), makes that absolute against the
    /// link's folder, and classifies it before going on: a network, device or otherwise refused target stops the walk at
    /// once, so nothing that was classified as network or device is ever opened or looked up. A followed target is walked
    /// again from its own root (its parent folders may be links too). More than <see cref="MaxLinkHops"/> hops, a cycle
    /// included, is refused. Returns the canonical path, or null with <paramref name="refusal"/>.
    /// </summary>
    private static string? ResolveLinks(string full, ILinkReader links, out FilePathRefusal refusal)
    {
        refusal = default;
        var hops = 0;
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        var pending = new Queue<string>(full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
        while (pending.TryDequeue(out var segment))
        {
            var next = Path.Combine(current, segment);
            if (links.ImmediateTarget(next) is not { } raw)
            {
                current = next;
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                refusal = FilePathRefusal.TooManyLinks;
                return null;
            }

            var target = Absolute(raw, Path.GetDirectoryName(next) ?? root);
            if (Classify(target) is { } refused)
            {
                // Refused before anything touches the target (the security review of 0f38576).
                refusal = refused;
                return null;
            }

            // Walk the target from its own root, then the segments that were left.
            var targetRoot = Path.GetPathRoot(target) ?? string.Empty;
            var rest = pending.ToArray();
            pending = new Queue<string>([.. target[targetRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries), .. rest]);
            current = targetRoot;
        }

        return current.Length == 0 ? full : current;
    }

    /// <summary>
    /// A link's raw target as an absolute path, without touching it: an NT-namespace prefix (<c>\??\</c>) is turned into
    /// the form <see cref="Classify"/> refuses or accepts (<c>\??\UNC\server\…</c> becomes <c>\\server\…</c>, a network
    /// path), and a relative target is resolved against <paramref name="linkFolder"/>.
    /// </summary>
    private static string Absolute(string raw, string linkFolder)
    {
        if (raw.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + raw[8..];
        }

        if (raw.StartsWith(@"\??\", StringComparison.Ordinal) && raw.Length > 6 && raw[5] == ':')
        {
            raw = raw[4..];
        }

        if (raw.StartsWith(@"\\", StringComparison.Ordinal) || raw.StartsWith("//", StringComparison.Ordinal) || raw.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return raw; // network or device: classified, never resolved
        }

        if (Path.IsPathFullyQualified(raw))
        {
            return Path.GetFullPath(raw);
        }

        // Rooted but not fully qualified (`\foo`, which a reparse point means in the NT namespace, or drive-relative
        // `X:foo`): GetFullPath would resolve it against this process's drive and folder, not the link's volume, so it is
        // returned unresolved and Classify refuses it (security review of 3946c72). Only a truly relative target is
        // resolved against the link's own folder.
        return Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(linkFolder, raw));
    }

    /// <summary>The most link hops one path may take; more, a cycle included, is <see cref="FilePathRefusal.TooManyLinks"/>.</summary>
    public const int MaxLinkHops = 8;

    /// <summary>Reads a link's immediate target, for <see cref="Canonical(string, ILinkReader, out FilePathRefusal)"/>; a seam for tests.</summary>
    internal interface ILinkReader
    {
        /// <summary>The raw immediate target of <paramref name="path"/> when it is a link or junction; null when it isn't one or doesn't exist. Never follows the link.</summary>
        string? ImmediateTarget(string path);
    }

    /// <summary>The real file system: <see cref="FileSystemInfo.LinkTarget"/> reads the link's own reparse data only.</summary>
    private sealed class FileSystemLinks : ILinkReader
    {
        public static FileSystemLinks Instance { get; } = new();

        public string? ImmediateTarget(string path)
        {
            FileSystemInfo? info = Directory.Exists(path) ? new DirectoryInfo(path) : File.Exists(path) ? new FileInfo(path) : null;
            return info?.LinkTarget;
        }
    }
}
