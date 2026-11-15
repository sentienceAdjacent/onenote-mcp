using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>delete_hierarchy_object</c> (story 5.5): <c>DeleteHierarchy</c> with <c>permanent=false</c> of a page, section or
/// section group, through the write pipeline (AD-7) as a <c>SoftDelete</c>.
/// <list type="number">
/// <item>Step 1, before the lock: an empty <c>objectId</c>, or a basis with <c>elementHashes</c>, is
/// <see cref="ErrorCode.InvalidArgument"/>. The target is read at <c>self</c>: a notebook is
/// <see cref="ErrorCode.NotSupported"/> with AD-7's notebook <c>userAction</c>, before any grant dialog; a basis
/// <c>pageHash</c> for a target that isn't a page is <see cref="ErrorCode.InvalidArgument"/>. Then the write grant on the
/// target's notebook (the grant dialog as for <c>create_page</c>). No read grant is ever asked for (AD-21 Deletes).</item>
/// <item value="3">A page (<see cref="SoftDeletePage"/>, a <see cref="PageWrite"/>): its section, its page XML, its entry in
/// its section's pages (hierarchy <c>lastModifiedTime</c>, subpages), and the notebook's Deleted Pages. Refused before COM:
/// the recycle bin or anything in it (<see cref="ErrorCode.ObjectNotFound"/>), a locked section
/// (<see cref="ErrorCode.SectionLocked"/>), a password-protected section (<see cref="ErrorCode.NotSupported"/>, AD-21's
/// <c>userAction</c>), a page with subpages (<see cref="ErrorCode.InvalidArgument"/>, with the count; Q2). The caller's
/// page-XML date and <c>pageHash</c> are compared (AD-17), and <c>DeleteHierarchy</c> is sent the page's hierarchy date. Its
/// snapshot is a <c>page</c> entry. After the delete, Deleted Pages is read again and the new ID picked (Q3); the entry is
/// rewritten once with it, or the result carries <c>recycleBinIdMissing</c>.</item>
/// <item value="3">A section or section group (<see cref="SoftDeleteNode"/>): read at <c>Pages</c> scope, which is its
/// snapshot (a <c>hierarchy</c> entry, <see cref="HierarchySnapshot"/>). Refused before COM: the recycle bin or anything in
/// it, a locked section, or a group holding one anywhere beneath it. Its basis is its hierarchy date, compared and sent.</item>
/// </list>
/// The result is <c>meta.objectId</c>, <c>meta.journalEntryId</c> and, for a page whose new ID was read back,
/// <c>meta.recycleBinId</c> (Q6). Messages carry IDs and counts only, never a name (AD-9, AD-21).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SoftDeleteUseCase : IUseCase
{
    /// <summary>The tool's name, recorded in the journal entry.</summary>
    public const string OperationName = "delete_hierarchy_object";

    /// <summary>AD-7's notebook <c>userAction</c> (Phill, 2026-11-12, 5.2 plan Q3).</summary>
    public const string NotebookUserAction =
        "COM can't delete a notebook. Ask the user to close it in OneNote, then delete its folder in File Explorer or OneDrive.";

    /// <summary>AD-21's <c>userAction</c> for a page in a password-protected section (Phill, 2026-11-12, 5.2 Fable N4).</summary>
    public const string ProtectedPageUserAction =
        "A page in a password-protected section can only be deleted permanently: use a permanent delete, here or in OneNote.";

    /// <summary>Q2 (Phill, 2026-11-13): what to do about a page with subpages.</summary>
    public const string SubpagesUserAction =
        "Delete the page's subpages first, deepest first, or move them out from under it in OneNote, then delete the page.";

    /// <summary>What to do when the target isn't a live page, section or section group.</summary>
    public const string NotATargetUserAction =
        "Pass the ID of a page, section or section group that isn't in the recycle bin. Read the hierarchy (get_hierarchy) for a current ID.";

    /// <summary>What to do about a basis this tool refuses.</summary>
    public const string BasisUserAction =
        "For a page, pass basis.lastModifiedTime and basis.pageHash from get_page_content's meta.basis (no elementHashes). "
        + "For a section or section group, pass only basis.lastModifiedTime: the lastModifiedTime attribute on its element in get_hierarchy.";

    /// <summary>What to do after a page delete's <c>noBasis</c>, <c>partialBasis</c> or a basis date that isn't a date.</summary>
    public const string PageBasisUserAction =
        "Pass basis.lastModifiedTime and basis.pageHash from get_page_content's meta.basis, unchanged, and leave out its elementHashes, so a change made since your read is caught.";

    /// <summary>What to do after <c>noBasis</c>, <c>partialBasis</c> or a stale date on a section or section group.</summary>
    public const string NodeBasisUserAction =
        "Pass the object's lastModifiedTime from get_hierarchy (the attribute on its element) unchanged as basis.lastModifiedTime, so a change made since your read is caught.";

    private readonly PageWriteServices pageServices;
    private readonly HierarchySnapshot hierarchySnapshot;
    private readonly WritePipeline pipeline;

    /// <param name="pageServices">A page delete's gateway, grant checks and page snapshot (as a page write's).</param>
    /// <param name="hierarchySnapshot">A section's or section group's snapshot (AD-8).</param>
    /// <param name="pipeline">The AD-7 step runner.</param>
    public SoftDeleteUseCase(PageWriteServices pageServices, HierarchySnapshot hierarchySnapshot, WritePipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pageServices);
        ArgumentNullException.ThrowIfNull(hierarchySnapshot);
        ArgumentNullException.ThrowIfNull(pipeline);
        this.pageServices = pageServices;
        this.hierarchySnapshot = hierarchySnapshot;
        this.pipeline = pipeline;
    }

    public OperationClass Class => OperationClass.SoftDelete;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string objectId, PageWriteBasis? basis, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Step 1, before anything is read or locked.
        if (string.IsNullOrEmpty(objectId))
        {
            throw Invalid("The argument objectId is empty, so nothing was read or deleted.", NotATargetUserAction);
        }

        if (basis?.ElementHashes is not null)
        {
            throw Invalid("The basis carries elementHashes, which a delete never compares, so nothing was deleted.", BasisUserAction);
        }

        // The target's kind decides the steps: a notebook is refused here, before any grant dialog (AD-7).
        var selfXml = await pageServices.Gateway.GetHierarchyAsync(objectId, HierarchyScope.Self, ct).ConfigureAwait(false);
        var self = XmlReads.Readable(() => DeleteTargets.Root(selfXml), "GetHierarchy");
        switch (self.Kind)
        {
            case HierarchyKind.Notebook:
                throw new OneNoteException(
                    ErrorCode.NotSupported,
                    $"Object {objectId} is a notebook, which COM can't delete, so nothing was deleted.",
                    userAction: NotebookUserAction,
                    retryable: false);
            case HierarchyKind.Page:
                return await DeletePageAsync(context, objectId, basis, ct).ConfigureAwait(false);
            case HierarchyKind.Section or HierarchyKind.SectionGroup:
                if (basis?.PageHash is not null)
                {
                    throw Invalid($"The basis carries a pageHash, but {objectId} isn't a page, so nothing was deleted.", BasisUserAction);
                }

                return await DeleteNodeAsync(context, objectId, basis, ct).ConfigureAwait(false);
            default:
                throw NotATarget(objectId, "isn't a page, section or section group");
        }
    }

    private async Task<ToolSuccess> DeletePageAsync(ToolCallContext context, string pageId, PageWriteBasis? basis, CancellationToken ct)
    {
        var write = new SoftDeletePage(pageServices, context.CorrelationId, pageId, basis, pageServices.ReadAccess.Now());
        var result = await pipeline.RunAsync(context, write, ct).ConfigureAwait(false);
        return new ToolSuccess(
            null,
            new ToolMeta(context.CorrelationId, ObjectId: pageId, JournalEntryId: Id(write.JournalEntryId), RecycleBinId: write.RecycleBinId),
            result.Warnings);
    }

    private async Task<ToolSuccess> DeleteNodeAsync(ToolCallContext context, string objectId, PageWriteBasis? basis, CancellationToken ct)
    {
        var delete = new SoftDeleteNode(pageServices.Gateway, pageServices.WriteAccess, hierarchySnapshot, context.CorrelationId, objectId, basis?.LastModifiedTime, basis is null);
        var warnings = await pipeline.RunAsync(context, delete, ct).ConfigureAwait(false);
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, ObjectId: objectId, JournalEntryId: Id(delete.JournalEntryId)), warnings);
    }

    private static string? Id(Guid? entryId) => entryId?.ToString("D", CultureInfo.InvariantCulture);

    private static OneNoteException Invalid(string message, string userAction) =>
        new(ErrorCode.InvalidArgument, message, userAction: userAction, retryable: false);

    private static OneNoteException NotATarget(string objectId, string why) =>
        new(ErrorCode.ObjectNotFound, $"Object {objectId} {why}, so nothing was deleted.", userAction: NotATargetUserAction);

    /// <summary>A notebook's Deleted Pages, read through its recycle bin; none when the notebook has no recycle bin yet.</summary>
    private static async Task<IReadOnlyList<DeletedPage>> DeletedPagesAsync(IOneNoteGateway gateway, string notebookId, CancellationToken ct)
    {
        var children = await gateway.GetHierarchyAsync(notebookId, HierarchyScope.Children, ct).ConfigureAwait(false);
        if (XmlReads.Readable(() => RecycleBin.BinId(children), "GetHierarchy") is not { Length: > 0 } binId)
        {
            return [];
        }

        var bin = await gateway.GetHierarchyAsync(binId, HierarchyScope.Pages, ct).ConfigureAwait(false);
        return XmlReads.Readable(() => RecycleBin.DeletedPages(bin), "GetHierarchy");
    }

    /// <summary>
    /// A page's soft delete as a <see cref="PageWrite"/> of class <c>SoftDelete</c>: the page path's reads, basis check,
    /// snapshot and entry naming are <see cref="PageWrite"/>'s; this adds the hierarchy date, the subpage refusal, the
    /// protected-section refusal and the <c>recycleBinId</c> read-back and rewrite (AD-7, AD-8, AD-17).
    /// </summary>
    private sealed class SoftDeletePage(PageWriteServices services, string correlationId, string pageId, PageWriteBasis? basis, DateTimeOffset callStart)
        : PageWrite(services, correlationId, OperationName, pageId, callStart)
    {
        private SectionPage? listed;
        private string? notebookId;
        private IReadOnlyList<DeletedPage> binBefore = [];
        private string? hierarchyDate;

        public override OperationClass Class => OperationClass.SoftDelete;

        protected override string BasisUserAction => PageBasisUserAction;

        protected override string UnbasedRisk => "a change made since your own read wasn't caught";

        /// <summary>The page's new ID in Deleted Pages, once the read-back picked one (Q3); null otherwise.</summary>
        public string? RecycleBinId { get; private set; }

        public override string? FailureNote => JournalEntryId is { } entryId
            ? string.Create(CultureInfo.InvariantCulture, $"The page as it was before this delete is in journal entry {entryId:D} (restore_from_journal).")
            : null;

        /// <summary>AD-21 Deletes: a page in a protected section is refused until 5.5's probe says where it would land.</summary>
        protected override void CheckSection(HierarchyNode owningSection)
        {
            if (owningSection.Encrypted)
            {
                throw new OneNoteException(
                    ErrorCode.NotSupported,
                    $"Page {PageId} is in password-protected section {owningSection.Id}, where it can't be soft-deleted, so nothing was deleted.",
                    userAction: ProtectedPageUserAction,
                    retryable: false);
            }
        }

        public override async Task ReadAsync(CancellationToken ct)
        {
            // The section, the page node and its piBasic XML (PageWrite's order: recycle bin, SectionLocked, protected, page).
            await base.ReadAsync(ct).ConfigureAwait(false);
            var section = OwningSection ?? throw new InvalidOperationException("Step 3 hasn't read the section.");

            var pages = await Services.Gateway.GetHierarchyAsync(section.Id, HierarchyScope.Pages, ct).ConfigureAwait(false);
            listed = XmlReads.Readable(() => DeleteTargets.Page(pages, PageId), "GetHierarchy");

            notebookId = (await new HierarchyWalk(Services.Gateway).OwningNotebookAsync(section.Id, ct).ConfigureAwait(false))?.Id;
            binBefore = notebookId is null ? [] : await DeletedPagesAsync(Services.Gateway, notebookId, ct).ConfigureAwait(false);
        }

        public override void Validate()
        {
            var page = listed ?? throw NotATarget(PageId, "isn't listed in its section");
            if (page.Subpages > 0)
            {
                // Q2 (a): nothing is re-parented without the caller asking. A count only, never a subpage's ID or name.
                throw Invalid(
                    string.Create(CultureInfo.InvariantCulture, $"Page {PageId} has {page.Subpages} subpage(s), which a delete would leave behind, so nothing was deleted."),
                    SubpagesUserAction);
            }
        }

        public override Task CheckBasisAsync(CancellationToken ct)
        {
            // The caller's page-XML basis is compared (date and pageHash); DeleteHierarchy checks the hierarchy date, so that
            // is what it is sent, from the same step 3 (AD-17's page-delete exception).
            CheckBasis(basis, [], comparePageHash: true);
            var date = listed?.LastModifiedTime;
            if (OneNoteDates.ToUtc(date) is null)
            {
                throw new OneNoteException(
                    ErrorCode.Internal,
                    $"OneNote's hierarchy gives page {PageId} no lastModifiedTime the server can send, so nothing was deleted.",
                    userAction: "Re-read the page's section (get_hierarchy with scope pages), then retry.");
            }

            hierarchyDate = date;
            return Task.CompletedTask;
        }

        public override async Task<PageWriteResult> CallAsync(CancellationToken ct)
        {
            await CallNamingEntryAsync(() => Services.Gateway.DeleteHierarchyAsync(PageId, hierarchyDate ?? string.Empty, permanent: false, ct)).ConfigureAwait(false);
            return new PageWriteResult(null, [.. Warnings]);
        }

        /// <summary>
        /// Between 7 and 8, under the lock (AD-7 <c>SoftDelete</c>): Deleted Pages read again, the new ID picked (Q3), and
        /// the entry rewritten once with it. Neither ever fails the delete.
        /// </summary>
        public override async Task<PageWriteResult> AfterCallAsync(PageWriteResult result, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(result);
            var after = notebookId is null ? [] : await DeletedPagesAsync(Services.Gateway, notebookId, ct).ConfigureAwait(false);
            var picked = RecycleBin.PickNewPage(binBefore, after, listed?.Name ?? PageNode?.Name ?? string.Empty);
            if (picked is null)
            {
                var known = binBefore.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
                var added = after.Count(p => !known.Contains(p.Id));
                return Missing(result, added == 0
                    ? "no page is new in Deleted Pages"
                    : string.Create(CultureInfo.InvariantCulture, $"{added} pages are new in Deleted Pages, and not exactly one of them has its name"));
            }

            RecycleBinId = picked;
            var file = JournalFile ?? throw new InvalidOperationException("Step 6 hasn't stored the entry.");
            return JournalStore.TryAddRecycleBinId(file.Path, file.EntryId, picked) is { } why
                ? Missing(result, $"the journal entry couldn't be rewritten with it ({why})")
                : result;
        }

        /// <summary>The read-back threw (its deadline or a cancel included): the delete stands, with <c>recycleBinIdMissing</c>.</summary>
        public override PageWriteResult AfterCallFailed(PageWriteResult result, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(exception);
            var why = exception switch
            {
                OneNoteException one => one.Code.ToString(),
                OperationCanceledException => "the write's deadline or a cancel",
                _ => exception.GetType().Name,
            };
            return Missing(result, $"Deleted Pages couldn't be read again ({why})");
        }

        private PageWriteResult Missing(PageWriteResult result, string why) =>
            new(result.Basis, [.. result.Warnings, new ToolWarning(
                ToolWarnings.RecycleBinIdMissing,
                RecycleBinId is null
                    ? $"Page {PageId} was moved to the recycle bin, but its new ID there wasn't found: {why}. Its journal entry has no recycleBinId, so a restore matches it by name."
                    : $"Page {PageId} was moved to the recycle bin as {RecycleBinId}, but {why}. Its journal entry has no recycleBinId, so a restore matches it by name.")]);
    }

    /// <summary>
    /// A section's or section group's soft delete (AD-7 <c>SoftDelete</c>): the target read at <c>Pages</c> scope, refused
    /// in the recycle bin or when a locked section is in it, its hierarchy date compared and sent (AD-17), and a
    /// <c>hierarchy</c> snapshot (AD-8). The page-text cache drops the pages step 3 read (AD-12).
    /// </summary>
    private sealed class SoftDeleteNode(
        IOneNoteGateway gateway,
        WriteAccess access,
        HierarchySnapshot snapshot,
        string correlationId,
        string objectId,
        string? basisDate,
        bool noBasis) : WriteOperation<IReadOnlyList<ToolWarning>>
    {
        private readonly List<ToolWarning> warnings = [];
        private string? xml;
        private DeleteTarget? node;
        private IReadOnlyList<string> pageIds = [];
        private string? expected;

        public override OperationClass Class => OperationClass.SoftDelete;

        public override IReadOnlyCollection<string> WrittenPageIds => pageIds;

        /// <summary>The journal entry step 6 stored; null until the snapshot is taken.</summary>
        public Guid? JournalEntryId { get; private set; }

        public override string? FailureNote => JournalEntryId is { } entryId
            ? string.Create(CultureInfo.InvariantCulture, $"The {Noun} as it was before this delete is recorded in journal entry {entryId:D}.")
            : null;

        private string Noun => node?.Kind == HierarchyKind.SectionGroup ? "section group" : "section";

        public override async Task AuthorizeAsync(CancellationToken ct) =>
            await access.CheckAsync(objectId, () => NotATarget(objectId, "isn't inside a notebook"), ct).ConfigureAwait(false);

        public override async Task ReadAsync(CancellationToken ct)
        {
            var read = await gateway.GetHierarchyAsync(objectId, HierarchyScope.Pages, ct).ConfigureAwait(false);
            node = XmlReads.Readable(() => DeleteTargets.Root(read), "GetHierarchy");
            pageIds = XmlReads.Readable(() => DeleteTargets.PageIds(read), "GetHierarchy");
            xml = read;
        }

        public override void Validate()
        {
            var target = node ?? throw new InvalidOperationException("Validate runs after ReadAsync.");
            if (target.IsRecycleBin)
            {
                throw NotATarget(objectId, "is a notebook's recycle bin");
            }

            if (target.Kind is not (HierarchyKind.Section or HierarchyKind.SectionGroup))
            {
                throw NotATarget(objectId, "isn't a section or section group");
            }

            if (target.InRecycleBin)
            {
                throw NotATarget(objectId, "is in the recycle bin");
            }

            if (target.Kind == HierarchyKind.Section)
            {
                if (target.Locked)
                {
                    throw new OneNoteException(ErrorCode.SectionLocked, $"Section {objectId} is locked, so nothing was deleted.");
                }

                return;
            }

            // A locked section anywhere beneath the group (probe L7 records what OneNote would do; the tool refuses first).
            var locked = XmlReads.Readable(() => LockedSections.Find(xml!), "GetHierarchy");
            if (locked.Count > 0)
            {
                throw new OneNoteException(
                    ErrorCode.SectionLocked,
                    string.Create(CultureInfo.InvariantCulture, $"Section group {objectId} holds {locked.Count} locked section(s), so nothing was deleted."));
            }
        }

        public override Task CheckBasisAsync(CancellationToken ct)
        {
            var current = node!.LastModifiedTime;
            if (OneNoteDates.ToUtc(current) is not { } now)
            {
                throw new OneNoteException(
                    ErrorCode.Internal,
                    $"OneNote's hierarchy gives {Noun} {objectId} no lastModifiedTime the server can send, so nothing was deleted.",
                    userAction: "Re-read the hierarchy (get_hierarchy), then retry.");
            }

            if (basisDate is null)
            {
                warnings.Add(noBasis
                    ? new ToolWarning(
                        ToolWarnings.NoBasis,
                        $"No basis was passed, so the delete of {Noun} {objectId} was checked against it as the server just read it: a change made since your own read wasn't caught.",
                        NodeBasisUserAction)
                    : new ToolWarning(
                        ToolWarnings.PartialBasis,
                        $"The basis for {Noun} {objectId} is partial: it has no lastModifiedTime, so the {Noun}'s current one was used.",
                        NodeBasisUserAction));
                expected = current;
                return Task.CompletedTask;
            }

            var sent = OneNoteDates.ToUtc(basisDate)
                ?? throw Invalid($"The basis lastModifiedTime for {objectId} isn't a date OneNote writes, so nothing was deleted.", NodeBasisUserAction);
            if (sent != now)
            {
                throw new OneNoteException(
                    ErrorCode.StaleWrite,
                    $"The {Noun} {objectId} changed since the basis was read (its lastModifiedTime differs), so nothing was deleted.");
            }

            // The caller's date, never the live one (AD-17).
            expected = basisDate;
            return Task.CompletedTask;
        }

        public override async Task SnapshotAsync(CancellationToken ct)
        {
            var written = await snapshot.TakeAsync(new HierarchySnapshotRequest(correlationId, OperationName, Class, objectId, xml!), ct).ConfigureAwait(false);
            JournalEntryId = written.EntryId;
        }

        public override async Task<IReadOnlyList<ToolWarning>> CallAsync(CancellationToken ct)
        {
            await CallNamingFailureNoteAsync(() => gateway.DeleteHierarchyAsync(objectId, expected ?? string.Empty, permanent: false, ct)).ConfigureAwait(false);
            return warnings;
        }
    }
}
