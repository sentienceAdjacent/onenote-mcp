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
    /// junction along it resolved to its final target, segment by segment. Null with a <paramref name="refusal"/> for a
    /// device path, a UNC path, a mapped network drive or a path that leads to one, and anything that isn't a fully
    /// qualified local path. Only fully qualified paths are ever resolved, so the result never depends on the server's
    /// working directory.
    /// </summary>
    public static string? Canonical(string path, out FilePathRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(path);
        refusal = default;
        if (Classify(path) is { } early)
        {
            refusal = early;
            return null;
        }

        string resolved;
        try
        {
            resolved = ResolveLinks(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            refusal = FilePathRefusal.NotFullyQualified;
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

    /// <summary>Walks <paramref name="full"/> from its root, replacing each existing link or junction by its final target.</summary>
    private static string ResolveLinks(string full)
    {
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        var segments = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var next = Path.Combine(current, segments[i]);
            FileSystemInfo? info = Directory.Exists(next) ? new DirectoryInfo(next) : File.Exists(next) ? new FileInfo(next) : null;
            if (info?.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                next = Path.GetFullPath(target.FullName);

                // A link to the network or a device: stop before touching it; the caller refuses the result.
                if (Classify(next) is FilePathRefusal.Network or FilePathRefusal.Device)
                {
                    return Path.Combine([next, .. segments[(i + 1)..]]);
                }
            }

            current = next;
        }

        return current.Length == 0 ? full : current;
    }
}
