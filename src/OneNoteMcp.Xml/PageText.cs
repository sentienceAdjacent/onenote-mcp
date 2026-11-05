using System.Net;
using System.Text.RegularExpressions;

namespace OneNoteMcp.Xml;

/// <summary>
/// The text check behind <c>find_pages</c> (story 9): with unindexed pages included, <c>FindPages</c> returns every page
/// under the start node, so the server checks each candidate's own text.
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
            .Select(t => WebUtility.HtmlDecode(Markup().Replace(t.Value, string.Empty))));

    /// <summary>True when every term appears, ignoring case, in <paramref name="name"/> or in <paramref name="text"/>.</summary>
    public static bool ContainsAll(IReadOnlyList<string> terms, string name, string text)
    {
        ArgumentNullException.ThrowIfNull(terms);
        return terms.All(term =>
            (name ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase)
            || (text ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    // one:T holds an HTML fragment (spans, links); its tags are markup, not text.
    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Markup();
}
