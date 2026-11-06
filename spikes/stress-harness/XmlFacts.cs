using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StressHarness;

/// <summary>A page as the hierarchy shows it. <see cref="Name"/> stays in memory only.</summary>
internal sealed record PageNode(string Id, string SectionId, string Name, bool InRecycleBin);

internal sealed record SectionNode(string Id, bool Locked, bool InRecycleBin);

internal sealed record NotebookModel(string NotebookId, IReadOnlyList<SectionNode> Sections, IReadOnlyList<PageNode> Pages);

/// <summary>What the harness keeps from a page's piBasic read: in memory only, never written.</summary>
internal sealed class PageFacts
{
    public required string BasicHash { get; set; }

    public required string Text { get; set; }

    public required IReadOnlyList<string> MetaNames { get; set; }

    public required IReadOnlyList<string> CallbackIds { get; set; }

    public required int Images { get; set; }

    public required int InsertedFiles { get; set; }
}

/// <summary>
/// The harness's own XML reading, kept separate from OneNoteMcp.Xml. The find_pages text rule here (one:T values with
/// tags stripped, entities decoded, joined by newlines) is the same rule the server applies, so search comparisons check
/// that the server applies it consistently, not that the rule is right. Exceptions are reported by type name only.
/// </summary>
internal static partial class XmlFacts
{
    public static readonly XNamespace One = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    public static XDocument Parse(string xml) => XDocument.Parse(xml, LoadOptions.PreserveWhitespace);

    public static NotebookModel Model(string notebookId, string pagesScopeXml)
    {
        var doc = Parse(pagesScopeXml);
        var sections = new List<SectionNode>();
        var pages = new List<PageNode>();
        foreach (var s in doc.Descendants(One + "Section"))
        {
            var inBin = InBin(s);
            sections.Add(new SectionNode(Attr(s, "ID"), IsTrue(Attr(s, "locked")), inBin));
            foreach (var p in s.Elements(One + "Page"))
            {
                pages.Add(new PageNode(Attr(p, "ID"), Attr(s, "ID"), Attr(p, "name"), inBin || IsTrue(Attr(p, "isInRecycleBin"))));
            }
        }

        return new NotebookModel(notebookId, sections, pages);
    }

    /// <summary>The element or any Section/SectionGroup above it has isInRecycleBin or isRecycleBin set.</summary>
    public static bool InBin(XElement e)
    {
        for (var x = e; x is not null; x = x.Parent)
        {
            if (x.Name == One + "Section" || x.Name == One + "SectionGroup" || x.Name == One + "Page")
            {
                if (IsTrue(Attr(x, "isInRecycleBin")) || IsTrue(Attr(x, "isRecycleBin")))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static HashSet<string> Ids(string xml, string element) =>
        [.. Parse(xml).Descendants(One + element).Select(e => Attr(e, "ID"))];

    /// <summary>Outermost one:Page IDs of a search result, in document order.</summary>
    public static List<string> SearchPageIds(string xml) =>
        [.. Parse(xml).Descendants(One + "Page").Select(e => Attr(e, "ID"))];

    public static PageFacts Facts(string basicXml)
    {
        var doc = Parse(basicXml);
        var root = doc.Root!;
        var text = string.Join('\n', doc.Descendants(One + "T").Select(t => WebUtility.HtmlDecode(Tags().Replace(t.Value, string.Empty))));
        return new PageFacts
        {
            BasicHash = Hash(basicXml),
            Text = text,
            MetaNames = [.. root.Elements(One + "Meta").Select(m => Attr(m, "name"))],
            CallbackIds = [.. doc.Descendants(One + "CallbackID").Select(c => Attr(c, "callbackID")).Where(c => c.Length > 0)],
            Images = doc.Descendants(One + "Image").Count(),
            InsertedFiles = doc.Descendants(One + "InsertedFile").Count(),
        };
    }

    /// <summary>Counts in a piBinaryData read: images, attachments and the total characters of one:Data.</summary>
    public static (int Images, int InsertedFiles, long DataChars) BinaryCounts(string binaryXml)
    {
        var doc = Parse(binaryXml);
        return (
            doc.Descendants(One + "Image").Count(),
            doc.Descendants(One + "InsertedFile").Count(),
            doc.Descendants(One + "Data").Sum(d => (long)d.Value.Length));
    }

    /// <summary>Words of 3+ letters or digits, for drawing search queries. Kept in memory only.</summary>
    public static IReadOnlyList<string> Words(string text) =>
        [.. Word().Matches(text).Select(m => m.Value)];

    public static bool ContainsAll(IReadOnlyList<string> terms, string name, string text) =>
        terms.All(t => name.Contains(t, StringComparison.OrdinalIgnoreCase) || text.Contains(t, StringComparison.OrdinalIgnoreCase));

    public static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static string Attr(XElement e, string name) => (string?)e.Attribute(name) ?? string.Empty;

    private static bool IsTrue(string v) => v is "true" or "1";

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();

    [GeneratedRegex(@"[\p{L}\p{N}]{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex Word();
}
