using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>
/// Hierarchy reads for story 5.5's soft delete (AD-7 <c>SoftDelete</c>, AD-8): the page IDs under a node, and a section
/// group's snapshot XML without protected sections' pages. The target itself and a page's entry in its section are read
/// by <see cref="HierarchyTarget"/>.
/// </summary>
public static class DeleteTargets
{
    /// <summary>The <c>ID</c> of every <c>one:Page</c> in <paramref name="hierarchyXml"/>, in document order. Throws <see cref="OneNoteXmlException"/> when the XML isn't well-formed.</summary>
    public static IReadOnlyList<string> PageIds(string hierarchyXml) =>
        [.. OneNoteXml.Parse(hierarchyXml).Descendants(OneNoteXml.One + "Page").Select(p => (string?)p.Attribute("ID") ?? string.Empty).Where(id => id.Length > 0)];

    /// <summary>
    /// <paramref name="hierarchyXml"/> with the <c>one:Page</c> elements of every protected section (marked, or by a path
    /// <paramref name="underRoot"/> accepts, entry 15) removed and
    /// everything else kept, the protected <c>one:Section</c> elements included (AD-8: a larger node's hierarchy snapshot,
    /// as a read without a grant would show it, AD-4). Unchanged text when there is nothing to remove. Throws
    /// <see cref="OneNoteXmlException"/> when the XML isn't well-formed.
    /// </summary>
    public static string WithoutProtectedPages(string hierarchyXml, Func<string, bool> underRoot)
    {
        ArgumentNullException.ThrowIfNull(underRoot);
        var document = OneNoteXml.Parse(hierarchyXml);
        var pages = document.Descendants(OneNoteXml.One + "Section")
            .Where(s => EncryptedSections.IsProtected(s, underRoot))
            .SelectMany(s => s.Elements(OneNoteXml.One + "Page"))
            .ToList();
        if (pages.Count == 0)
        {
            return hierarchyXml;
        }

        foreach (var page in pages)
        {
            page.Remove();
        }

        return document.Declaration is { } declaration
            ? declaration + Environment.NewLine + document.ToString(SaveOptions.DisableFormatting)
            : document.ToString(SaveOptions.DisableFormatting);
    }
}
