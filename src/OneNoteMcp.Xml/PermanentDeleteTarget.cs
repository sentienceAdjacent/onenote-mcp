using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>
/// The target of a hierarchy delete as a <c>GetHierarchy</c> read at <c>self</c> shows it (story 5.6; AD-7
/// <c>PermanentDelete</c>): its kind, <c>ID</c>, <c>name</c> and <c>lastModifiedTime</c> (the exact string OneNote wrote, null
/// when absent, AD-17), and the attributes the refusals read: <see cref="IsRecycleBin"/> (<c>isRecycleBin</c>, or a section
/// group named <c>OneNote_RecycleBin</c>), <see cref="IsInRecycleBin"/> (<c>isInRecycleBin</c>),
/// <see cref="IsDeletedPages"/> (<c>isDeletedPages</c>), <see cref="Encrypted"/> (a section's <c>encrypted</c>, AD-21) and
/// <see cref="Locked"/>. Story 5.5's <see cref="DeleteTargets"/> reads much the same but folds <c>isDeletedPages</c> into
/// <c>InRecycleBin</c>, while a permanent delete allows a recycled section and refuses Deleted Pages, so both readers stay
/// until the share-point refactor (5.6 plan change log).
/// </summary>
public sealed record PermanentDeleteTarget(
    HierarchyKind Kind,
    string Id,
    string Name,
    string? LastModifiedTime,
    bool IsRecycleBin,
    bool IsInRecycleBin,
    bool IsDeletedPages,
    bool Encrypted,
    bool Locked)
{
    /// <summary>
    /// The root of <paramref name="selfXml"/>. Throws <see cref="OneNoteXmlException"/> when the XML isn't well-formed or has
    /// no root.
    /// </summary>
    public static PermanentDeleteTarget Read(string selfXml)
    {
        var root = OneNoteXml.Parse(selfXml).Root ?? throw new OneNoteXmlException("The XML has no root element.");
        var kind = KindOf(root);
        return new PermanentDeleteTarget(
            kind,
            (string?)root.Attribute("ID") ?? string.Empty,
            (string?)root.Attribute("name") ?? string.Empty,
            (string?)root.Attribute("lastModifiedTime"),
            OneNoteXml.IsTrue((string?)root.Attribute("isRecycleBin"))
                || (kind == HierarchyKind.SectionGroup && string.Equals((string?)root.Attribute("name"), NotebookSections.RecycleBinName, StringComparison.Ordinal)),
            OneNoteXml.IsTrue((string?)root.Attribute("isInRecycleBin")),
            OneNoteXml.IsTrue((string?)root.Attribute("isDeletedPages")),
            kind == HierarchyKind.Section && EncryptedSections.IsEncrypted(root),
            OneNoteXml.IsTrue((string?)root.Attribute("locked")));
    }

    /// <summary>
    /// How many sections and section groups lie anywhere beneath the root of <paramref name="hierarchyXml"/> (a section
    /// group read at <c>pages</c> scope): AD-7 permanently deletes only an empty group. Throws
    /// <see cref="OneNoteXmlException"/> when the XML isn't well-formed or has no root.
    /// </summary>
    public static int ContainersBeneath(string hierarchyXml)
    {
        var root = OneNoteXml.Parse(hierarchyXml).Root ?? throw new OneNoteXmlException("The XML has no root element.");
        return root.Descendants().Count(e => KindOf(e) is HierarchyKind.Section or HierarchyKind.SectionGroup);
    }

    /// <summary>
    /// The <c>ID</c>s of the <c>one:Page</c> elements directly under the root of <paramref name="sectionPagesXml"/> (a
    /// section read at <c>pages</c> scope), in section order, subpages included. Throws <see cref="OneNoteXmlException"/>
    /// when the XML isn't well-formed or has no root.
    /// </summary>
    public static IReadOnlyList<string> PageIds(string sectionPagesXml)
    {
        var root = OneNoteXml.Parse(sectionPagesXml).Root ?? throw new OneNoteXmlException("The XML has no root element.");
        return [.. root.Elements(OneNoteXml.One + "Page").Select(p => (string?)p.Attribute("ID") ?? string.Empty)];
    }

    /// <summary>
    /// Each <c>one:Page</c> directly under the root of <paramref name="sectionPagesXml"/>, in section order, as its <c>ID</c>
    /// and <c>lastModifiedTime</c> (null when absent): what a section's permanent delete compares before and after its backup
    /// (story 5.6). Throws <see cref="OneNoteXmlException"/> when the XML isn't well-formed or has no root.
    /// </summary>
    public static IReadOnlyList<(string Id, string? LastModifiedTime)> PageDates(string sectionPagesXml)
    {
        var root = OneNoteXml.Parse(sectionPagesXml).Root ?? throw new OneNoteXmlException("The XML has no root element.");
        return [.. root.Elements(OneNoteXml.One + "Page").Select(p => ((string?)p.Attribute("ID") ?? string.Empty, (string?)p.Attribute("lastModifiedTime")))];
    }

    /// <summary>
    /// Each <c>one:Page</c> directly under the root of <paramref name="sectionPagesXml"/>, in section order, as its <c>ID</c>
    /// and <c>name</c> (empty when absent): what a page's permanent-delete confirmation compares its own name with (story
    /// 5.6, fresh review). Throws <see cref="OneNoteXmlException"/> when the XML isn't well-formed or has no root.
    /// </summary>
    public static IReadOnlyList<(string Id, string Name)> PageNames(string sectionPagesXml)
    {
        var root = OneNoteXml.Parse(sectionPagesXml).Root ?? throw new OneNoteXmlException("The XML has no root element.");
        return [.. root.Elements(OneNoteXml.One + "Page").Select(p => ((string?)p.Attribute("ID") ?? string.Empty, (string?)p.Attribute("name") ?? string.Empty))];
    }

    /// <summary>
    /// The number of subpages of <paramref name="pageId"/> in <paramref name="sectionPagesXml"/>: the pages straight after
    /// it whose <c>pageLevel</c> is greater than its own, up to the next page at its level or above. Null when the page
    /// isn't in the section. A missing or unreadable <c>pageLevel</c> counts as 1. Throws
    /// <see cref="OneNoteXmlException"/> when the XML isn't well-formed or has no root.
    /// </summary>
    public static int? Subpages(string sectionPagesXml, string pageId)
    {
        var root = OneNoteXml.Parse(sectionPagesXml).Root ?? throw new OneNoteXmlException("The XML has no root element.");
        var pages = root.Elements(OneNoteXml.One + "Page").ToList();
        var index = pages.FindIndex(p => string.Equals((string?)p.Attribute("ID"), pageId, StringComparison.Ordinal));
        if (index < 0)
        {
            return null;
        }

        var level = Level(pages[index]);
        var count = 0;
        for (var i = index + 1; i < pages.Count && Level(pages[i]) > level; i++)
        {
            count++;
        }

        return count;
    }

    private static int Level(XElement page) =>
        int.TryParse((string?)page.Attribute("pageLevel"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var level) ? level : 1;

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
