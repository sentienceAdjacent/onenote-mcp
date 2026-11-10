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
/// The root element of hierarchy XML from <c>GetHierarchy</c>: its kind and the attributes access checks need (AD-6,
/// AD-21), unchanged. <see cref="DisplayName"/> is a notebook's <c>nickname</c>, else its <c>name</c>.
/// </summary>
public sealed record HierarchyNode(HierarchyKind Kind, string Id, string Name, string DisplayName, string Path, bool Encrypted, bool Locked)
{
    /// <summary>The root of <paramref name="hierarchyXml"/>. Throws <see cref="OneNoteXmlException"/> when it isn't well-formed or has no root.</summary>
    public static HierarchyNode Root(string hierarchyXml)
    {
        var root = OneNoteXml.Parse(hierarchyXml).Root ?? throw new OneNoteXmlException("The XML has no root element.");
        var kind = root.Name.Namespace != OneNoteXml.One
            ? HierarchyKind.Other
            : root.Name.LocalName switch
            {
                "Notebook" => HierarchyKind.Notebook,
                "SectionGroup" => HierarchyKind.SectionGroup,
                "Section" => HierarchyKind.Section,
                "Page" => HierarchyKind.Page,
                _ => HierarchyKind.Other,
            };
        return new HierarchyNode(
            kind,
            (string?)root.Attribute("ID") ?? string.Empty,
            (string?)root.Attribute("name") ?? string.Empty,
            EncryptedSections.DisplayName(root),
            (string?)root.Attribute("path") ?? string.Empty,
            kind == HierarchyKind.Section && EncryptedSections.IsEncrypted(root),
            OneNoteXml.IsTrue((string?)root.Attribute("locked")));
    }
}
