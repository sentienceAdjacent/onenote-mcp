using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Access;

/// <summary>
/// One open notebook in hierarchy XML, for the grant CLI (AD-6): its ID, <c>name</c>, displayed name (<c>nickname</c>,
/// else <c>name</c>), live <c>path</c> and <see cref="GrantKeys.NotebookKey"/> of that path (empty when OneNote reports no
/// path). Names are shown to the user only, never put in a tool result (AD-9).
/// </summary>
public sealed record OpenNotebook(string Id, string Name, string DisplayName, string Path, string Key);

/// <summary>Lists the open notebooks in hierarchy XML for callers outside Core, through <see cref="HierarchyNode.Notebooks"/>.</summary>
public static class OpenNotebooks
{
    /// <summary>
    /// Every <c>one:Notebook</c> in <paramref name="hierarchyXml"/> (<c>GetHierarchy</c> output), in document order. Throws
    /// <see cref="ErrorCode.InvalidXml"/> when the XML can't be read.
    /// </summary>
    public static IReadOnlyList<OpenNotebook> Find(string hierarchyXml) =>
        [.. XmlReads.Readable(() => HierarchyNode.Notebooks(hierarchyXml), "GetHierarchy")
            .Select(n => new OpenNotebook(n.Id, n.Name, n.DisplayName, n.Path, n.Path.Length == 0 ? string.Empty : GrantKeys.NotebookKey(n.Path)))];
}
