namespace OneNoteMcp.Core;

/// <summary>
/// The check on a new section's relative file name before <c>OpenHierarchy</c> creates it by parent ID (AD-7
/// <c>Create</c> row; spike 5.1 constraint 5): one file name ending <c>.one</c>, with no <c>..</c>, no <c>\</c> or
/// <c>/</c>, and no character or reserved device name a Windows file name can't hold. The gateway refuses a name that
/// fails it with <see cref="ErrorCode.InvalidArgument"/> before COM, so OneNote's path errors (<c>0x80042004</c>,
/// <c>0x80042006</c>) are never reached through a bad name.
/// </summary>
public static class SectionFileNames
{
    /// <summary>The extension OneNote gives a section file.</summary>
    public const string Extension = ".one";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Null when <paramref name="fileName"/> is a valid relative section file name; otherwise what is wrong with it, as a
    /// phrase that never quotes the name (such as <c>"contains '..'"</c>).
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

        if (fileName.Contains("..", StringComparison.Ordinal))
        {
            return "contains '..'";
        }

        if (fileName.Contains('\\', StringComparison.Ordinal) || fileName.Contains('/', StringComparison.Ordinal))
        {
            return "contains a path separator";
        }

        if (fileName.IndexOfAny(InvalidCharacters) >= 0)
        {
            return "contains a character a Windows file name can't hold";
        }

        var stem = fileName[..^Extension.Length];
        if (stem.Trim().Length == 0)
        {
            return "has no name before " + Extension;
        }

        if (stem.EndsWith(' ') || stem.EndsWith('.') || fileName.StartsWith(' ') || fileName.StartsWith('.'))
        {
            return "starts or ends with a space or a dot";
        }

        var device = stem.Split('.')[0];
        return ReservedNames.Contains(device) ? "is a reserved Windows device name" : null;
    }

    /// <summary>The characters no Windows file name holds: control characters and <c>&lt; &gt; : " / \ | ? *</c>.</summary>
    private static readonly char[] InvalidCharacters =
        [.. Enumerable.Range(0, 32).Select(i => (char)i), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];
}
