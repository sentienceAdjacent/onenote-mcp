namespace OneNoteMcp.Xml;

/// <summary>One live section or section group directly under a parent (<see cref="HierarchyChildren.Read"/>): its kind, <c>ID</c> and <c>name</c>, unchanged.</summary>
public sealed record HierarchyChild(HierarchyKind Kind, string Id, string Name);

/// <summary>
/// A would-be parent of a new section or section group, as a <c>GetHierarchy</c> read at <c>Children</c> scope shows it
/// (story 5.3; AD-7 <c>Create</c> validation for <c>OpenHierarchy</c>): its kind and ID; whether it is a notebook's recycle
/// bin (<c>isRecycleBin</c>, or a section group named <c>OneNote_RecycleBin</c>) or inside one (<c>isInRecycleBin</c>,
/// <c>isDeletedPages</c>); and its live sections and section groups, in document order. A child that is the recycle bin
/// or in it isn't listed.
/// </summary>
public sealed record HierarchyParent(HierarchyKind Kind, string Id, bool IsRecycleBin, bool InRecycleBin, IReadOnlyList<HierarchyChild> Children);

/// <summary>Reads a parent and its live children from hierarchy XML (story 5.3).</summary>
public static class HierarchyChildren
{
    /// <summary>
    /// The root of <paramref name="childrenScopeXml"/> and its direct <c>one:Section</c> and <c>one:SectionGroup</c>
    /// children outside the recycle bin. Throws <see cref="OneNoteXmlException"/> when the XML isn't well-formed or has no root.
    /// </summary>
    public static HierarchyParent Read(string childrenScopeXml)
    {
        var root = OneNoteXml.ParseRoot(childrenScopeXml);
        var children = new List<HierarchyChild>();
        foreach (var child in root.Elements())
        {
            var kind = HierarchyElements.KindOf(child);
            if (kind is not (HierarchyKind.Section or HierarchyKind.SectionGroup) || HierarchyElements.IsRecycleBin(child) || HierarchyElements.InRecycleBin(child))
            {
                continue;
            }

            children.Add(new HierarchyChild(kind, (string?)child.Attribute("ID") ?? string.Empty, (string?)child.Attribute("name") ?? string.Empty));
        }

        return new HierarchyParent(
            HierarchyElements.KindOf(root),
            (string?)root.Attribute("ID") ?? string.Empty,
            HierarchyElements.IsRecycleBin(root),
            HierarchyElements.InRecycleBin(root),
            children);
    }
}
