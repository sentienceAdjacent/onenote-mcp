using System.Runtime.Versioning;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Journal;

/// <summary>
/// What a write's step 6 passes to <see cref="PageSnapshot.TakeAsync"/>: the call's correlationId, the tool and its AD-7
/// class, the page, and the piBasic XML step 3 already read (the snapshot never reads piBasic again).
/// </summary>
public sealed record PageSnapshotRequest(string CorrelationId, string Operation, OperationClass OperationClass, string PageId, string BasicXml);

/// <summary>
/// AD-7 step 6 for a page (AD-8): the reusable snapshot that <c>PageWrite</c> and epic 5's page deletes call from
/// <c>SnapshotAsync</c>, under the write lock. It walks up from the page once (its section, read live, with
/// <c>encrypted</c>; the section groups; the notebook), reads the page's piBinaryData XML, lists what that XML lacks
/// against step 3's piBasic read (<see cref="ImageCount.MissingBinaries"/>), and stores one entry
/// (<see cref="JournalStore"/>). In a password-protected section the entry's title and XML are encrypted (AD-21).
/// <para>Errors (story 2.8 decision Q3): the walk's and the read's own COM errors pass through unchanged, with their own
/// codes and retry rules. Only building or storing the entry fails with <see cref="ErrorCode.JournalFailed"/>: page XML
/// the server can't read, a page it can't place in a section and notebook, I/O or DPAPI. Messages carry IDs only.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PageSnapshot
{
    private readonly IOneNoteGateway gateway;
    private readonly JournalStore store;
    private readonly TimeProvider clock;

    public PageSnapshot(IOneNoteGateway gateway, JournalStore store, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(store);
        this.gateway = gateway;
        this.store = store;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Takes and stores the snapshot, and returns what was stored.</summary>
    public async Task<JournalWritten> TakeAsync(PageSnapshotRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pageId = request.PageId ?? string.Empty;

        // Placed first, so a page that can't be placed costs no piBinaryData read under the write lock.
        var location = await new HierarchyWalk(gateway).PageLocationAsync(pageId, ct).ConfigureAwait(false);
        if (location.Section is not { } section || location.Notebook is not { } notebook)
        {
            throw new OneNoteException(
                ErrorCode.JournalFailed,
                $"The journal entry for page {pageId} couldn't be made: its section and notebook weren't found.");
        }

        var binary = await gateway.GetPageContentAsync(pageId, PageInfo.BinaryData, ct).ConfigureAwait(false);

        IReadOnlyList<MissingBinary> missing;
        string title;
        try
        {
            missing = ImageCount.MissingBinaries(request.BasicXml ?? string.Empty, binary);
            var root = HierarchyNode.Root(binary);
            if (root.Kind != HierarchyKind.Page)
            {
                throw new OneNoteXmlException("The XML's root isn't a one:Page.");
            }

            title = root.Name;
        }
        catch (OneNoteXmlException ex)
        {
            // The parse message carries positions and element names only (AD-13).
            throw new OneNoteException(ErrorCode.JournalFailed, $"The journal entry for page {pageId} couldn't be made: {ex.Message}");
        }

        var entry = new JournalEntry
        {
            EntryId = Guid.NewGuid(),
            CorrelationId = request.CorrelationId ?? string.Empty,
            Operation = request.Operation ?? string.Empty,
            OperationClass = request.OperationClass.ToString(),
            SnapshotKind = JournalEntry.PageKind,
            ObjectId = pageId,
            NotebookId = notebook.Id,
            NotebookPath = GrantKeys.NotebookKey(notebook.Path),
            NotebookName = notebook.DisplayName,
            ParentId = section.Id,
            ParentNamePath = string.Join('/', [.. location.Groups, section.Name]),
            SectionKey = section.Encrypted ? GrantKeys.SectionKey(section.Path) : null,
            Title = title,
            TakenAtUtc = clock.GetUtcNow().UtcDateTime,
            BinaryComplete = missing.Count == 0,
            MissingBinary = missing,
            Xml = binary,
        };
        return store.Write(entry);
    }
}
