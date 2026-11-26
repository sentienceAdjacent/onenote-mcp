using System.Globalization;
using System.Text;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// The one rule for text the server puts on the user's screen as given (a path in a dialog, the section picker's title and
/// description; round-3 retrospective F6): no character in it may hide other characters or start a new line.
/// </summary>
public static class ScreenText
{
    /// <summary>
    /// True for a control character (line breaks, tabs, bell…), an invisible formatting character (bidi overrides,
    /// zero-width marks, the U+E0000 tag block: Unicode category Cf), and the line and paragraph separators (U+2028, U+2029).
    /// </summary>
    public static bool IsHidden(Rune r) =>
        Rune.IsControl(r)
        || Rune.GetUnicodeCategory(r) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>
    /// True when <paramref name="text"/> holds any code point <see cref="IsHidden(Rune)"/> is true for, or a lone surrogate.
    /// It reads by code point, not by UTF-16 unit, so a hidden character outside the BMP (a tag character) is caught too.
    /// </summary>
    public static bool HasHidden(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (var i = 0; i < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i), out var r, out var used) != System.Buffers.OperationStatus.Done)
                return true;
            if (IsHidden(r))
                return true;
            i += used;
        }
        return false;
    }
}
