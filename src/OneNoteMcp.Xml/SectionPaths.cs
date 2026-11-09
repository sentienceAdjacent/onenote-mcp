namespace OneNoteMcp.Xml;

/// <summary>The <c>path</c> of every section in hierarchy XML, for <c>list_grants</c>' open-section check (AD-21).</summary>
public static class SectionPaths
{
    /// <summary>
    /// The non-empty <c>path</c> of every <c>one:Section</c> in <paramref name="hierarchyXml"/>, the root included, in
    /// document order, unchanged (AD-4). Throws <see cref="OneNoteXmlException"/> when the XML isn't well-formed.
    /// </summary>
    public static IReadOnlyList<string> Find(string hierarchyXml) =>
        [.. OneNoteXml.Parse(hierarchyXml)
            .Descendants(OneNoteXml.One + "Section")
            .Select(s => (string?)s.Attribute("path"))
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)];
}
