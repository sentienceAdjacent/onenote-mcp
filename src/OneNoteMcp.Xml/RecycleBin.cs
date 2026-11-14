using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>One page in a notebook's Deleted Pages (<see cref="RecycleBin.DeletedPages"/>): its <c>ID</c> and <c>name</c>, unchanged.</summary>
public sealed record DeletedPage(string Id, string Name);

/// <summary>
/// A notebook's recycle bin, as story 5.5's soft delete reads it (AD-7 <c>SoftDelete</c>; spike 5.1 constraint 8): the
/// <c>OneNote_RecycleBin</c> section group (<c>isRecycleBin</c>), the Deleted Pages section in it (<c>isDeletedPages</c>),
/// and the pick of a soft-deleted page's new ID there (Phill, 2026-11-13, 5.5 plan Q3).
/// </summary>
public static class RecycleBin
{
    /// <summary>
    /// The <c>ID</c> of the recycle-bin section group directly under the root of <paramref name="notebookChildrenXml"/>
    /// (a notebook read at <c>Children</c> scope), or null when the notebook has none yet (a notebook gets one at its first
    /// delete). Throws <see cref="OneNoteXmlException"/> when the XML isn't well-formed or has no root.
    /// </summary>
    public static string? BinId(string notebookChildrenXml)
    {
        var root = OneNoteXml.Parse(notebookChildrenXml).Root ?? throw new OneNoteXmlException("The XML has no root element.");
        return root.Elements(OneNoteXml.One + "SectionGroup").FirstOrDefault(IsBin) is { } bin
            ? (string?)bin.Attribute("ID") ?? string.Empty
            : null;
    }

    /// <summary>
    /// Every <c>one:Page</c> of every Deleted Pages section (<c>isDeletedPages</c>) in <paramref name="binPagesXml"/> (the
    /// recycle bin read at <c>Pages</c> scope), in document order. Pages of recycled sections aren't listed. Throws
    /// <see cref="OneNoteXmlException"/> when the XML isn't well-formed.
    /// </summary>
    public static IReadOnlyList<DeletedPage> DeletedPages(string binPagesXml) =>
        [.. OneNoteXml.Parse(binPagesXml)
            .Descendants(OneNoteXml.One + "Section")
            .Where(s => OneNoteXml.IsTrue((string?)s.Attribute("isDeletedPages")))
            .SelectMany(s => s.Elements(OneNoteXml.One + "Page"))
            .Select(p => new DeletedPage((string?)p.Attribute("ID") ?? string.Empty, (string?)p.Attribute("name") ?? string.Empty))];

    /// <summary>
    /// Q3 (a): the soft-deleted page's new ID in Deleted Pages, from the IDs there <paramref name="before"/> and
    /// <paramref name="after"/> the delete, both read under the write lock. One new ID is it; several are narrowed to those
    /// named <paramref name="pageName"/> (ordinal, as OneNote stores it); anything but exactly one is null.
    /// </summary>
    public static string? PickNewPage(IReadOnlyCollection<DeletedPage> before, IReadOnlyCollection<DeletedPage> after, string pageName)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var known = before.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var added = after.Where(p => p.Id.Length > 0 && !known.Contains(p.Id)).DistinctBy(p => p.Id, StringComparer.Ordinal).ToList();
        if (added.Count == 1)
        {
            return added[0].Id;
        }

        var named = added.Where(p => string.Equals(p.Name, pageName, StringComparison.Ordinal)).ToList();
        return named.Count == 1 ? named[0].Id : null;
    }

    /// <summary>True for the recycle-bin section group: <c>isRecycleBin</c>, or a section group named <c>OneNote_RecycleBin</c>.</summary>
    internal static bool IsBin(XElement element) =>
        element.Name == OneNoteXml.One + "SectionGroup"
        && (OneNoteXml.IsTrue((string?)element.Attribute("isRecycleBin"))
            || string.Equals((string?)element.Attribute("name"), NotebookSections.RecycleBinName, StringComparison.Ordinal));
}
