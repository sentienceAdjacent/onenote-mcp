using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Access;

/// <summary>
/// One password-protected section in hierarchy XML (AD-21), for the grant CLI: its ID, name, <see cref="GrantKeys.SectionKey"/>
/// (empty when OneNote reports no <c>path</c>) and <c>locked</c>, and where it is. <see cref="NotebookName"/> is the
/// notebook's displayed name and <see cref="NotebookPath"/> its <c>path</c>; the name is null and the path empty when the
/// XML doesn't show the notebook. <see cref="Groups"/> are the section groups around it, outermost first. Names are shown
/// to the user only, never put in a tool result (AD-9).
/// </summary>
public sealed record ProtectedSection(string Id, string Name, string Key, bool Locked, string? NotebookName, string NotebookPath, IReadOnlyList<string> Groups)
{
    /// <summary>The notebook, when the XML shows it, then the section groups, outermost first.</summary>
    public IReadOnlyList<string> Location => NotebookName is null ? Groups : [NotebookName, .. Groups];
}

/// <summary>Finds the password-protected sections in hierarchy XML for callers outside Core, through <see cref="EncryptedSections"/>.</summary>
public static class ProtectedSections
{
    /// <summary>
    /// Every protected section in <paramref name="hierarchyXml"/> (<c>GetHierarchy</c> output), in document order. Throws
    /// <see cref="ErrorCode.InvalidXml"/> when the XML can't be read.
    /// </summary>
    public static IReadOnlyList<ProtectedSection> Find(string hierarchyXml) =>
        [.. XmlReads.Readable(() => EncryptedSections.Find(hierarchyXml), "GetHierarchy")
            .Select(s => new ProtectedSection(s.Id, s.Name, GrantKeys.SectionKey(s.Path), s.Locked, s.NotebookName, s.NotebookPath, s.SectionGroups))];
}
