using System.Globalization;
using System.Text.RegularExpressions;

namespace OneNoteMcp.Xml;

/// <summary>
/// The HTML stopgap's scan of one <c>one:T</c>'s HTML (Phill, 2026-11-12, until the <c>html-local-ref</c> probe shows
/// what OneNote fetches). An allowlist (Phill, 2026-11-12, after the bypass review of <c>e0ba25b</c>): at every stage
/// of entity decoding, the text is refused for a <c>file:</c> URL, a comment, declaration or processing instruction
/// (<c>&lt;!</c>, <c>&lt;?</c>), or any tag or attribute outside the small set OneNote's own page text uses
/// (<see cref="AllowedTags"/>, <see cref="GlobalAttributes"/>, <c>href</c> on <c>a</c>, and <c>face</c>, <c>size</c>,
/// <c>color</c> on <c>font</c>). An attribute value is refused for a backtick (old IE quotes with it), an entity
/// reference .NET can't decode left after the last stage (such as <c>&amp;lpar;</c>), an <c>href</c> that isn't an
/// absolute http, https or mailto URL or an allowed <c>onenote:</c> link, or a <c>style</c> with any CSS function but
/// an rgb or hsl colour (so no <c>url()</c>), a backslash escape or an at-rule. Text that still changes after five
/// rounds of decoding is refused. Tags are found twice, by an HTML5-style tokenizer (quotes, <c>/</c> separators) and
/// by a plain regex scan, and both results count. Refusing too much is the safe failure here.
/// </summary>
internal static class HtmlStopgap
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>The tags OneNote's own page text uses: inline formatting, paragraphs, line breaks and links.</summary>
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "b", "br", "cite", "code", "del", "em", "font", "i", "ins", "p", "s", "small", "span", "strike", "strong",
        "sub", "sup", "u",
    };

    /// <summary>The attributes any allowed tag may carry.</summary>
    private static readonly HashSet<string> GlobalAttributes = new(StringComparer.OrdinalIgnoreCase) { "style", "lang", "dir" };

    /// <summary>The attributes only <c>font</c> may carry.</summary>
    private static readonly HashSet<string> FontAttributes = new(StringComparer.OrdinalIgnoreCase) { "face", "size", "color" };

    /// <summary>The only CSS functions a style attribute may call: colours, which never load anything.</summary>
    private static readonly HashSet<string> CssFunctions = new(StringComparer.OrdinalIgnoreCase) { "rgb", "rgba", "hsl", "hsla" };

    private static readonly Regex FileUrl = new("(?<![a-z0-9+.-])file:", Options);

    /// <summary>A comment, conditional comment, declaration, CDATA section or processing instruction.</summary>
    private static readonly Regex MarkupDeclaration = new("<[!?]", Options);

    /// <summary>A named reference left after the last decoding stage: one .NET doesn't know, which an HTML5 parser may.</summary>
    private static readonly Regex UnknownReference = new("&[a-z][a-z0-9]*;", Options);

    private static readonly Regex AllowedUrl = new("^(?:https?://|mailto:)", Options);

    /// <summary>An <c>onenote:</c> link: an optional web-notebook address (with a host), then an optional <c>#</c> part.</summary>
    private static readonly Regex OneNoteLink = new(
        @"^onenote:(?<web>https?://[a-z0-9-]+(?:\.[a-z0-9-]+)+(?::[0-9]+)?(?:[/?][^#]*)?)?(?:#(?<fragment>.*))?$", Options | RegexOptions.Singleline);

    /// <summary>The scheme and host at the start of a web-notebook address, so the path checks look only at what follows.</summary>
    private static readonly Regex WebAuthority = new("^https?://[^/?#]*", Options);

    /// <summary>What lets an onenote: link name a notebook file: base-path, a backslash, //, a .. segment, file: or a drive.</summary>
    private static readonly Regex OneNotePathMarker = new(
        @"base-path|\\|//|file:|(?:^|[/=&])\.\.(?:[/&?]|$)|(?<![a-z0-9])[a-z]:[\\/]", Options);

    /// <summary>The parameters OneNote's own page links carry after the title: GUID ids, a short hex index, and <c>end</c>.</summary>
    private static readonly Regex OneNoteParameter = new(
        @"^(?:(?:section-id|page-id|object-id)=\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}|[0-9a-f]{1,8}|end)$", Options);

    private static readonly Regex ParameterSeparator = new("&(?:amp;)?", Options);

    private static readonly Regex NaiveTag = new("<([a-zA-Z][^\\s/>]*)([^>]*)(?:>|$)", RegexOptions.CultureInvariant);

    private static readonly Regex NaiveAttribute = new(
        """([^\s"'=<>/]+)(?:\s*=\s*(?:"([^"]*)"?|'([^']*)'?|([^\s>]*)))?""", RegexOptions.CultureInvariant);

    private static readonly Regex NumericReference = new("&#(?:[xX]([0-9a-fA-F]+)|([0-9]+));?", RegexOptions.CultureInvariant);

    /// <summary>The legacy named references HTML decodes without a semicolon, where they change the markup.</summary>
    private static readonly Regex LegacyReference = new("&(lt|gt|quot|amp)(?![;a-zA-Z0-9=])", Options);

    private sealed record Tag(string Name, IReadOnlyList<(string Name, string? Value)> Attributes);

    public static bool IsRefused(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Stages(text) is not { } stages)
        {
            return true;
        }

        foreach (var stage in stages)
        {
            if (FileUrl.IsMatch(stage) || MarkupDeclaration.IsMatch(stage))
            {
                return true;
            }

            if (Tokenize(stage).Concat(NaiveTags(stage)).Any(IsRefused))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRefused(Tag tag)
    {
        if (!AllowedTags.Contains(tag.Name))
        {
            return true;
        }

        foreach (var (name, value) in tag.Attributes)
        {
            var isHref = name.Equals("href", StringComparison.OrdinalIgnoreCase);
            var allowed = GlobalAttributes.Contains(name)
                || (isHref && tag.Name.Equals("a", StringComparison.OrdinalIgnoreCase))
                || (FontAttributes.Contains(name) && tag.Name.Equals("font", StringComparison.OrdinalIgnoreCase));
            if (!allowed || Stages(value ?? string.Empty) is not { } stages
                || stages.Any(v => v.Contains('`', StringComparison.Ordinal)) || UnknownReference.IsMatch(stages[^1]))
            {
                return true;
            }

            if ((isHref && stages.Any(v => !IsAllowedUrl(v)))
                || (name.Equals("style", StringComparison.OrdinalIgnoreCase) && stages.Any(IsRefusedCss)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True for an absolute http, https or mailto URL, or an allowed onenote link. As a browser does, tabs and newlines anywhere, and
    /// controls and spaces at either end, are removed first; any other control character refuses the URL.
    /// </summary>
    private static bool IsAllowedUrl(string value)
    {
        var url = value.Replace("\t", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Trim(Enumerable.Range(0, 0x21).Select(c => (char)c).ToArray());
        return url.Length > 0 && !url.Any(c => c < 0x20 || c is >= '\u007f' and <= '\u009f')
            && (AllowedUrl.IsMatch(url) || IsAllowedOneNoteLink(url));
    }

    /// <summary>
    /// An allowlist for <c>onenote:</c> links: optionally an http(s) web-notebook address with a host, then optionally a
    /// <c>#</c> part holding a page title and only the parameters OneNote's own links carry (<see cref="OneNoteParameter"/>).
    /// Each part must stop changing within four rounds of percent-decoding, and its decoded form must hold no backslash and
    /// no <c>base-path</c> (which OneNote's links to local notebooks carry); the decoded title must hold no <c>&amp;</c>,
    /// <c>=</c> or <c>#</c>, so no parameter hides in it. Links to local, relative or network notebooks are refused.
    /// </summary>
    private static bool IsAllowedOneNoteLink(string url)
    {
        var link = OneNoteLink.Match(url);
        var web = link.Groups["web"];
        var fragment = link.Groups["fragment"];
        if (!link.Success || (!web.Success && !fragment.Success))
        {
            return false;
        }

        if (web.Success && (FullyDecoded(web.Value) is not { } address || address.Contains('&', StringComparison.Ordinal)
            || OneNotePathMarker.IsMatch(WebAuthority.Replace(address, string.Empty))
            || !Uri.TryCreate(web.Value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.IsUnc
            || !IsOneDriveOrSharePoint(uri.Host)))
        {
            return false;
        }

        if (!fragment.Success)
        {
            return true;
        }

        // Split on & and on &amp;: the value is checked before entity decoding too, where the separators are still &amp;.
        var parts = ParameterSeparator.Split(fragment.Value);
        return FullyDecoded(parts[0]) is { } title && !OneNotePathMarker.IsMatch(title) && title.IndexOfAny(['&', '=', '#']) < 0
            && parts.Skip(1).All(OneNoteParameter.IsMatch);
    }

    /// <summary>
    /// The web-notebook hosts an <c>onenote:</c> link may name (Phill, 2026-11-12): OneDrive (<c>d.docs.live.net</c>) and
    /// SharePoint (<c>*.sharepoint.com</c>). OneNote opens such a link as a notebook, and on an intranet host Windows may
    /// send the user's credentials.
    /// </summary>
    private static bool IsOneDriveOrSharePoint(string host) =>
        host.Equals("d.docs.live.net", StringComparison.OrdinalIgnoreCase)
        || (host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase) && host.Length > ".sharepoint.com".Length);

    /// <summary>The text percent-decoded until it stops changing, or null when it still changes after four rounds.</summary>
    private static string? FullyDecoded(string text)
    {
        for (var i = 0; i < 4; i++)
        {
            var next = Uri.UnescapeDataString(text);
            if (next == text)
            {
                return text;
            }

            text = next;
        }

        return null;
    }

    /// <summary>True when a style value has a backslash escape, an at-rule, expression(), or any function but a colour.</summary>
    private static bool IsRefusedCss(string css)
    {
        if (css.Contains('\\', StringComparison.Ordinal) || css.Contains('@', StringComparison.Ordinal)
            || css.Contains("expression", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        for (var i = css.IndexOf('(', StringComparison.Ordinal); i >= 0; i = css.IndexOf('(', i + 1))
        {
            var start = i;
            while (start > 0 && (char.IsAsciiLetterOrDigit(css[start - 1]) || css[start - 1] is '-' or '_'))
            {
                start--;
            }

            if (!CssFunctions.Contains(css[start..i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The text as sent, then decoded again and again until it stops changing (at most five stages); null when it still
    /// changes after that, which the scan refuses.
    /// </summary>
    private static List<string>? Stages(string text)
    {
        var stages = new List<string> { text };
        for (var i = 0; i < 5; i++)
        {
            var next = Decode(stages[^1]);
            if (next == stages[^1])
            {
                return stages;
            }

            stages.Add(next);
        }

        return null;
    }

    /// <summary>One round of HTML5-style character-reference decoding, including numeric and legacy references with no semicolon.</summary>
    private static string Decode(string text)
    {
        var numeric = NumericReference.Replace(text, m =>
        {
            var hex = m.Groups[1].Success;
            // HTML5 allows any number of leading zeros, so they don't count toward the length.
            var digits = (hex ? m.Groups[1].Value : m.Groups[2].Value).TrimStart('0');
            if (digits.Length == 0 || digits.Length > 8
                || !int.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var cp)
                || cp == 0 || cp > 0x10FFFF || cp is >= 0xD800 and <= 0xDFFF)
            {
                return "\uFFFD";
            }

            return char.ConvertFromUtf32(cp);
        });
        var legacy = LegacyReference.Replace(numeric, m => "&" + m.Groups[1].Value.ToLowerInvariant() + ";");
        return System.Net.WebUtility.HtmlDecode(legacy);
    }

    private static IEnumerable<Tag> NaiveTags(string html)
    {
        foreach (Match tag in NaiveTag.Matches(html))
        {
            var attributes = new List<(string, string?)>();
            foreach (Match a in NaiveAttribute.Matches(tag.Groups[2].Value))
            {
                var value = a.Groups[2].Success ? a.Groups[2].Value
                    : a.Groups[3].Success ? a.Groups[3].Value
                    : a.Groups[4].Success ? a.Groups[4].Value
                    : null;
                attributes.Add((a.Groups[1].Value, value));
            }

            yield return new Tag(tag.Groups[1].Value, attributes);
        }
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    /// <summary>
    /// Start tags as an HTML5 tokenizer sees them: comments (<c>&lt;!--</c> to <c>--&gt;</c> or <c>--!&gt;</c>) and
    /// bogus comments (<c>&lt;!</c>, <c>&lt;?</c>, <c>&lt;/</c> not followed by a letter) are skipped, attribute values
    /// may be quoted, and <c>/</c> separates attributes. A tag cut off at the end of the text still counts.
    /// </summary>
    private static List<Tag> Tokenize(string s)
    {
        var tags = new List<Tag>();
        var n = s.Length;
        var i = 0;
        while (i < n)
        {
            if (s[i] != '<' || i + 1 >= n)
            {
                i++;
                continue;
            }

            var c = s[i + 1];
            if (c == '!' && string.CompareOrdinal(s, i, "<!--", 0, 4) == 0)
            {
                i = CommentEnd(s, i + 4);
                continue;
            }

            if (c is '!' or '?' || (c == '/' && (i + 2 >= n || !char.IsAsciiLetter(s[i + 2]))))
            {
                var close = s.IndexOf('>', i + 1);
                i = close < 0 ? n : close + 1;
                continue;
            }

            var endTag = c == '/';
            var j = endTag ? i + 2 : i + 1;
            if (!char.IsAsciiLetter(s[j]))
            {
                i++;
                continue;
            }

            var nameStart = j;
            while (j < n && !IsSpace(s[j]) && s[j] is not '/' and not '>')
            {
                j++;
            }

            var name = s[nameStart..j];
            var attributes = new List<(string, string?)>();
            while (j < n)
            {
                while (j < n && (IsSpace(s[j]) || s[j] == '/'))
                {
                    j++;
                }

                if (j >= n || s[j] == '>')
                {
                    break;
                }

                var attributeStart = j++;
                while (j < n && !IsSpace(s[j]) && s[j] is not '/' and not '>' and not '=')
                {
                    j++;
                }

                var attributeName = s[attributeStart..j];
                var k = j;
                while (k < n && IsSpace(s[k]))
                {
                    k++;
                }

                string? value = null;
                if (k < n && s[k] == '=')
                {
                    j = k + 1;
                    while (j < n && IsSpace(s[j]))
                    {
                        j++;
                    }

                    if (j < n && s[j] is '"' or '\'')
                    {
                        var quote = s[j];
                        var close = s.IndexOf(quote, j + 1);
                        value = close < 0 ? s[(j + 1)..] : s[(j + 1)..close];
                        j = close < 0 ? n : close + 1;
                    }
                    else
                    {
                        var valueStart = j;
                        while (j < n && !IsSpace(s[j]) && s[j] != '>')
                        {
                            j++;
                        }

                        value = s[valueStart..j];
                    }
                }

                attributes.Add((attributeName, value));
            }

            i = j < n ? j + 1 : n;
            if (!endTag)
            {
                tags.Add(new Tag(name, attributes));
            }
        }

        return tags;
    }

    /// <summary>Where a comment whose text starts at <paramref name="start"/> ends: after <c>--&gt;</c> or <c>--!&gt;</c>; <c>&lt;!--&gt;</c> and <c>&lt;!---&gt;</c> end at once.</summary>
    private static int CommentEnd(string s, int start)
    {
        if (start < s.Length && s[start] == '>')
        {
            return start + 1;
        }

        if (start + 1 < s.Length && s[start] == '-' && s[start + 1] == '>')
        {
            return start + 2;
        }

        var plain = s.IndexOf("-->", start, StringComparison.Ordinal);
        var bang = s.IndexOf("--!>", start, StringComparison.Ordinal);
        return (plain, bang) switch
        {
            ( < 0, < 0) => s.Length,
            ( < 0, _) => bang + 4,
            (_, < 0) => plain + 3,
            _ => plain < bang ? plain + 3 : bang + 4,
        };
    }
}
