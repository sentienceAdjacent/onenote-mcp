namespace OneNoteMcp.Core.Access;

/// <summary>
/// Text a dialog shows the user (a review finding on story 5.6, applied to every dialog on 2026-11-14): names are
/// model-writable, so a dialog runs each one through <see cref="Shown"/> before quoting it, and states what the server
/// worked out itself before any name. Where the user types a name to confirm, the typed text is compared with
/// <see cref="Matches"/> against <see cref="Shown"/>'s <c>typed</c> form, never against the raw name.
/// </summary>
public static class DialogText
{
    /// <summary>How many characters of a name a dialog shows, the ellipsis included.</summary>
    public const int MaxNameLength = 80;

    /// <summary>How many characters of a notebook's path a dialog shows, the ellipsis included.</summary>
    public const int MaxPathLength = 260;

    /// <summary>
    /// <paramref name="text"/> as a dialog may show it (a review finding: the names are model-writable, so they mustn't
    /// be able to forge the dialog's sentence or hide what it is about): control, format (Unicode Cf, bidi controls
    /// included), line and paragraph separator characters dropped; <c>"</c> and every quote mark that can pass for it
    /// (<see cref="IsQuoteLike"/>) replaced by <c>'</c>; trimmed; and cut to <paramref name="max"/> characters, ending in
    /// <c>…</c>, without splitting a surrogate pair. <paramref name="typed"/> is what the user types to confirm it: the
    /// same text, without the ellipsis when it was cut.
    /// </summary>
    public static string Shown(string? text, out string typed, int max = MaxNameLength)
    {
        var clean = new System.Text.StringBuilder();
        foreach (var rune in (text ?? string.Empty).EnumerateRunes())
        {
            var category = System.Text.Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)
            {
                continue;
            }

            clean.Append(IsQuoteLike(rune, category) ? "'" : rune.ToString());
        }

        var shown = clean.ToString().Trim();
        if (shown.Length <= max)
        {
            typed = shown;
            return shown;
        }

        var cut = max - 1;
        if (char.IsHighSurrogate(shown[cut - 1]))
        {
            cut--;
        }

        typed = shown[..cut].TrimEnd();
        return typed + "…";
    }

    /// <summary>The typed-text check: trimmed, compared ignoring case; an empty answer never matches.</summary>
    public static bool Matches(string? typed, string expected) =>
        typed?.Trim() is { Length: > 0 } text && string.Equals(text, expected?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The marks that can pass for the dialog's own <c>"</c> (fresh review, 2026-11-14): every initial or final quote
    /// (Unicode Pi, Pf: “ ” ‘ ’ ‛ ‟ « » ‹ › and the rest), plus <see cref="QuoteLikes"/>, the quote marks Unicode files as
    /// opening or closing brackets (Ps, Pe) or other punctuation and symbols. Ordinary brackets, also Ps and Pe, stay.
    /// </summary>
    private static bool IsQuoteLike(System.Text.Rune rune, System.Globalization.UnicodeCategory category) =>
        category is System.Globalization.UnicodeCategory.InitialQuotePunctuation or System.Globalization.UnicodeCategory.FinalQuotePunctuation
        || QuoteLikes.Contains(rune.Value);

    /// <summary>
    /// Quote look-alikes outside Pi and Pf: <c>"</c>; the low-9 quotes ‚ „ and ⹂; the CJK corner brackets 「 」 『 』, their
    /// halfwidth and vertical forms, and the double prime quotes 〝 〞 〟; full-width ＂ and ＇; primes ″ ‶ ‴; ditto 〃; Hebrew
    /// gershayim ״; modifier double prime ʺ and double acute ˝; and the quote ornaments ❛ ❜ ❝ ❞ 🙶 🙷 🙸.
    /// </summary>
    private static readonly HashSet<int> QuoteLikes =
    [
        '"', 0x201A, 0x201E, 0x2E42,
        0x300C, 0x300D, 0x300E, 0x300F, 0xFF62, 0xFF63, 0xFE41, 0xFE42, 0xFE43, 0xFE44, 0x301D, 0x301E, 0x301F,
        0xFF02, 0xFF07, 0x2033, 0x2034, 0x2036, 0x3003, 0x05F4, 0x02BA, 0x02DD,
        0x275B, 0x275C, 0x275D, 0x275E, 0x1F676, 0x1F677, 0x1F678,
    ];
}
