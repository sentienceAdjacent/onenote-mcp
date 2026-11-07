using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>
/// The text check behind <c>find_pages</c> (story 9): with unindexed pages included, <c>FindPages</c> returns every page
/// under the start node, so the server checks each candidate's own text. The same text rule labels the entries of an
/// oversized page's index (AD-20).
/// </summary>
public static partial class PageText
{
    /// <summary>The whitespace-separated terms of a query. An empty or all-whitespace query has none.</summary>
    public static IReadOnlyList<string> Terms(string query) =>
        (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The text of every <c>one:T</c> in page XML, markup stripped and entities decoded, one per line so a term never
    /// matches across two of them. Throws <see cref="OneNoteXmlException"/> when the XML isn't well-formed.
    /// </summary>
    public static string Of(string pageXml) =>
        string.Join('\n', OneNoteXml.Parse(pageXml)
            .Descendants(OneNoteXml.One + "T")
            .Select(t => Clean(t.Value)));

    /// <summary>True when every term appears, ignoring case, in <paramref name="name"/> or in <paramref name="text"/>.</summary>
    public static bool ContainsAll(IReadOnlyList<string> terms, string name, string text)
    {
        ArgumentNullException.ThrowIfNull(terms);
        return terms.All(term =>
            (name ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase)
            || (text ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The first <paramref name="maxChars"/> characters of <paramref name="element"/>'s text by <see cref="Of"/>'s rule:
    /// the <c>one:T</c> elements at or below it, markup stripped, entities decoded, one per line. Stops reading once it
    /// has enough, so a long element costs no more than its first few <c>one:T</c>s.
    /// </summary>
    internal static string Label(XElement element, int maxChars)
    {
        var builder = new StringBuilder();
        foreach (var t in element.DescendantsAndSelf(OneNoteXml.One + "T"))
        {
            if (builder.Length >= maxChars)
            {
                break;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(Clean(t.Value));
        }

        if (builder.Length <= maxChars)
        {
            return builder.ToString();
        }

        // Never end on half a surrogate pair.
        var cut = char.IsHighSurrogate(builder[maxChars - 1]) ? maxChars - 1 : maxChars;
        return builder.ToString(0, cut);
    }

    // one:T holds an HTML fragment (spans, links); its tags are markup, not text.
    private static string Clean(string value) => WebUtility.HtmlDecode(Markup().Replace(value, string.Empty));

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Markup();
}
