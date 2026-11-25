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
/// <see cref="EncryptedMarker"/> is a section's <c>encrypted</c> attribute only: whether the section counts as protected
/// is decided in Core, which also applies AD-21's path rule (entry 15), so nothing here should be read as "protected".
/// <see cref="AllPagesAvailable"/> is false only when the element carries <c>areAllPagesAvailable</c> false (a section with a
/// page stub added from another computer that never synced; story 3.7, the backlog 10 sitting).
/// </summary>
public sealed record HierarchyNode(HierarchyKind Kind, string Id, string Name, string DisplayName, string Path, bool EncryptedMarker, bool Locked, bool InRecycleBin = false, bool AllPagesAvailable = true)
{
    /// <summary>The root of <paramref name="hierarchyXml"/>. Throws <see cref="OneNoteXmlException"/> when it isn't well-formed or has no root.</summary>
    public static HierarchyNode Root(string hierarchyXml) =>
        From(OneNoteXml.ParseRoot(hierarchyXml));

    /// <summary>
    /// Every <c>one:Notebook</c> in <paramref name="hierarchyXml"/>, the root included, in document order (the grant
    /// CLI's notebook list, AD-6). Throws <see cref="OneNoteXmlException"/> when the XML isn't well-formed.
    /// </summary>
    public static IReadOnlyList<HierarchyNode> Notebooks(string hierarchyXml) =>
        [.. OneNoteXml.Parse(hierarchyXml).Descendants(OneNoteXml.One + "Notebook").Select(From)];

    /// <summary>
    /// The <c>one:Notebook</c> element whose <c>ID</c> is <paramref name="notebookId"/> (compared exactly) in
    /// <paramref name="hierarchyXml"/>, as XML: its attributes exactly as OneNote wrote them, without children, with the 2013
    /// namespace declared on it (story 4.4: <c>close_notebook</c>'s result). Null when there is none. Throws
    /// <see cref="OneNoteXmlException"/> when the XML isn't well-formed.
    /// </summary>
    public static string? NotebookElement(string hierarchyXml, string notebookId)
    {
        var element = OneNoteXml.Parse(hierarchyXml).Descendants(OneNoteXml.One + "Notebook")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("ID"), notebookId, StringComparison.Ordinal));
        if (element is null)
        {
            return null;
        }

        var copy = new XElement(
            element.Name,
            new XAttribute(XNamespace.Xmlns + "one", OneNoteXml.Namespace2013),
            element.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => new XAttribute(a)));
        return copy.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Every <c>one:Section</c> in <paramref name="hierarchyXml"/>, at any depth (inside section groups too), in document
    /// order. Throws <see cref="OneNoteXmlException"/> when the XML isn't well-formed.
    /// </summary>
    public static IReadOnlyList<HierarchyNode> Sections(string hierarchyXml) =>
        [.. OneNoteXml.Parse(hierarchyXml).Descendants(OneNoteXml.One + "Section").Select(From)];

    /// <summary>
    /// Every <c>one:Notebook</c> in <paramref name="hierarchyXml"/> (a <c>GetHierarchy</c> of the open notebooks at
    /// <c>Sections</c> scope or deeper), each with its <c>one:Section</c> and <c>one:SectionGroup</c> elements at any depth, in
    /// document order (story 4.7: <c>navigate_to_url</c> matches a link against them). Throws
    /// <see cref="OneNoteXmlException"/> when the XML isn't well-formed.
    /// </summary>
    public static IReadOnlyList<NotebookTree> NotebookTrees(string hierarchyXml) =>
        [.. OneNoteXml.Parse(hierarchyXml).Descendants(OneNoteXml.One + "Notebook").Select(n => new NotebookTree(
            From(n),
            [.. n.Descendants(OneNoteXml.One + "Section").Select(From)],
            [.. n.Descendants(OneNoteXml.One + "SectionGroup").Select(From)]))];

    private static HierarchyNode From(XElement element)
    {
        var kind = HierarchyElements.KindOf(element);
        return new HierarchyNode(
            kind,
            (string?)element.Attribute("ID") ?? string.Empty,
            (string?)element.Attribute("name") ?? string.Empty,
            EncryptedSections.DisplayName(element),
            (string?)element.Attribute("path") ?? string.Empty,
            kind == HierarchyKind.Section && EncryptedSections.IsEncrypted(element),
            OneNoteXml.IsTrue((string?)element.Attribute("locked")),
            HierarchyElements.InRecycleBin(element),
            (string?)element.Attribute("areAllPagesAvailable") is not ("false" or "0"));
    }
}

/// <summary>One open notebook with its sections and section groups at any depth (<see cref="HierarchyNode.NotebookTrees"/>).</summary>
public sealed record NotebookTree(HierarchyNode Notebook, IReadOnlyList<HierarchyNode> Sections, IReadOnlyList<HierarchyNode> SectionGroups);
