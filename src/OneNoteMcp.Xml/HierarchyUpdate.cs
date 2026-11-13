using System.Globalization;
using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>
/// One live child of a node a reorder reads (story 5.4): a section or section group under a notebook or section group, or a
/// page under a section. <see cref="PageLevel"/> is a page's <c>pageLevel</c> (1 when it has none or it isn't a whole
/// number), and 0 for anything that isn't a page.
/// </summary>
public sealed record HierarchyMember(HierarchyKind Kind, string Id, string Name, int PageLevel);

/// <summary>
/// The node a hierarchy write targets, as a <c>GetHierarchy</c> read shows it (story 5.4; AD-7 <c>HierarchyWrite</c>): its
/// kind, <c>ID</c>, <c>name</c>, a notebook's <c>nickname</c> (null when it has none), <c>path</c>,
/// <c>lastModifiedTime</c> (unchanged, null when absent), whether it is a notebook's recycle bin (<c>isRecycleBin</c>, or a
/// section group named <c>OneNote_RecycleBin</c>) or inside one (<c>isInRecycleBin</c>, <c>isDeletedPages</c>), a section's
/// <c>encrypted</c> and <c>locked</c>, and its live children in document order: sections and section groups under a
/// notebook or section group, pages under a section. A child that is the recycle bin or in it is never listed. Read at
/// <c>self</c> scope it has no children.
/// </summary>
public sealed record HierarchyTarget(
    HierarchyKind Kind,
    string Id,
    string Name,
    string? Nickname,
    string Path,
    string? LastModifiedTime,
    bool IsRecycleBin,
    bool InRecycleBin,
    bool Encrypted,
    bool Locked,
    IReadOnlyList<HierarchyMember> Children)
{
    /// <summary>The name OneNote shows: a notebook's <c>nickname</c> when it has one, else <c>name</c>.</summary>
    public string DisplayName => Kind == HierarchyKind.Notebook && !string.IsNullOrEmpty(Nickname) ? Nickname : Name;

    /// <summary>
    /// The root of <paramref name="hierarchyXml"/> and its live children. Throws <see cref="OneNoteXmlException"/> when the
    /// XML isn't well-formed or has no root.
    /// </summary>
    public static HierarchyTarget Read(string hierarchyXml)
    {
        var root = OneNoteXml.Parse(hierarchyXml).Root ?? throw new OneNoteXmlException("The XML has no root element.");
        var kind = HierarchyChildren.KindOf(root);
        var children = new List<HierarchyMember>();
        foreach (var child in root.Elements())
        {
            var childKind = HierarchyChildren.KindOf(child);
            var listed = kind switch
            {
                HierarchyKind.Notebook or HierarchyKind.SectionGroup => childKind is HierarchyKind.Section or HierarchyKind.SectionGroup,
                HierarchyKind.Section => childKind == HierarchyKind.Page,
                _ => false,
            };
            if (!listed || HierarchyChildren.IsRecycleBin(child) || HierarchyChildren.InRecycleBin(child))
            {
                continue;
            }

            children.Add(new HierarchyMember(
                childKind,
                (string?)child.Attribute("ID") ?? string.Empty,
                (string?)child.Attribute("name") ?? string.Empty,
                childKind == HierarchyKind.Page ? PageLevel(child) : 0));
        }

        return new HierarchyTarget(
            kind,
            (string?)root.Attribute("ID") ?? string.Empty,
            (string?)root.Attribute("name") ?? string.Empty,
            kind == HierarchyKind.Notebook ? (string?)root.Attribute("nickname") : null,
            (string?)root.Attribute("path") ?? string.Empty,
            (string?)root.Attribute("lastModifiedTime"),
            HierarchyChildren.IsRecycleBin(root),
            HierarchyChildren.InRecycleBin(root),
            kind == HierarchyKind.Section && EncryptedSections.IsEncrypted(root),
            OneNoteXml.IsTrue((string?)root.Attribute("locked")),
            children);
    }

    private static int PageLevel(XElement page) =>
        int.TryParse((string?)page.Attribute("pageLevel"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) && level >= 1 ? level : 1;
}

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
            element.Add(new XElement(OneNoteXml.One + ElementName(child.Kind, nameof(children)), new XAttribute("ID", child.Id)));
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
