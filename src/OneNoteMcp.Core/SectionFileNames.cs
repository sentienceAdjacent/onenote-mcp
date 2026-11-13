namespace OneNoteMcp.Core;

/// <summary>
/// The checks on a new hierarchy object's relative name before <c>OpenHierarchy</c> creates it (AD-7 <c>Create</c> row;
/// spike 5.1 constraints 3 and 5). Every kind follows one file-system name rule (story 5.3 build directions): one name with
/// no <c>..</c>, no <c>\</c> or <c>/</c>, no control or <c>&lt; &gt; : " | ? *</c> character, no reserved device name
/// (<c>CON</c>, <c>NUL</c>, <c>COM1</c>…, also with an extension), and no leading or trailing space or dot, because a
/// section is a file and a section group or notebook is a folder. <see cref="NameProblem"/> also refuses OneNote's own
/// <c>OneNote_RecycleBin</c> and <c>OneNote_DeletedPages</c>, ignoring case. A section reaches COM as a file name ending
/// <c>.one</c> (<see cref="ToSectionFileName"/>, Phill, 2026-11-13, 5.3 Q2). The security review (Phill, 2026-11-13) added
/// control, bidi and invisible characters to the refused ones and a <see cref="MaxNameLength"/>. The gateway refuses a name that fails with
/// <see cref="ErrorCode.InvalidArgument"/> before COM, so OneNote's path errors (<c>0x80042004</c>, <c>0x80042006</c>) and
/// its phantom <c>..</c> group are never reached through a bad name. Problems are phrases that never quote the name.
/// </summary>
public static class SectionFileNames
{
    /// <summary>The extension OneNote gives a section file.</summary>
    public const string Extension = ".one";

    /// <summary>
    /// The longest name a new section, section group or notebook may have (security review, Phill, 2026-11-13). It counts
    /// UTF-16 code units (<see cref="string.Length"/>), not text elements, as nothing else here counts text elements: a
    /// character outside the Basic Multilingual Plane, such as an emoji, counts as two.
    /// </summary>
    public const int MaxNameLength = 128;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>The names OneNote keeps for itself: the recycle-bin section group and its Deleted Pages section.</summary>
    private static readonly HashSet<string> OneNoteNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "OneNote_RecycleBin",
        "OneNote_DeletedPages",
    };

    /// <summary>
    /// Null when <paramref name="fileName"/> is a valid relative section file name, ending <c>.one</c>; otherwise what is
    /// wrong with it, as a phrase that never quotes the name (such as <c>"contains '..'"</c>). <c>restore_from_journal</c>'s
    /// <c>Recovered</c> section and the gateway use it.
    /// </summary>
    public static string? Problem(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return "is empty";
        }

        if (!fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return "doesn't end with " + Extension;
        }

        var stem = fileName[..^Extension.Length];
        if (Characters(fileName) is { } characters)
        {
            return characters;
        }

        if (stem.Trim().Length == 0)
        {
            return "has no name before " + Extension;
        }

        return Shape(fileName, stem);
    }

    /// <summary>
    /// Null when <paramref name="name"/> is a valid name for a new section (as OneNote shows it, or its file name), section
    /// group or notebook; otherwise what is wrong with it, as a phrase that never quotes the name. On top of
    /// <see cref="Problem"/>'s rules it refuses OneNote's own names, ignoring case, with or without <c>.one</c>, and a name
    /// longer than <see cref="MaxNameLength"/>. This is the one name rule all three kinds share, so the use case and the
    /// gateway both check it before any dialog or COM call.
    /// </summary>
    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "is empty";
        }

        if (name.Length > MaxNameLength)
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"is longer than {MaxNameLength} characters");
        }

        if (Characters(name) is { } characters)
        {
            return characters;
        }

        if (name.Trim().Length == 0)
        {
            return "is blank";
        }

        var stem = name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? name[..^Extension.Length] : name;
        if (OneNoteNames.Contains(name) || OneNoteNames.Contains(stem))
        {
            return "is a name OneNote keeps for its recycle bin";
        }

        return Shape(name, name);
    }

    /// <summary>
    /// The relative file name a new section named <paramref name="name"/> is created with (Phill, 2026-11-13, 5.3 Q2):
    /// <paramref name="name"/> with <c>.one</c> added, unless it already ends <c>.one</c> ignoring case, when it is sent as
    /// it is. Check <paramref name="name"/> with <see cref="NameProblem"/> first.
    /// </summary>
    public static string ToSectionFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? name : name + Extension;
    }

    /// <summary>The name a section file shows in OneNote: <paramref name="fileName"/> without its <c>.one</c>.</summary>
    public static string SectionName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? fileName[..^Extension.Length] : fileName;
    }

    private static string? Characters(string name)
    {
        if (name.Contains("..", StringComparison.Ordinal))
        {
            return "contains '..'";
        }

        if (name.Contains('\\', StringComparison.Ordinal) || name.Contains('/', StringComparison.Ordinal))
        {
            return "contains a path separator";
        }

        if (name.IndexOfAny(InvalidCharacters) >= 0)
        {
            return "contains a character a Windows file name can't hold";
        }

        return name.Any(IsHiddenOrControl) ? "contains a control, invisible or text-direction character" : null;
    }

    /// <summary>
    /// True for a character that can make a name read as something it isn't, in a dialog or a listing (security review,
    /// Phill, 2026-11-13): any control character (Unicode category Cc, which covers U+007F–U+009F as well as the ASCII
    /// ones), the line and paragraph separators U+2028 and U+2029, the bidi controls U+200E, U+200F, U+202A–U+202E and
    /// U+2066–U+2069, and the invisible U+200B–U+200D, U+2060 and U+FEFF.
    /// </summary>
    private static bool IsHiddenOrControl(char c) =>
        char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Control
        || (int)c is 0x2028 or 0x2029
            or 0x200E or 0x200F
            or (>= 0x202A and <= 0x202E)
            or (>= 0x2066 and <= 0x2069)
            or (>= 0x200B and <= 0x200D)
            or 0x2060 or 0xFEFF;

    /// <summary>The leading or trailing space or dot, and the reserved device name, of <paramref name="whole"/> and its <paramref name="stem"/>.</summary>
    private static string? Shape(string whole, string stem)
    {
        if (stem.EndsWith(' ') || stem.EndsWith('.') || whole.EndsWith(' ') || whole.EndsWith('.') || whole.StartsWith(' ') || whole.StartsWith('.'))
        {
            return "starts or ends with a space or a dot";
        }

        // Windows ignores an extension and trailing spaces after a device name: "NUL.txt" and "CON .x" are devices too.
        var device = stem.Split('.')[0].TrimEnd(' ');
        return ReservedNames.Contains(device) ? "is a reserved Windows device name" : null;
    }

    /// <summary>The characters no Windows file name holds: control characters and <c>&lt; &gt; : " / \ | ? *</c>.</summary>
    private static readonly char[] InvalidCharacters =
        [.. Enumerable.Range(0, 32).Select(i => (char)i), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];
}
