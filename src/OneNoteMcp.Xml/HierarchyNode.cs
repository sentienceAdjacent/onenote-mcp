using System.Xml.Linq;

namespace OneNoteMcp.Xml;

/// <summary>What kind of hierarchy object an XML element is.</summary>
public enum HierarchyKind
{
    /// <summary>Anything else, such as <c>one:Notebooks</c>, the root of an all-notebooks read.</summary>
    Other,
    Notebook,
    SectionGroup,
    Section,
    Page,
}

/// <summary>
/// One element of hierarchy XML from <c>GetHierarchy</c>: its kind and the attributes access checks need (AD-6,
/// AD-21), unchanged. <see cref="DisplayName"/> is a notebook's <c>nickname</c>, else its <c>name</c>.
/// <see cref="InRecycleBin"/> is true when the element carries <c>isInRecycleBin</c> or <c>isDeletedPages</c>.
/// </summary>
public sealed record HierarchyNode(HierarchyKind Kind, string Id, string Name, string DisplayName, string Path, bool Encrypted, bool Locked, bool InRecycleBin = false)
{
    /// <summary>The root of <paramref name="hierarchyXml"/>. Throws <see cref="OneNoteXmlException"/> when it isn't well-formed or has no root.</summary>
    public static HierarchyNode Root(string hierarchyXml) =>
        From(OneNoteXml.Parse(hierarchyXml).Root ?? throw new OneNoteXmlException("The XML has no root element."));

    /// <summary>
    /// Every <c>one:Notebook</c> in <paramref name="hierarchyXml"/>, the root included, in document order (the grant
    /// CLI's notebook list, AD-6). Throws <see cref="OneNoteXmlException"/> when the XML isn't well-formed.
    /// </summary>
    public static IReadOnlyList<HierarchyNode> Notebooks(string hierarchyXml) =>
        [.. OneNoteXml.Parse(hierarchyXml).Descendants(OneNoteXml.One + "Notebook").Select(From)];

    private static HierarchyNode From(XElement element)
    {
        var kind = element.Name.Namespace != OneNoteXml.One
            ? HierarchyKind.Other
            : element.Name.LocalName switch
            {
                "Notebook" => HierarchyKind.Notebook,
                "SectionGroup" => HierarchyKind.SectionGroup,
                "Section" => HierarchyKind.Section,
                "Page" => HierarchyKind.Page,
                _ => HierarchyKind.Other,
            };
        return new HierarchyNode(
            kind,
            (string?)element.Attribute("ID") ?? string.Empty,
            (string?)element.Attribute("name") ?? string.Empty,
            EncryptedSections.DisplayName(element),
            (string?)element.Attribute("path") ?? string.Empty,
            kind == HierarchyKind.Section && EncryptedSections.IsEncrypted(element),
            OneNoteXml.IsTrue((string?)element.Attribute("locked")),
            OneNoteXml.IsTrue((string?)element.Attribute("isInRecycleBin")) || OneNoteXml.IsTrue((string?)element.Attribute("isDeletedPages")));
    }
}
