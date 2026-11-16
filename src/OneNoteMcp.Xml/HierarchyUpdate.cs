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
