using OneNoteMcp.Core.Settings;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// AD-7 <c>ExternalWrite</c> validation of an export's target file (AD-11; stories 3.3 and 3.13): where <c>publish</c> may write.
/// <list type="number">
/// <item>The caller's path must end in its format's extension, ignoring case (3.3 plan Q2; 3.13 plan Q1:
/// <see cref="Extensions"/>), and hold no <c>:</c> after the drive (an NTFS stream).</item>
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
/// <see cref="ErrorCode.FileExists"/>. For HTML, nothing may exist at <c>&lt;stem&gt;_files</c> beside it either, where a
/// web page's images may land (3.13 plan Q3).</item>
/// </list>
/// Messages name only the caller's own path, never a root (AD-9).
/// </summary>
public static class ExportTarget
{
    /// <summary>The suffix of the folder beside an HTML export where its images may land (3.13 plan Q3).</summary>
    public const string HtmlFilesSuffix = "_files";

    /// <summary>What to do about a path this check refuses as malformed.</summary>
    public const string InvalidPathUserAction =
        "Pass a file name ending in the format's extension, such as \"Meeting notes.pdf\" or \"Project\\Meeting notes.docx\", which lands under the export root, "
        + "or a full local path (C:\\…) under an export root. No network or device paths, no ':' after the drive, and none of < > \" | ? *.";

    /// <summary>The extensions a target in <paramref name="format"/> may end in, the first being its usual one (3.13 plan Q1).</summary>
    public static IReadOnlyList<string> Extensions(ExportFormat format) => format switch
    {
        ExportFormat.OneNote => [".one"],
        ExportFormat.Pdf => [".pdf"],
        ExportFormat.Xps => [".xps"],
        ExportFormat.Word => [".docx"],
        ExportFormat.Mhtml => [".mht", ".mhtml"],
        ExportFormat.Html => [".htm", ".html"],
        ExportFormat.Emf => [".emf"],
        ExportFormat.OneNotePackage => [".onepkg"],
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Not an export format."),
    };

    /// <summary>What to do about a path outside every export root.</summary>
    public const string NotAllowedUserAction =
        "Pass a path relative to the export root, such as \"Meeting notes.pdf\", which lands under the first exportRoots entry "
        + "(by default Documents\\OneNote Exports), or a full path inside one of them. Only the user can change exportRoots, by editing settings.json.";

    /// <summary>What to do when something is already at the path.</summary>
    public const string ExistsUserAction =
        "Nothing overwrites a file: choose a new file name, or ask the user to move or delete the existing file first.";

    /// <summary>
    /// The canonical path for <paramref name="raw"/>, a <paramref name="format"/> file, under <paramref name="roots"/>, or
    /// throws <see cref="ErrorCode.InvalidArgument"/>, <see cref="ErrorCode.ExportPathNotAllowed"/> or
    /// <see cref="ErrorCode.FileExists"/>, checked in that order.
    /// </summary>
    public static string Resolve(string raw, ExportRoots roots, ExportFormat format)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var extensions = Extensions(format);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw Invalid("The argument path is empty, so nothing was exported.");
        }

        if (!EndsInOneOf(raw, extensions))
        {
            throw Invalid($"The path {raw} doesn't end in {string.Join(" or ", extensions)}, the extension of its format, so nothing was exported.");
        }

        var target = UnderARoot(raw, roots, Exported, extensions);
        if (format == ExportFormat.Html && FilesFolder(target) is var folder && Exists(folder))
        {
            throw new OneNoteException(
                ErrorCode.FileExists,
                $"Something already exists at {folder}, where the web page's images would go, so nothing was exported.",
                userAction: ExistsUserAction,
                retryable: false);
        }

        return target;
    }

    /// <summary>What to do about a destination folder this check refuses as malformed (story 3.8).</summary>
    public const string InvalidFolderUserAction =
        "Pass a new folder name, such as \"Opened trip\", which lands under the export root, or a full local path (C:\\…) under an export root, "
        + "or leave destination out for a folder named after the package. No network or device paths, no ':' after the drive, and none of < > \" | ? *.";

    /// <summary>
    /// Story 3.8 (<c>open_package</c>): the canonical path for <paramref name="raw"/>, a new folder, under
    /// <paramref name="roots"/>, with <see cref="Resolve"/>'s rules but no extension, or throws
    /// <see cref="ErrorCode.InvalidArgument"/>, <see cref="ErrorCode.ExportPathNotAllowed"/> or
    /// <see cref="ErrorCode.FileExists"/>; messages say nothing was opened. The same root test as exports and AD-21's path
    /// rule (<see cref="PathUnder"/>).
    /// </summary>
    public static string ResolveFolder(string raw, ExportRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new OneNoteException(ErrorCode.InvalidArgument, "The argument destination is empty, so nothing was opened.", userAction: InvalidFolderUserAction, retryable: false);
        }

        return UnderARoot(raw.TrimEnd('\\', '/'), roots, Opened, extensions: null);
    }

    private const string Exported = "nothing was exported";

    private const string Opened = "nothing was opened";

    /// <summary>The checks <see cref="Resolve"/> and <see cref="ResolveFolder"/> share, in the class summary's order.</summary>
    private static string UnderARoot(string raw, ExportRoots roots, string nothing, IReadOnlyList<string>? extensions)
    {
        var full = raw;
        if (!Path.IsPathRooted(raw))
        {
            if (roots.Paths.Count == 0)
            {
                throw NotAllowed(raw, "no export root is configured", nothing);
            }

            // The first root must itself be a usable local folder; otherwise the root is at fault, not the caller's name.
            if (string.IsNullOrWhiteSpace(roots.Paths[0]) || FilePathGate.Canonical(roots.Paths[0], out _) is not { } firstRoot)
            {
                throw NotAllowed(raw, "the first export root isn't a usable local folder", nothing);
            }

            full = Path.Combine(firstRoot, raw);
        }

        // A ':' anywhere but after the drive letter names an NTFS stream or isn't a Windows path at all.
        if (full.IndexOf(':', 2) >= 0 || (full.Contains(':', StringComparison.Ordinal) && full.IndexOf(':', StringComparison.Ordinal) != 1))
        {
            throw Invalid($"The path {raw} has a ':' after its drive, so {nothing}.", extensions is null);
        }

        if (FilePathGate.Canonical(full, out var refusal) is not { } target)
        {
            throw Invalid($"The path {raw} {Why(refusal)}, so {nothing}.", extensions is null);
        }

        if ((extensions is not null && !EndsInOneOf(target, extensions)) || target.IndexOf(':', 2) >= 0)
        {
            // A link followed to a name with another extension (the caller's own path passed the check above).
            throw NotAllowed(raw, extensions is null ? "it leads somewhere that isn't a plain local path" : $"it leads to a file that doesn't end in {string.Join(" or ", extensions)}", nothing);
        }

        if (!roots.Paths.Any(root => Contains(root, target)))
        {
            throw NotAllowed(raw, "it isn't inside an export root", nothing);
        }

        if (Exists(full) || Exists(target))
        {
            throw new OneNoteException(
                ErrorCode.FileExists,
                $"Something already exists at {target}, so {nothing}.",
                userAction: extensions is null ? "Nothing is opened into an existing folder: choose a new folder name, or leave destination out." : ExistsUserAction,
                retryable: false);
        }

        return target;
    }

    /// <summary>The folder beside an HTML export where its images may land: <c>&lt;stem&gt;_files</c> (3.13 plan Q3).</summary>
    public static string FilesFolder(string target) =>
        Path.Combine(Path.GetDirectoryName(target) ?? string.Empty, Path.GetFileNameWithoutExtension(target) + HtmlFilesSuffix);

    private static bool EndsInOneOf(string path, IReadOnlyList<string> extensions) =>
        extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when <paramref name="target"/> (already canonical) sits strictly inside <paramref name="root"/>: the one test
    /// the protected-by-path rule uses too (<see cref="PathUnder"/>, entry 15).
    /// </summary>
    private static bool Contains(string root, string target) => PathUnder.IsUnder(target, [root]);

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

    private static OneNoteException Invalid(string message, bool folder = false) =>
        new(ErrorCode.InvalidArgument, message, userAction: folder ? InvalidFolderUserAction : InvalidPathUserAction, retryable: false);

    private static OneNoteException NotAllowed(string raw, string why, string nothing = Exported) =>
        new(ErrorCode.ExportPathNotAllowed, $"The path {raw} isn't allowed: {why}, so {nothing}.", userAction: NotAllowedUserAction, retryable: false);
}
