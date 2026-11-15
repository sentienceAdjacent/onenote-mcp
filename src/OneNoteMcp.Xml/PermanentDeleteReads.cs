namespace OneNoteMcp.Xml;

/// <summary>
/// Hierarchy reads for story 5.6's permanent delete (AD-7 <c>PermanentDelete</c>) beyond the target itself, which
/// <see cref="HierarchyTarget"/> reads: how many containers lie beneath a section group, and a section's pages as IDs,
/// dates and names.
/// </summary>
public static class PermanentDeleteReads
{
    /// <summary>
    /// How many sections and section groups lie anywhere beneath the root of <paramref name="hierarchyXml"/> (a section
    /// group read at <c>pages</c> scope): AD-7 permanently deletes only an empty group. Throws
    /// <see cref="OneNoteXmlException"/> when the XML isn't well-formed or has no root.
    /// </summary>
    public static int ContainersBeneath(string hierarchyXml)
    {
        var root = OneNoteXml.ParseRoot(hierarchyXml);
        return root.Descendants().Count(e => HierarchyElements.KindOf(e) is HierarchyKind.Section or HierarchyKind.SectionGroup);
    }

    /// <summary>
    /// The <c>ID</c>s of the <c>one:Page</c> elements directly under the root of <paramref name="sectionPagesXml"/> (a
    /// section read at <c>pages</c> scope), in section order, subpages included. Throws <see cref="OneNoteXmlException"/>
    /// when the XML isn't well-formed or has no root.
    /// </summary>
    public static IReadOnlyList<string> PageIds(string sectionPagesXml)
    {
        var root = OneNoteXml.ParseRoot(sectionPagesXml);
        return [.. root.Elements(OneNoteXml.One + "Page").Select(p => (string?)p.Attribute("ID") ?? string.Empty)];
    }

    /// <summary>
    /// Each <c>one:Page</c> directly under the root of <paramref name="sectionPagesXml"/>, in section order, as its <c>ID</c>
    /// and <c>lastModifiedTime</c> (null when absent): what a section's permanent delete compares before and after its backup
    /// (story 5.6). Throws <see cref="OneNoteXmlException"/> when the XML isn't well-formed or has no root.
    /// </summary>
    public static IReadOnlyList<(string Id, string? LastModifiedTime)> PageDates(string sectionPagesXml)
    {
        var root = OneNoteXml.ParseRoot(sectionPagesXml);
        return [.. root.Elements(OneNoteXml.One + "Page").Select(p => ((string?)p.Attribute("ID") ?? string.Empty, (string?)p.Attribute("lastModifiedTime")))];
    }

    /// <summary>
    /// Each <c>one:Page</c> directly under the root of <paramref name="sectionPagesXml"/>, in section order, as its <c>ID</c>
    /// and <c>name</c> (empty when absent): what a page's permanent-delete confirmation compares its own name with (story
    /// 5.6, fresh review). Throws <see cref="OneNoteXmlException"/> when the XML isn't well-formed or has no root.
    /// </summary>
    public static IReadOnlyList<(string Id, string Name)> PageNames(string sectionPagesXml)
    {
        var root = OneNoteXml.ParseRoot(sectionPagesXml);
        return [.. root.Elements(OneNoteXml.One + "Page").Select(p => ((string?)p.Attribute("ID") ?? string.Empty, (string?)p.Attribute("name") ?? string.Empty))];
    }
}
