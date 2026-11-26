using System.Globalization;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// The one rule for text the server puts on the user's screen as given (a path in a dialog, the section picker's title and
/// description; round-3 retrospective F6): no character in it may hide other characters or start a new line.
/// </summary>
public static class ScreenText
{
    /// <summary>
    /// True for a control character (line breaks, tabs, bell…), an invisible formatting character (bidi overrides,
    /// zero-width marks: Unicode category Cf), and the line and paragraph separators (U+2028, U+2029).
    /// </summary>
    public static bool IsHidden(char c) =>
        char.IsControl(c)
        || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>True when <paramref name="text"/> holds any character <see cref="IsHidden(char)"/> is true for.</summary>
    public static bool HasHidden(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Any(IsHidden);
    }
}
