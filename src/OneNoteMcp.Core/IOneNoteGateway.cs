namespace OneNoteMcp.Core;

/// <summary>
/// The one driven port (hexagonal paradigm). Implementations throw only <see cref="OneNoteException"/>.
/// Methods return tasks that run on the gateway's STA queue. These are the AD-7 <c>Read</c>-class COM methods, plus
/// <see cref="NavigateToAsync"/>, an <c>AppState</c> call (AD-11), and <see cref="CreateNewPageAsync"/>, a <c>Create</c>
/// call only the write pipeline makes (AD-7); every XML result is 2013-schema, returned unchanged (AD-4). IDs are
/// opaque strings, passed through unchanged.
/// </summary>
public interface IOneNoteGateway
{
    /// <summary>Returns hierarchy XML. An empty <paramref name="startNodeId"/> means all notebooks.</summary>
    Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct);

    /// <summary>Returns the parent's ID. At a notebook OneNote returns an empty string (observed 2026-11-04, story 6), which marks the end of the chain (AD-6).</summary>
    Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct);

    /// <summary>Returns page XML with the detail <paramref name="pageInfo"/> asks for.</summary>
    Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct);

    /// <summary>Returns one binary object of a page, base64-encoded, by its <c>callbackID</c>.</summary>
    Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct);

    /// <summary>Returns hierarchy XML of the pages matching <paramref name="query"/>. Never displays results in OneNote (AD-11).</summary>
    Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct);

    /// <summary>Returns hierarchy XML of the objects carrying metadata named <paramref name="name"/>.</summary>
    Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct);

    /// <summary>Returns a <c>onenote:</c> link. An empty <paramref name="pageContentObjectId"/> links to the hierarchy object itself.</summary>
    Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct);

    /// <summary>Returns a web link for a cloud notebook's object. An empty <paramref name="pageContentObjectId"/> links to the hierarchy object itself.</summary>
    Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct);

    /// <summary>Returns the folder path or section ID OneNote keeps for <paramref name="location"/>.</summary>
    Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct);

    /// <summary>
    /// User-visible (AD-11): shows <paramref name="hierarchyId"/> in OneNote's first open window and takes keyboard focus.
    /// A non-empty <paramref name="objectId"/> also selects that object on the page. Only <c>NavigateToUseCase</c> calls
    /// it, on the user's explicit request; no read use case does.
    /// </summary>
    Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct);

    /// <summary>
    /// A write (<c>Create</c>, AD-7): adds a page of <paramref name="style"/> to the section <paramref name="sectionId"/>
    /// and returns its ID. Never retried after a disconnect (AD-3). Only the write pipeline calls it, after the access
    /// check and under the write lock (AD-18).
    /// </summary>
    Task<string> CreateNewPageAsync(string sectionId, NewPageStyle style, CancellationToken ct);
}
