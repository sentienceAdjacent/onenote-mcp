using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>
/// The minimal <c>UpdateHierarchy</c> XML a hierarchy write sends (story 5.4; spike 5.1's shapes), built from IDs and a name
/// only, so no attribute a caller chose ever reaches OneNote (no <c>color</c>, <c>pageLevel</c>, <c>path</c>). The caller
/// checks the result with <see cref="SchemaValidation.Problems"/> before COM (AD-16).
/// </summary>
public static class HierarchyUpdate
{
    /// <summary>
    /// A rename: the node's own element with its <c>ID</c> and the new displayed name, <c>nickname</c> for a notebook
    /// (OneNote ignores a changed <c>name</c> there, spike 5.1) and <c>name</c> for a section or section group. A section's
    /// <paramref name="name"/> is its name as OneNote shows it, without <c>.one</c>.
    /// </summary>
    public static string Rename(HierarchyKind kind, string id, string name)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(name);
        var element = new XElement(OneNoteXml.One + ElementName(kind, nameof(kind)), new XAttribute("ID", id));
        element.Add(new XAttribute(kind == HierarchyKind.Notebook ? "nickname" : "name", name));
        return Serialize(element);
    }

    /// <summary>
    /// A reorder: the parent's element with its <c>ID</c>, holding one ID-only child element per entry of
    /// <paramref name="children"/>, in that order. OneNote reorders the listed children to the order sent and keeps any it
    /// isn't sent (the recycle bin) after them (spike 5.1 constraint 4).
    /// </summary>
    public static string Reorder(HierarchyKind parentKind, string parentId, IReadOnlyList<HierarchyMember> children)
    {
        ArgumentNullException.ThrowIfNull(parentId);
        ArgumentNullException.ThrowIfNull(children);
        var element = new XElement(OneNoteXml.One + ElementName(parentKind, nameof(parentKind)), new XAttribute("ID", parentId));
        foreach (var child in children)
        {
            var member = new XElement(OneNoteXml.One + ElementName(child.Kind, nameof(children)), new XAttribute("ID", child.Id));
            if (child.Kind == HierarchyKind.Page && child.PageLevel > 0)
            {
                // Each page's level as read, unchanged: without it OneNote flattens every subpage to level 1 (sitting 2026-11-15, L6).
                member.Add(new XAttribute("pageLevel", child.PageLevel));
            }

            element.Add(member);
        }

        return Serialize(element);
    }

    /// <summary>
    /// A page move (story 3.4; spike 3.1 M1 shape a): the target section's element with its <c>ID</c>, holding every live
    /// page of <paramref name="pages"/> in order, each ID-only with its <c>pageLevel</c> as read, and the moved page
    /// <paramref name="pageId"/> inserted at the 0-based <paramref name="index"/> with <c>pageLevel="1"</c>. One call moves
    /// it: OneNote gives it a new ID and leaves its subpages in the source. Without the levels OneNote would flatten the
    /// target's subpages (5.4 sitting L6).
    /// </summary>
    public static string MovePage(string sectionId, IReadOnlyList<HierarchyMember> pages, string pageId, int index)
    {
        ArgumentNullException.ThrowIfNull(sectionId);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(pageId);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, pages.Count);
        var element = new XElement(OneNoteXml.One + "Section", new XAttribute("ID", sectionId));
        var moved = new XElement(OneNoteXml.One + "Page", new XAttribute("ID", pageId), new XAttribute("pageLevel", 1));
        for (var i = 0; i < pages.Count; i++)
        {
            if (i == index)
            {
                element.Add(moved);
            }

            var page = pages[i];
            element.Add(new XElement(
                OneNoteXml.One + "Page",
                new XAttribute("ID", page.Id),
                new XAttribute("pageLevel", page.PageLevel > 0 ? page.PageLevel : 1)));
        }

        if (index == pages.Count)
        {
            element.Add(moved);
        }

        return Serialize(element);
    }

    private static string ElementName(HierarchyKind kind, string argument) => kind switch
    {
        HierarchyKind.Notebook => "Notebook",
        HierarchyKind.SectionGroup => "SectionGroup",
        HierarchyKind.Section => "Section",
        HierarchyKind.Page => "Page",
        _ => throw new ArgumentOutOfRangeException(argument, kind, "Only a notebook, section group, section or page has an element."),
    };

    private static string Serialize(XElement element)
    {
        // The one: prefix, as OneNote writes it and spike 5.1 sent it.
        element.SetAttributeValue(XNamespace.Xmlns + "one", OneNoteXml.Namespace2013);
        return "<?xml version=\"1.0\"?>" + element.ToString(SaveOptions.DisableFormatting);
    }
}
