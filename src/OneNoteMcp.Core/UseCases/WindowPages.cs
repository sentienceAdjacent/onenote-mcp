using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// AD-21 for a window that names a page (stories 4.6 and 4.7): <c>get_windows</c> and <c>navigate_to_url</c>'s landing check
/// list a window as <see cref="WindowListing"/>, leaving out the page's ID when its section is protected and no read grant
/// is held at the call's start (a stored grant or a live <c>once</c> window; <see cref="ReadAccess.HoldsReadGrant"/>, never
/// prompted). It reads each such section once per call with <c>GetHierarchy</c> at <c>self</c>; a page whose section can't
/// be read or told apart is left out the same way (fail closed). One instance serves one call.
/// </summary>
internal sealed class WindowPages(IOneNoteGateway gateway, ReadAccess access, DateTimeOffset callStart)
{
    private readonly Dictionary<string, bool> readable = new(StringComparer.Ordinal);

    /// <summary>An ID OneNote returned empty is null, so omitted on the wire, never an empty ID (W5).</summary>
    public static string? Present(string id) => id.Length == 0 ? null : id;

    /// <summary>
    /// <paramref name="window"/> as a listing, and whether its page's ID was left out under AD-21. A section read that fails
    /// with anything but <see cref="ErrorCode.ObjectNotFound"/> throws.
    /// </summary>
    public async Task<(WindowListing Listing, bool Withheld)> ListAsync(OneNoteWindow window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(window);
        var pageId = Present(window.PageId);
        var withheld = false;
        if (pageId is not null && !await PageReadableAsync(window.SectionId, ct).ConfigureAwait(false))
        {
            pageId = null;
            withheld = true;
        }

        return (Listing(window, pageId), withheld);
    }

    /// <summary><paramref name="window"/> as a listing with <paramref name="pageId"/> as its page.</summary>
    public static WindowListing Listing(OneNoteWindow window, string? pageId)
    {
        ArgumentNullException.ThrowIfNull(window);
        return new WindowListing(
            OneNoteWindow.HandleOf(window.Handle),
            window.Current,
            window.Active,
            window.FullPageView,
            GetWindowsUseCase.DockedLocationName(window.DockedLocation),
            window.SideNote,
            Present(window.NotebookId),
            Present(window.SectionGroupId),
            Present(window.SectionId),
            pageId);
    }

    /// <summary>
    /// AD-21: whether a page of <paramref name="sectionId"/> may be named, from one <c>GetHierarchy</c> of the section at
    /// <c>self</c> per call. Fails closed: no section ID, a section OneNote can't find, or a node that isn't a section.
    /// </summary>
    private async Task<bool> PageReadableAsync(string sectionId, CancellationToken ct)
    {
        if (sectionId.Length == 0)
        {
            return false;
        }

        if (readable.TryGetValue(sectionId, out var known))
        {
            return known;
        }

        bool allowed;
        try
        {
            var xml = await gateway.GetHierarchyAsync(sectionId, HierarchyScope.Self, ct).ConfigureAwait(false);
            var node = XmlReads.Readable(() => HierarchyNode.Root(xml), "GetHierarchy");
            allowed = node.Kind == HierarchyKind.Section && access.HoldsReadGrant(node, callStart);
        }
        catch (OneNoteException ex) when (ex.Code == ErrorCode.ObjectNotFound)
        {
            allowed = false;
        }

        readable[sectionId] = allowed;
        return allowed;
    }
}
