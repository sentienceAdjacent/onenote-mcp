using OneNoteMcp.Core.Settings;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// AD-7 <c>ExternalWrite</c> validation of an export's target file (AD-11; story 3.3): where <c>publish</c> may write.
/// <list type="number">
/// <item>The caller's path must end in <c>.pdf</c>, ignoring case (Phill, 2026-11-16, 3.3 plan Q2), and hold no <c>:</c>
/// after the drive (an NTFS stream).</item>
/// <item>A relative path (<c>x.pdf</c>, <c>sub\x.pdf</c>) is resolved under the first <c>exportRoots</c> entry (3.3 plan
/// Q1); anything rooted must be fully qualified and local.</item>
/// <item>The path is made canonical by <see cref="FilePathGate.Canonical(string, out FilePathRefusal)"/>, the page write's
/// own canonicaliser (<c>GetFullPath</c>, links and junctions followed one hop at a time). A UNC, device, drive-relative or
/// otherwise not fully qualified path, or one with characters a Windows path can't hold, is
/// <see cref="ErrorCode.InvalidArgument"/>.</item>
/// <item>Each root is made canonical the same way, and the target must sit strictly inside one: the root's canonical path
/// plus a separator, compared <see cref="StringComparison.OrdinalIgnoreCase"/>. The root itself is never a target. Anything
/// else, a <c>..</c> or a junction that leads out included, is <see cref="ErrorCode.ExportPathNotAllowed"/>.</item>
/// <item>Nothing may exist at the path, followed or not (no tool argument overwrites a file, AD-11):
/// <see cref="ErrorCode.FileExists"/>.</item>
/// </list>
/// Messages name only the caller's own path, never a root (AD-9).
/// </summary>
public static class ExportTarget
{
    /// <summary>The extension every <c>publish</c> target carries (PDF only in this release).</summary>
    public const string PdfExtension = ".pdf";

    /// <summary>What to do about a path this check refuses as malformed.</summary>
    public const string InvalidPathUserAction =
        "Pass a file name ending in .pdf, such as \"Meeting notes.pdf\" or \"Project\\Meeting notes.pdf\", which lands under the export root, "
        + "or a full local path (C:\\…) under an export root. No network or device paths, no ':' after the drive, and none of < > \" | ? *.";

    /// <summary>What to do about a path outside every export root.</summary>
    public const string NotAllowedUserAction =
        "Pass a path relative to the export root, such as \"Meeting notes.pdf\", which lands under the first exportRoots entry "
        + "(by default Documents\\OneNote Exports), or a full path inside one of them. Only the user can change exportRoots, by editing settings.json.";

    /// <summary>What to do when something is already at the path.</summary>
    public const string ExistsUserAction =
        "Nothing overwrites a file: choose a new file name, or ask the user to move or delete the existing file first.";

    /// <summary>
    /// The canonical path for <paramref name="raw"/> under <paramref name="roots"/>, or throws
    /// <see cref="ErrorCode.InvalidArgument"/>, <see cref="ErrorCode.ExportPathNotAllowed"/> or
    /// <see cref="ErrorCode.FileExists"/>, checked in that order.
    /// </summary>
    public static string Resolve(string raw, ExportRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw Invalid("The argument path is empty, so nothing was exported.");
        }

        if (!raw.EndsWith(PdfExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid($"The path {raw} doesn't end in .pdf, so nothing was exported.");
        }

        var full = raw;
        if (!Path.IsPathRooted(raw))
        {
            if (roots.Paths.Count == 0)
            {
                throw NotAllowed(raw, "no export root is configured");
            }

            // The first root must itself be a usable local folder; otherwise the root is at fault, not the caller's name.
            if (string.IsNullOrWhiteSpace(roots.Paths[0]) || FilePathGate.Canonical(roots.Paths[0], out _) is not { } firstRoot)
            {
                throw NotAllowed(raw, "the first export root isn't a usable local folder");
            }

            full = Path.Combine(firstRoot, raw);
        }

        // A ':' anywhere but after the drive letter names an NTFS stream or isn't a Windows path at all.
        if (full.IndexOf(':', 2) >= 0 || (full.Contains(':', StringComparison.Ordinal) && full.IndexOf(':', StringComparison.Ordinal) != 1))
        {
            throw Invalid($"The path {raw} has a ':' after its drive, so nothing was exported.");
        }

        if (FilePathGate.Canonical(full, out var refusal) is not { } target)
        {
            throw Invalid($"The path {raw} {Why(refusal)}, so nothing was exported.");
        }

        if (!target.EndsWith(PdfExtension, StringComparison.OrdinalIgnoreCase) || target.IndexOf(':', 2) >= 0)
        {
            // A link followed to a name that isn't a .pdf (the caller's own path passed the check above).
            throw NotAllowed(raw, "it leads to a file that isn't a .pdf");
        }

        if (!roots.Paths.Any(root => Contains(root, target)))
        {
            throw NotAllowed(raw, "it isn't inside an export root");
        }

        if (Exists(full) || Exists(target))
        {
            throw new OneNoteException(
                ErrorCode.FileExists,
                $"Something already exists at {target}, so nothing was exported.",
                userAction: ExistsUserAction,
                retryable: false);
        }

        return target;
    }

    /// <summary>True when <paramref name="target"/> (already canonical) sits strictly inside <paramref name="root"/>'s canonical form.</summary>
    private static bool Contains(string root, string target)
    {
        if (string.IsNullOrWhiteSpace(root) || FilePathGate.Canonical(root, out _) is not { } canonicalRoot)
        {
            return false;
        }

        var prefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar) ? canonicalRoot : canonicalRoot + Path.DirectorySeparatorChar;
        return target.Length > prefix.Length && target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A file, a folder or a link (dangling or not) at <paramref name="path"/>.</summary>
    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Unreadable counts as taken: never write over what can't be checked.
            return true;
        }
    }

    private static string Why(FilePathRefusal refusal) => refusal switch
    {
        FilePathRefusal.Network => "is a network path, or leads to one",
        FilePathRefusal.Device => "is a device path",
        FilePathRefusal.InvalidCharacters => "holds a character a Windows path can't hold",
        FilePathRefusal.TooManyLinks => "goes through too many links or junctions",
        _ => "isn't a fully qualified local path (C:\\…) or a name relative to the export root",
    };

    private static OneNoteException Invalid(string message) =>
        new(ErrorCode.InvalidArgument, message, userAction: InvalidPathUserAction, retryable: false);

    private static OneNoteException NotAllowed(string raw, string why) =>
        new(ErrorCode.ExportPathNotAllowed, $"The path {raw} isn't allowed: {why}, so nothing was exported.", userAction: NotAllowedUserAction, retryable: false);
}
