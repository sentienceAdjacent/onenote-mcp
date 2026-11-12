namespace OneNoteMcp.Core;

/// <summary>
/// The one driven port (hexagonal paradigm). Implementations throw only <see cref="OneNoteException"/>.
/// Methods return tasks that run on the gateway's STA queue. These are the AD-7 <c>Read</c>-class COM methods, plus
/// <see cref="NavigateToAsync"/>, an <c>AppState</c> call (AD-11), and the writes only the write pipeline makes (AD-7):
/// <see cref="CreateNewPageAsync"/> (<c>Create</c>), <see cref="UpdatePageContentAsync"/> and
/// <see cref="DeletePageContentAsync"/> (<c>PageWrite</c>). Every XML result is 2013-schema, returned unchanged (AD-4).
/// IDs are opaque strings, passed through unchanged.
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

    /// <summary>
    /// A write (<c>PageWrite</c>, AD-7): applies <paramref name="pageXml"/>, a 2013-schema page document, with
    /// <c>force: false</c>. <paramref name="expectedLastModified"/> is the page's <c>lastModifiedTime</c> exactly as
    /// OneNote wrote it; one that is empty, unparsable or the minimum date is refused with
    /// <see cref="ErrorCode.InvalidArgument"/> before COM. Never retried after a disconnect (AD-3). Only the write
    /// pipeline calls it, after the access check and under the write lock (AD-18).
    /// </summary>
    Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct);

    /// <summary>
    /// A write (<c>PageWrite</c>, AD-7): deletes the object <paramref name="objectId"/> from the page
    /// <paramref name="pageId"/> with <c>force: false</c>. The date rules and callers are those of
    /// <see cref="UpdatePageContentAsync"/>.
    /// </summary>
    Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct);

    /// <summary>
    /// Completes once no abandoned write is outstanding: a write that started in OneNote and whose caller stopped
    /// waiting (a timeout, or a cancel while it ran) and that hasn't returned yet (AD-3). Completes at once when there
    /// is none. Only the write pipeline waits on it, to keep the write lock until such a call settles.
    /// </summary>
    Task WaitForAbandonedWritesAsync(CancellationToken ct);
}
