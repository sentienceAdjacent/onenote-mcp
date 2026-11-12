using System.Xml;
using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>A file path a page write names (AD-7 user-gated file paths): the value, and where it sits.</summary>
/// <param name="Value">The attribute's value, exactly as sent.</param>
/// <param name="OwnerObjectId">
/// The <c>objectID</c> of the element the path belongs to: the element carrying the attribute, or, for a
/// <c>one:File</c>, its parent object. Null when that element has none: such a path is always new.
/// </param>
/// <param name="Element">The local name of the element carrying the attribute.</param>
/// <param name="Attribute">The attribute's local name: <c>path</c>, <c>pathSource</c> or <c>pathCache</c>.</param>
public sealed record SentFilePath(string Value, string? OwnerObjectId, string Element, string Attribute);

/// <summary>
/// The parts of a page write's step 1 that read its XML before the page is read (story 2.9 security review; AD-7, AD-16,
/// AD-17): the DOCTYPE-safe parse, whether it sends any <c>objectID</c>, and which file paths it names and which of them
/// are new. Every parse prohibits DTD processing and uses no resolver.
/// </summary>
public static class PageWriteScan
{
    private static readonly XName File = OneNoteXml.One + "File";

    /// <summary>The attributes that name a file OneNote reads into the page.</summary>
    private static readonly HashSet<string> PathAttributes = new(StringComparer.Ordinal) { "path", "pathSource" };

    /// <summary>On these elements <c>pathCache</c> names a file too, so it is gated like <c>pathSource</c>.</summary>
    private static readonly HashSet<string> FileElements = new(StringComparer.Ordinal) { "InsertedFile", "MediaFile" };

    /// <summary>
    /// The first check of a page write's step 1 (AD-16): null when <paramref name="xml"/> parses with DTD processing
    /// prohibited; otherwise the problem, as the schema gate reports it: a DOCTYPE, or the position where the XML stops
    /// being well-formed.
    /// </summary>
    public static SchemaProblem? ParseProblem(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        if (OneNoteXml.HasDoctype(xml))
        {
            return new SchemaProblem(0, 0, null) { IsDoctype = true };
        }

        try
        {
            OneNoteXml.Load(xml, LoadOptions.None);
            return null;
        }
        catch (XmlException ex)
        {
            return new SchemaProblem(ex.LineNumber, ex.LinePosition, null);
        }
    }

    /// <summary>True when any element of <paramref name="xml"/> carries a non-empty <c>objectID</c>. Well-formed XML only.</summary>
    public static bool SendsObjectId(string xml) =>
        OneNoteXml.Parse(xml).Descendants().Any(e => !string.IsNullOrEmpty((string?)e.Attribute("objectID")));

    /// <summary>
    /// Every file path <paramref name="xml"/> names, in document order: each <c>path</c> or <c>pathSource</c> attribute
    /// (no namespace) on any element, and <c>pathCache</c> on a <c>one:InsertedFile</c> or <c>one:MediaFile</c>. Empty
    /// values are skipped. Well-formed XML only.
    /// </summary>
    public static IReadOnlyList<SentFilePath> FilePaths(string xml)
    {
        var paths = new List<SentFilePath>();
        foreach (var element in OneNoteXml.Parse(xml).Descendants())
        {
            var fileElement = element.Name.Namespace == OneNoteXml.One && FileElements.Contains(element.Name.LocalName);
            foreach (var attribute in element.Attributes())
            {
                if (attribute.IsNamespaceDeclaration
                    || attribute.Name.Namespace != XNamespace.None
                    || attribute.Value.Length == 0
                    || !(PathAttributes.Contains(attribute.Name.LocalName) || (fileElement && attribute.Name.LocalName == "pathCache")))
                {
                    continue;
                }

                paths.Add(new SentFilePath(attribute.Value, Owner(element), element.Name.LocalName, attribute.Name.LocalName));
            }
        }

        return paths;
    }

    /// <summary>
    /// The paths in <paramref name="sentXml"/> that are new (AD-7 as the 5.2 re-check words it): every path except one
    /// sent unchanged, with the same attribute, on the same existing element (the same owner <c>objectID</c>) as in
    /// <paramref name="readXml"/>. A path on an element without an <c>objectID</c> is always new, even when the same value
    /// is elsewhere on the page. With <paramref name="readXml"/> null (no read grant), every path is new.
    /// </summary>
    public static IReadOnlyList<SentFilePath> NewFilePaths(string sentXml, string? readXml)
    {
        var sent = FilePaths(sentXml);
        if (readXml is null)
        {
            return sent;
        }

        var onPage = FilePaths(readXml)
            .Where(p => p.OwnerObjectId is not null)
            .Select(Key)
            .ToHashSet(StringComparer.Ordinal);
        return [.. sent.Where(p => p.OwnerObjectId is null || !onPage.Contains(Key(p)))];
    }

    private static string Key(SentFilePath path) => string.Join('\n', path.OwnerObjectId, path.Element, path.Attribute, path.Value);

    // --- The HTML stopgap (Phill, 2026-11-12, until the html-local-ref probe runs) ---------------------------------------

    private static readonly XName HtmlBlock = OneNoteXml.One + "HTMLBlock";
    private static readonly XName T = OneNoteXml.One + "T";
    private static readonly XName OE = OneNoteXml.One + "OE";

    /// <summary>
    /// The HTML stopgap's findings in <paramref name="sentXml"/> (Phill, 2026-11-12): how many <c>one:HTMLBlock</c>
    /// elements it sends, and how many <c>one:T</c> texts hold HTML it refuses (<see cref="HasRefusedHtml"/>). An
    /// HTMLBlock or a text sent back unchanged on the same existing element (the same nearest owner <c>objectID</c>, the
    /// same content) as in <paramref name="readXml"/> doesn't count; with <paramref name="readXml"/> null (no read grant)
    /// everything counts. Counts only: the texts never leave this method. Well-formed XML only.
    /// </summary>
    public static (int HtmlBlocks, int RefusedTexts) HtmlProblems(string sentXml, string? readXml)
    {
        var sent = OneNoteXml.Parse(sentXml);
        var read = readXml is null ? null : OneNoteXml.Parse(readXml);

        var readBlocks = read?.Descendants(HtmlBlock).Select(BlockKey).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        var blocks = sent.Descendants(HtmlBlock).Count(b => BlockKey(b) is not { } key || !readBlocks.Contains(key));

        var readTexts = read?.Descendants(T).Select(TextKey).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        var references = sent.Descendants(T).Count(t => HasRefusedHtml(t.Value) && (TextKey(t) is not { } key || !readTexts.Contains(key)));
        return (blocks, references);
    }

    /// <summary>
    /// True when <paramref name="text"/> (a <c>one:T</c>'s HTML) is refused by the deny-by-default stopgap: a
    /// <c>file:</c> URL, a resource-loading tag, a URL that isn't an absolute http, https, mailto or onenote URL, or CSS
    /// that could load something, at any stage of entity decoding. The rules are on <see cref="HtmlStopgap"/>.
    /// </summary>
    public static bool HasRefusedHtml(string text) => HtmlStopgap.IsRefused(text);

    /// <summary>An HTMLBlock's identity for the exemption: its nearest owner's <c>objectID</c> and its content; null without an owner.</summary>
    private static string? BlockKey(XElement block) =>
        NearestObjectId(block) is { } owner ? owner + "\n" + block.ToString(SaveOptions.DisableFormatting) : null;

    /// <summary>A text's identity for the exemption: its OE's <c>objectID</c> and its text; null when the OE has none.</summary>
    private static string? TextKey(XElement text) =>
        text.Parent is { } oe && oe.Name == OE && (string?)oe.Attribute("objectID") is { Length: > 0 } id ? id + "\n" + text.Value : null;

    private static string? NearestObjectId(XElement element) =>
        element.Ancestors().Select(a => (string?)a.Attribute("objectID")).FirstOrDefault(id => !string.IsNullOrEmpty(id));

    private static string? Owner(XElement element)
    {
        var holder = element.Name == File ? element.Parent : element;
        return (string?)holder?.Attribute("objectID") is { Length: > 0 } id ? id : null;
    }
}
