using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>
/// The root of a soft delete's target read (story 5.5): its kind, <c>ID</c>, <c>name</c>, <c>path</c> and
/// <c>lastModifiedTime</c> (the exact string OneNote wrote, AD-17), and the states AD-7 <c>SoftDelete</c> refuses on:
/// <see cref="IsRecycleBin"/> (<c>isRecycleBin</c>, or a section group named <c>OneNote_RecycleBin</c>),
/// <see cref="InRecycleBin"/> (<c>isInRecycleBin</c> or <c>isDeletedPages</c>), <c>encrypted</c> and <c>locked</c>.
/// </summary>
public sealed record DeleteTarget(HierarchyKind Kind, string Id, string Name, string Path, string? LastModifiedTime, bool IsRecycleBin, bool InRecycleBin, bool Encrypted, bool Locked);

/// <summary>A page as its section's <c>Pages</c>-scope read lists it: <c>name</c>, hierarchy <c>lastModifiedTime</c>, <c>pageLevel</c> (1 when absent), and how many subpages follow it.</summary>
public sealed record SectionPage(string Name, string? LastModifiedTime, int PageLevel, int Subpages);

/// <summary>
/// Hierarchy reads for story 5.5's soft delete (AD-7 <c>SoftDelete</c>, AD-8): the target's root, a page's entry in its
/// section, the page IDs under a node, and a section group's snapshot XML without protected sections' pages.
/// </summary>
public static class DeleteTargets
{
    /// <summary>The root of <paramref name="hierarchyXml"/>. Throws <see cref="OneNoteXmlException"/> when it isn't well-formed or has no root.</summary>
    public static DeleteTarget Root(string hierarchyXml)
    {
        var root = OneNoteXml.Parse(hierarchyXml).Root ?? throw new OneNoteXmlException("The XML has no root element.");
        var kind = KindOf(root);
        return new DeleteTarget(
            kind,
            (string?)root.Attribute("ID") ?? string.Empty,
            (string?)root.Attribute("name") ?? string.Empty,
            (string?)root.Attribute("path") ?? string.Empty,
            (string?)root.Attribute("lastModifiedTime"),
            RecycleBin.IsBin(root),
            OneNoteXml.IsTrue((string?)root.Attribute("isInRecycleBin")) || OneNoteXml.IsTrue((string?)root.Attribute("isDeletedPages")),
            kind == HierarchyKind.Section && EncryptedSections.IsEncrypted(root),
            OneNoteXml.IsTrue((string?)root.Attribute("locked")));
    }

    /// <summary>
    /// The page <paramref name="pageId"/> in <paramref name="sectionPagesXml"/> (its section read at <c>Pages</c> scope),
    /// or null when it isn't listed. Its subpages are the pages right after it whose <c>pageLevel</c> is greater than its
    /// own (Q2; OneNote keeps a page's subpages directly after it). Throws <see cref="OneNoteXmlException"/> when the XML
    /// isn't well-formed.
    /// </summary>
    public static SectionPage? Page(string sectionPagesXml, string pageId)
    {
        var pages = OneNoteXml.Parse(sectionPagesXml).Descendants(OneNoteXml.One + "Page").ToList();
        var index = pages.FindIndex(p => string.Equals((string?)p.Attribute("ID"), pageId, StringComparison.Ordinal));
        if (index < 0)
        {
            return null;
        }

        var page = pages[index];
        var level = Level(page);
        var subpages = pages.Skip(index + 1).TakeWhile(p => Level(p) > level).Count();
        return new SectionPage((string?)page.Attribute("name") ?? string.Empty, (string?)page.Attribute("lastModifiedTime"), level, subpages);
    }

    /// <summary>The <c>ID</c> of every <c>one:Page</c> in <paramref name="hierarchyXml"/>, in document order. Throws <see cref="OneNoteXmlException"/> when the XML isn't well-formed.</summary>
    public static IReadOnlyList<string> PageIds(string hierarchyXml) =>
        [.. OneNoteXml.Parse(hierarchyXml).Descendants(OneNoteXml.One + "Page").Select(p => (string?)p.Attribute("ID") ?? string.Empty).Where(id => id.Length > 0)];

    /// <summary>
    /// <paramref name="hierarchyXml"/> with the <c>one:Page</c> elements of every password-protected section removed and
    /// everything else kept, the protected <c>one:Section</c> elements included (AD-8: a larger node's hierarchy snapshot,
    /// as a read without a grant would show it, AD-4). Unchanged text when there is nothing to remove. Throws
    /// <see cref="OneNoteXmlException"/> when the XML isn't well-formed.
    /// </summary>
    public static string WithoutProtectedPages(string hierarchyXml)
    {
        var document = OneNoteXml.Parse(hierarchyXml);
        var pages = document.Descendants(OneNoteXml.One + "Section")
            .Where(EncryptedSections.IsEncrypted)
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

    private static int Level(XElement page) =>
        int.TryParse((string?)page.Attribute("pageLevel"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var level) ? level : 1;

    private static HierarchyKind KindOf(XElement element) =>
        element.Name.Namespace != OneNoteXml.One
            ? HierarchyKind.Other
            : element.Name.LocalName switch
            {
                "Notebook" => HierarchyKind.Notebook,
                "SectionGroup" => HierarchyKind.SectionGroup,
                "Section" => HierarchyKind.Section,
                "Page" => HierarchyKind.Page,
                _ => HierarchyKind.Other,
            };
}
