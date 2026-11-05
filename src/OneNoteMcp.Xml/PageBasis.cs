using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>
/// AD-17's basis for a page, computed from its <c>GetPageContent</c> XML: what a read returns as <c>meta.basis</c> and
/// what a write compares.
/// </summary>
/// <remarks>
/// Hashes compare only between reads of the same <c>PageInfo</c> kind. For a cloud page, piBinaryData drops each image
/// with no local binary, so its hashes differ from a piBasic read of the same page. Story 8 and epic 2 compute the basis
/// from piBasic reads.
/// </remarks>
/// <param name="LastModifiedTime">The root <c>one:Page@lastModifiedTime</c>, the exact string OneNote returned (never parsed here), or null when absent.</param>
/// <param name="PageHash">The <see cref="ElementHash"/> of the root <c>one:Page</c>.</param>
/// <param name="ElementHashes">The <see cref="ElementHash"/> of every <c>one:Outline</c> and <c>one:OE</c> that has an <c>objectID</c>, keyed by it.</param>
public sealed record PageBasis(string? LastModifiedTime, string PageHash, IReadOnlyDictionary<string, string> ElementHashes)
{
    private static readonly XName Outline = OneNoteXml.One + "Outline";
    private static readonly XName OE = OneNoteXml.One + "OE";

    /// <summary>
    /// The basis of <paramref name="pageXml"/>. Throws <see cref="OneNoteXmlException"/> when it isn't well-formed or
    /// its root isn't a 2013-namespace <c>one:Page</c>. Outlines and OEs without an <c>objectID</c> are skipped; if an
    /// <c>objectID</c> repeats, the first element in document order keeps it.
    /// </summary>
    public static PageBasis From(string pageXml)
    {
        var root = OneNoteXml.RequireRoot(OneNoteXml.Parse(pageXml), "Page");

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in root.Descendants().Where(e => e.Name == Outline || e.Name == OE))
        {
            if ((string?)element.Attribute("objectID") is { Length: > 0 } objectId && !hashes.ContainsKey(objectId))
            {
                hashes.Add(objectId, ElementHash.Compute(element));
            }
        }

        return new PageBasis((string?)root.Attribute("lastModifiedTime"), ElementHash.Compute(root), hashes);
    }
}
