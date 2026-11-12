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

    private static string? Owner(XElement element)
    {
        var holder = element.Name == File ? element.Parent : element;
        return (string?)holder?.Attribute("objectID") is { Length: > 0 } id ? id : null;
    }
}
