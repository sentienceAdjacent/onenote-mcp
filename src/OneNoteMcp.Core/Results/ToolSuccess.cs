namespace OneNoteMcp.Core.Results;

/// <summary>
/// What a use case returns on success: the <c>ok: true</c> envelope without the <c>ok</c> flag (AD-9).
/// <see cref="Index"/> is an AD-20 index, set instead of a body. <see cref="Source"/> never goes on the wire: it tells
/// <see cref="LargeResults.ResultDelivery"/> how to store the body when it is over the inline threshold (AD-20). Nor does
/// <see cref="SearchCache"/>.
/// </summary>
public sealed record ToolSuccess(string? Xml, ToolMeta Meta, IReadOnlyList<ToolWarning> Warnings)
{
    public ResultIndex? Index { get; init; }

    public ResultSource? Source { get; init; }

    /// <summary>find_pages' use of the page-text cache (AD-12). Never on the wire: <c>ToolRunner</c> logs its counts and sizes.</summary>
    public UseCases.PageTextCacheUse? SearchCache { get; init; }

    /// <summary>The body the text block carries after its header line (AD-9): <c>xml</c>, or <c>meta.binary</c>; null when there is none.</summary>
    public string? Body => Xml ?? Meta.Binary;

    public static ToolSuccess WithXml(string xml, ToolMeta meta) => new(xml, meta, []);
}

/// <summary>What kind of body a read returned, so an oversized one is indexed the right way (AD-20).</summary>
public enum ResultKind
{
    /// <summary>Page XML from <c>GetPageContent</c>: refs are <c>objectID</c>s, labels are the element's text, parts carry a basis.</summary>
    PageXml,

    /// <summary>Hierarchy XML (<c>GetHierarchy</c>, <c>FindPages</c>, <c>FindMeta</c>): refs are <c>ID</c>s, labels are <c>name</c>s.</summary>
    HierarchyXml,

    /// <summary>Not XML, such as <c>get_binary_page_content</c>'s base64: no children, read by offset.</summary>
    Text,
}

/// <param name="Kind">How to index the body.</param>
/// <param name="PiBasicXml">For a page read, the piBasic XML the basis was computed from, kept so part hashes always come from piBasic (AD-17, AD-20), even when the body is the piBinaryData XML.</param>
public sealed record ResultSource(ResultKind Kind, string? PiBasicXml = null);
