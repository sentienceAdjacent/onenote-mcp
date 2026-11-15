using System.Runtime.Versioning;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Journal;

/// <summary>
/// What a hierarchy write's step 6 passes to <see cref="HierarchySnapshot.TakeAsync"/>: the call's correlationId, the tool
/// and its AD-7 class, the node at the snapshot's root, and the <c>GetHierarchy</c> XML step 3 already read for it (the
/// snapshot never reads it again).
/// </summary>
public sealed record HierarchySnapshotRequest(string CorrelationId, string Operation, OperationClass OperationClass, string ObjectId, string HierarchyXml);

/// <summary>
/// AD-7 step 6 for a hierarchy write (AD-8; stories 5.4 and 5.5): one <c>hierarchy</c> entry holding step 3's XML of the
/// node the write changes (the renamed node at <c>self</c>, the reordered parent with its children, or a soft-deleted
/// section or section group at <c>pages</c> scope), under the write lock. It places
/// the node once (its parent, the section groups around it and its notebook, read live; a notebook is its own) and stores
/// the entry (<see cref="JournalStore"/>).
/// <para>A snapshot rooted at a password-protected section (<c>encrypted</c> in step 3's read) is a protected entry, marked
/// with that section's <c>SectionKey</c> from its path before the write, so its title and XML are encrypted with DPAPI
/// (AD-8, AD-21). Any other entry never keeps a protected section's pages: its <c>one:Section</c> stays, its
/// <c>one:Page</c> elements are dropped (a section group's <c>pages</c>-scope read, story 5.5).</para>
/// <para>Errors: the walk's own COM errors pass through unchanged. Only building or storing the entry fails with
/// <see cref="ErrorCode.JournalFailed"/>: XML the server can't read, a node it can't place in a notebook, I/O or DPAPI.
/// Messages carry IDs only.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HierarchySnapshot
{
    private readonly IOneNoteGateway gateway;
    private readonly JournalStore store;
    private readonly TimeProvider clock;

    public HierarchySnapshot(IOneNoteGateway gateway, JournalStore store, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(store);
        this.gateway = gateway;
        this.store = store;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Takes and stores the snapshot, and returns what was stored.</summary>
    public async Task<JournalWritten> TakeAsync(HierarchySnapshotRequest request, CancellationToken ct) =>
        store.Write(await EntryAsync(request, JournalEntry.HierarchyKind, ct).ConfigureAwait(false));

    /// <summary>
    /// Builds the entry of kind <paramref name="snapshotKind"/>, unstored, with a new ID and the current time: story 5.6
    /// stores a section's <c>onenoteBackup</c> entry this way, last, once its backup file is on disk (<c>SectionBackup</c>).
    /// </summary>
    public async Task<JournalEntry> EntryAsync(HierarchySnapshotRequest request, string snapshotKind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(snapshotKind);
        var objectId = request.ObjectId ?? string.Empty;

        HierarchyNode root;
        string xml;
        try
        {
            root = HierarchyNode.Root(request.HierarchyXml ?? string.Empty);

            // A protected section's own entry is encrypted whole; anything else never keeps a protected section's pages
            // (AD-8, AD-21; story 5.5).
            xml = root.Kind == HierarchyKind.Section && root.Encrypted
                ? request.HierarchyXml!
                : DeleteTargets.WithoutProtectedPages(request.HierarchyXml!);
        }
        catch (OneNoteXmlException ex)
        {
            // The parse message carries positions and element names only (AD-13).
            throw new OneNoteException(ErrorCode.JournalFailed, $"The journal entry for {objectId} couldn't be made: {ex.Message}");
        }

        HierarchyNode notebook;
        string parentId;
        IReadOnlyList<string> groups;
        if (root.Kind == HierarchyKind.Notebook)
        {
            (notebook, parentId, groups) = (root, string.Empty, []);
        }
        else
        {
            var location = await new HierarchyWalk(gateway).ContainerLocationAsync(objectId, ct).ConfigureAwait(false);
            notebook = location.Notebook
                ?? throw new OneNoteException(ErrorCode.JournalFailed, $"The journal entry for {objectId} couldn't be made: its notebook wasn't found.");
            (parentId, groups) = (location.ParentId, location.Groups);
        }

        var entry = new JournalEntry
        {
            EntryId = Guid.NewGuid(),
            CorrelationId = request.CorrelationId ?? string.Empty,
            Operation = request.Operation ?? string.Empty,
            OperationClass = request.OperationClass.ToString(),
            SnapshotKind = snapshotKind,
            ObjectId = objectId,
            NotebookId = notebook.Id,
            NotebookPath = GrantKeys.NotebookKey(notebook.Path),
            NotebookName = notebook.DisplayName,
            ParentId = parentId,
            ParentNamePath = string.Join('/', groups),
            SectionKey = root.Kind == HierarchyKind.Section && root.Encrypted ? GrantKeys.SectionKey(root.Path) : null,
            Title = root.DisplayName,
            TakenAtUtc = clock.GetUtcNow().UtcDateTime,
            BinaryComplete = true,
            MissingBinary = [],
            Xml = xml,
        };
        return entry;
    }
}
