using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>permanently_delete_hierarchy_object</c> (story 5.6): <c>DeleteHierarchy</c> with <c>permanent=true</c> through the
/// write pipeline (AD-7) as a <c>PermanentDelete</c>, under its 600 s deadline (<see cref="PermanentDeleteWrite"/>). A
/// live page gets its page snapshot; a section, live or recycled, its page entries (5.6 plan Q3 b) and then a
/// <c>Publish</c> backup with its <c>onenoteBackup</c> entry (<see cref="SectionBackup"/>); an empty section group a
/// <c>hierarchy</c> entry. A notebook, a password-protected section, a non-empty section group, a recycled page and the
/// Deleted Pages section are <see cref="ErrorCode.NotSupported"/>; the recycle bin itself is
/// <see cref="ErrorCode.ObjectNotFound"/>. The result is <c>meta.objectId</c>, <c>meta.journalEntryId</c> and, for a
/// section, <c>meta.backupFile</c> (a file name only) and <c>meta.pageEntryIds</c> in section order. Messages carry IDs and
/// counts only (AD-9, AD-21).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PermanentDeleteUseCase(PermanentDeleteServices services, WritePipeline pipeline) : IUseCase
{
    public OperationClass Class => OperationClass.PermanentDelete;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string objectId, PageWriteBasis? basis, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);

        // AD-7 step 1, before anything is read or locked (5.6 plan): an empty objectId, and a basis with element hashes,
        // which a hierarchy delete never compares.
        if (string.IsNullOrEmpty(objectId))
        {
            throw new OneNoteException(
                ErrorCode.InvalidArgument,
                "The argument objectId is empty, so nothing was read or deleted.",
                userAction: "Pass objectId: the ID of the page, section or section group to delete permanently, from get_hierarchy.",
                retryable: false);
        }

        if (basis?.ElementHashes is { Count: > 0 })
        {
            throw new OneNoteException(
                ErrorCode.InvalidArgument,
                $"The basis for {objectId} carries elementHashes, which a permanent delete doesn't compare, so nothing was read or deleted.",
                userAction: PermanentDeleteWrite.BasisUserAction,
                retryable: false);
        }

        var write = new PermanentDeleteWrite(services, context.CorrelationId, objectId, basis, services.ReadAccess.Now());
        var result = await pipeline.RunAsync(context, write, ct).ConfigureAwait(false);
        return new ToolSuccess(
            null,
            new ToolMeta(
                context.CorrelationId,
                ObjectId: objectId,
                JournalEntryId: write.JournalEntryId?.ToString("D", CultureInfo.InvariantCulture),
                BackupFile: write.BackupFile,
                PageEntryIds: write.PageEntryIds?.Select(id => id.ToString("D", CultureInfo.InvariantCulture)).ToList()),
            result);
    }
}

/// <summary>What a permanent delete uses: the gateway, both access checks, the three step-6 snapshots.</summary>
[SupportedOSPlatform("windows")]
public sealed record PermanentDeleteServices(
    IOneNoteGateway Gateway,
    WriteAccess WriteAccess,
    ReadAccess ReadAccess,
    PageSnapshot PageSnapshot,
    HierarchySnapshot HierarchySnapshot,
    SectionBackup SectionBackup);

/// <summary>
/// One permanent delete through AD-7's steps (story 5.6).
/// <list type="number">
/// <item>Step 1, before the lock, in this order: a <c>pageHash</c> for a target that isn't a page is
/// <see cref="ErrorCode.InvalidArgument"/>; a notebook, then a password-protected section (<c>encrypted</c>, read live) is
/// <see cref="ErrorCode.NotSupported"/>, before any dialog; a page in a protected section with no read grant held at the
/// call's start and a <c>pageHash</c> is <see cref="ErrorCode.ReadNotGranted"/>, never prompted (AD-17, AD-21). Step 4's
/// refusals that need no lock run here too, before any dialog, a page's locked section and subpages included (fresh
/// review). Then the
/// write grant (its dialog, AD-6), then the permanent-delete grant: without a stored one, the one-call confirmation
/// (<see cref="PermanentDeleteUnconfirmed"/>; 5.6 plan Q1 c).</item>
/// <item value="3">Step 3 reads the target at <c>self</c>, a section or group also at <c>pages</c> scope, a page its section
/// and piBasic XML. Step 4's refusals run in AD-7's order as soon as what each needs is read: the recycle bin itself
/// (<see cref="ErrorCode.ObjectNotFound"/>); a notebook, a protected section, a non-empty group, a recycled page (each
/// <see cref="ErrorCode.NotSupported"/>); the Deleted Pages section (<see cref="ErrorCode.NotSupported"/>, Q4 a); a locked
/// section holding the page (<see cref="ErrorCode.SectionLocked"/>); a page with subpages
/// (<see cref="ErrorCode.InvalidArgument"/>, with the count).</item>
/// <item value="5">Step 5 (AD-17): a page compares the caller's page-XML date and <c>pageHash</c> and sends the page's
/// hierarchy date; a section or group compares and sends its hierarchy date, the caller's string. Never <c>MinValue</c>.</item>
/// <item>Step 6: the page snapshot; the empty group's <c>hierarchy</c> entry; a section's page entries, then its backup and
/// <c>onenoteBackup</c> entry. Any failure here and the delete doesn't run.</item>
/// <item>Step 7: <c>DeleteHierarchy(id, date, true)</c>, never retried; a failure names the journal entry.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PermanentDeleteWrite : WriteOperation<IReadOnlyList<ToolWarning>>
{
    /// <summary>The tool's name, recorded in the journal entries.</summary>
    public const string OperationName = "permanently_delete_hierarchy_object";

    /// <summary>AD-7's notebook <c>userAction</c>, word for word.</summary>
    public const string NotebookUserAction =
        "COM can't delete a notebook. Ask the user to close it in OneNote, then delete its folder in File Explorer or OneDrive.";

    /// <summary>AD-21's protected-section <c>userAction</c> (5.2 plan Q1).</summary>
    public const string ProtectedSectionUserAction =
        "A password-protected section can't be deleted permanently here: its backup would be written unencrypted. Ask the user to soft-delete it (delete it in OneNote), then empty the recycle bin in OneNote.";

    public const string RecycledPageUserAction =
        "This page is already in the recycle bin, where COM can't delete it. Ask the user to empty the recycle bin in OneNote (or delete the page from Deleted Pages there).";

    public const string DeletedPagesUserAction =
        "Deleted Pages is part of the recycle bin and can't be deleted here. Ask the user to empty the recycle bin in OneNote.";

    public const string NonEmptyGroupUserAction =
        "Only an empty section group can be deleted permanently: nothing can back up a group. Delete each section and group inside it first (each section gets its own backup), or ask the user to delete the group in OneNote.";

    public const string SubpagesUserAction =
        "Delete or move the page's subpages first, each with its own call, or ask the user to delete the page in OneNote.";

    public const string NotATargetUserAction =
        "Pass the ID of a page, section or section group (not a notebook, not the recycle bin). Read the hierarchy with get_hierarchy and use a current ID.";

    /// <summary>
    /// A page in a protected section with no read grant and no stored permanent-delete grant: no confirmation can be shown
    /// without naming what is inside the section (AD-21).
    /// </summary>
    public static string ProtectedNoConfirmationUserAction { get; } =
        "This page is in a password-protected section you can't read, so the user can't be asked to confirm its permanent delete in a dialog. "
        + "Ask the user whether they want it deleted for good. They can allow permanent deletes in the notebook by running "
        + GrantCli.Run("--permanent-delete \"<notebook name>\"") + " (with the notebook's name), then you retry; or you can read the page with get_page_content first "
        + "(which asks them for the section's read grant) and retry, when a dialog asks them to confirm. Never run the grant command yourself.";

    public const string BasisUserAction =
        "Pass basis as {lastModifiedTime, pageHash} from get_page_content's meta.basis for a page, or {lastModifiedTime} from the node's element in get_hierarchy for a section or section group; leave out elementHashes.";

    public const string ReadFirstUserAction =
        "In a password-protected section, a permanent delete may carry only basis.lastModifiedTime unless the section has a read grant. Read the page with get_page_content first "
        + "(it asks the user for the grant), or pass only basis.lastModifiedTime.";

    private readonly PermanentDeleteServices services;
    private readonly string correlationId;
    private readonly string objectId;
    private readonly PageWriteBasis? basis;
    private readonly DateTimeOffset callStart;
    private readonly List<ToolWarning> warnings = [];
    private bool confirmed;
    private PermanentDeleteTarget? target;
    private HierarchyNode? section;
    private string? basicXml;
    private string? containerXml;
    private IReadOnlyList<string> pageIds = [];
    private string? expectedLastModified;
    private WriteTarget? grantTarget;

    public PermanentDeleteWrite(PermanentDeleteServices services, string correlationId, string objectId, PageWriteBasis? basis, DateTimeOffset callStart)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;
        this.correlationId = correlationId ?? string.Empty;
        this.objectId = objectId ?? string.Empty;
        this.basis = basis;
        this.callStart = callStart;
    }

    public override OperationClass Class => OperationClass.PermanentDelete;

    /// <summary>The entry <c>meta.journalEntryId</c> reports: the page, <c>hierarchy</c> or <c>onenoteBackup</c> entry; null until step 6 stores it.</summary>
    public Guid? JournalEntryId { get; private set; }

    /// <summary>A section's backup, by file name only (<c>meta.backupFile</c>); null otherwise.</summary>
    public string? BackupFile { get; private set; }

    /// <summary>A section's page entries, in section order (<c>meta.pageEntryIds</c>, 5.6 plan Q3 b); null otherwise.</summary>
    public IReadOnlyList<Guid>? PageEntryIds { get; private set; }

    /// <summary>A page, or a section's pages: dropped from the search cache whatever the outcome (AD-7, AD-12).</summary>
    public override IReadOnlyCollection<string> WrittenPageIds =>
        target?.Kind == HierarchyKind.Page ? [objectId] : pageIds;

    public override string? FailureNote => JournalEntryId is not { } entryId
        ? null
        : target?.Kind switch
        {
            HierarchyKind.Section => string.Create(
                CultureInfo.InvariantCulture,
                $"The section's backup is journal entry {entryId:D}, and its {PageEntryIds?.Count ?? 0} page(s) are in their own entries (restore_from_journal)."),
            HierarchyKind.SectionGroup => string.Create(CultureInfo.InvariantCulture, $"The section group is recorded in journal entry {entryId:D}."),
            _ => string.Create(CultureInfo.InvariantCulture, $"The page as it was before this delete is in journal entry {entryId:D} (restore_from_journal)."),
        };

    // --- Step 1 ---------------------------------------------------------------------------------------------------------

    public override async Task AuthorizeAsync(CancellationToken ct)
    {
        var found = await ReadTargetAsync(ct).ConfigureAwait(false);
        if (basis?.PageHash is not null && found.Kind != HierarchyKind.Page)
        {
            throw new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{objectId} isn't a page, so a basis pageHash means nothing for it; nothing was read or deleted.",
                userAction: BasisUserAction,
                retryable: false);
        }

        // Before any dialog, step 4's refusals that need no lock, so no grant is asked for a delete that can't run (5.6
        // plan; review). Step 4 checks each again under the lock.
        RefuseUndeletable(found);
        var gateway = services.Gateway;
        HierarchyNode? owner = null;
        string? sectionPages = null;
        var readGrant = true;
        int? pageCount = null;
        switch (found.Kind)
        {
            case HierarchyKind.SectionGroup:
                RefuseNonEmptyGroup(await gateway.GetHierarchyAsync(objectId, HierarchyScope.Pages, ct).ConfigureAwait(false));
                break;

            case HierarchyKind.Section:
                var pages = await gateway.GetHierarchyAsync(objectId, HierarchyScope.Pages, ct).ConfigureAwait(false);
                pageCount = XmlReads.Readable(() => PermanentDeleteTarget.PageIds(pages), $"GetHierarchy({objectId})").Count;
                break;

            case HierarchyKind.Page:
                owner = await new HierarchyWalk(gateway).OwningSectionAsync(objectId, ct).ConfigureAwait(false)
                    ?? throw NotATarget("isn't in a section");
                readGrant = services.ReadAccess.HoldsReadGrant(owner, callStart);
                if (!readGrant)
                {
                    CheckWithoutReadGrant(owner.Id);
                }

                RefuseRecycledPage(found, owner);

                // Fresh review: a locked section and subpages too, so the user never types a name for a refused delete.
                RefuseLockedSection(owner);
                sectionPages = await gateway.GetHierarchyAsync(owner.Id, HierarchyScope.Pages, ct).ConfigureAwait(false);
                RefuseSubpages(sectionPages, owner.Id);
                break;

            default:
                throw NotATarget("isn't a page, section or section group");
        }

        var access = await services.WriteAccess.CheckPermanentDeleteAsync(objectId, () => NotATarget("isn't inside a notebook"), ct).ConfigureAwait(false);
        grantTarget = access.Target;
        if (access.PermanentDeleteGranted || confirmed)
        {
            return;
        }

        // AD-21: without the section's read grant nothing inside it may be shown, its page's title included, so there is no
        // confirmation to raise: only the stored grant (or a read grant, then the dialog) lets this delete run.
        if (!readGrant)
        {
            throw new OneNoteException(
                ErrorCode.PermanentDeleteNotGranted,
                $"Section {owner!.Id} is password-protected and has no read grant, so this delete can't be confirmed in a dialog, and notebook {access.Target.NotebookId} has no permanent-delete grant; {objectId} wasn't deleted.",
                userAction: ProtectedNoConfirmationUserAction);
        }

        var name = PermanentDeleteDialog.Shown(found.Name, out var expected);
        throw new PermanentDeleteUnconfirmed(
            new PermanentDeleteQuestion(
                objectId,
                KindName(found.Kind),
                name,
                PermanentDeleteDialog.Shown(access.NotebookName, out _),
                expected.Length > 0 ? expected : "delete",
                PermanentDeleteDialog.Shown(access.Target.NotebookKey, out _, PermanentDeleteDialog.MaxPathLength),
                owner is null ? null : PermanentDeleteDialog.Shown(owner.Name, out _),
                pageCount,
                sectionPages is not null && SharesItsShownName(sectionPages, name) ? ShownDate(found.LastModifiedTime) : null),
            access.Target.NotebookId,
            () => confirmed = true);
    }

    // --- Steps 3 and 4 --------------------------------------------------------------------------------------------------

    public override async Task ReadAsync(CancellationToken ct)
    {
        var found = await ReadTargetAsync(ct).ConfigureAwait(false);
        target = found;

        // Step 4's refusals, in AD-7's order, each as soon as what it needs is read.
        RefuseUndeletable(found);
        var gateway = services.Gateway;
        switch (found.Kind)
        {
            case HierarchyKind.SectionGroup:
                containerXml = await gateway.GetHierarchyAsync(objectId, HierarchyScope.Pages, ct).ConfigureAwait(false);
                RefuseNonEmptyGroup(containerXml);
                break;

            case HierarchyKind.Section:
                containerXml = await gateway.GetHierarchyAsync(objectId, HierarchyScope.Pages, ct).ConfigureAwait(false);
                pageIds = XmlReads.Readable(() => PermanentDeleteTarget.PageIds(containerXml), $"GetHierarchy({objectId})");
                break;

            case HierarchyKind.Page:
                section = await new HierarchyWalk(gateway).OwningSectionAsync(objectId, ct).ConfigureAwait(false)
                    ?? throw NotATarget("isn't in a section");

                // Never asks (AD-21); checked again under the lock, before the page is read.
                if (!services.ReadAccess.HoldsReadGrant(section, callStart))
                {
                    CheckWithoutReadGrant(section.Id);
                }

                RefuseRecycledPage(found, section);
                RefuseLockedSection(section);
                containerXml = await gateway.GetHierarchyAsync(section.Id, HierarchyScope.Pages, ct).ConfigureAwait(false);
                basicXml = await gateway.GetPageContentAsync(objectId, PageInfo.Basic, ct).ConfigureAwait(false);
                break;

            default:
                throw NotATarget("isn't a page, section or section group");
        }
    }

    /// <summary>Step 4's last refusal: a page with subpages (5.5 plan Q2 a), with the count.</summary>
    public override void Validate()
    {
        if (target?.Kind != HierarchyKind.Page)
        {
            return;
        }

        RefuseSubpages(containerXml ?? string.Empty, section?.Id);
    }

    // --- Step 5 ---------------------------------------------------------------------------------------------------------

    public override Task CheckBasisAsync(CancellationToken ct)
    {
        var found = target ?? throw new InvalidOperationException("Step 3 hasn't read the target.");
        var filled = new List<string>();
        if (found.Kind == HierarchyKind.Page)
        {
            var current = XmlReads.Readable(() => PageBasis.From(basicXml ?? string.Empty), $"GetPageContent({objectId})");
            if (basis is not null)
            {
                if (basis.LastModifiedTime is null)
                {
                    filled.Add("it has no lastModifiedTime, so the page's current one was used");
                }
                else
                {
                    CompareDate(basis.LastModifiedTime, current.LastModifiedTime, "page");
                }

                if (basis.PageHash is null)
                {
                    filled.Add("it has no pageHash, so the page's content wasn't compared");
                }
                else if (!string.Equals(basis.PageHash, current.PageHash, StringComparison.Ordinal))
                {
                    throw Stale("page");
                }
            }

            // AD-17: DeleteHierarchy checks the page's hierarchy date, read under this lock.
            expectedLastModified = found.LastModifiedTime;
        }
        else
        {
            var what = found.Kind == HierarchyKind.Section ? "section" : "section group";
            expectedLastModified = found.LastModifiedTime;
            if (basis is not null)
            {
                if (basis.LastModifiedTime is null)
                {
                    filled.Add($"it has no lastModifiedTime, so the {what}'s current one was used");
                }
                else
                {
                    CompareDate(basis.LastModifiedTime, found.LastModifiedTime, what);

                    // The caller's date, never the live one, goes to COM (AD-17).
                    expectedLastModified = basis.LastModifiedTime;
                }
            }
        }

        if (OneNoteDates.ToUtc(expectedLastModified) is null)
        {
            // Never MinValue (AD-17): no date to send means no delete.
            throw new OneNoteException(
                ErrorCode.Internal,
                $"OneNote reported no usable lastModifiedTime for {objectId}, so it wasn't deleted.");
        }

        if (basis is null)
        {
            warnings.Add(new ToolWarning(
                ToolWarnings.NoBasis,
                $"No basis was passed, so the delete of {objectId} was checked against it as the server just read it: a change made since your own read was deleted too.",
                BasisUserAction));
        }
        else if (filled.Count > 0)
        {
            warnings.Add(new ToolWarning(ToolWarnings.PartialBasis, $"The basis for {objectId} is partial: {string.Join("; ", filled)}.", BasisUserAction));
        }

        return Task.CompletedTask;
    }

    // --- Step 6 ---------------------------------------------------------------------------------------------------------

    public override async Task SnapshotAsync(CancellationToken ct)
    {
        switch (target?.Kind)
        {
            case HierarchyKind.Page:
                var page = await services.PageSnapshot.TakeAsync(
                    new PageSnapshotRequest(correlationId, OperationName, OperationClass.PermanentDelete, objectId, basicXml ?? string.Empty), ct).ConfigureAwait(false);
                JournalEntryId = page.EntryId;
                break;

            case HierarchyKind.SectionGroup:
                var group = await services.HierarchySnapshot.TakeAsync(
                    new HierarchySnapshotRequest(correlationId, OperationName, OperationClass.PermanentDelete, objectId, containerXml ?? string.Empty), ct).ConfigureAwait(false);
                JournalEntryId = group.EntryId;
                break;

            case HierarchyKind.Section:
                // Q3 b: every page first, in section order, before Publish; any failure and nothing is deleted.
                var entries = new List<Guid>(pageIds.Count);
                foreach (var pageId in pageIds)
                {
                    var basic = await services.Gateway.GetPageContentAsync(pageId, PageInfo.Basic, ct).ConfigureAwait(false);
                    var written = await services.PageSnapshot.TakeAsync(
                        new PageSnapshotRequest(correlationId, OperationName, OperationClass.PermanentDelete, pageId, basic), ct).ConfigureAwait(false);
                    entries.Add(written.EntryId);
                }

                PageEntryIds = entries;
                var entry = await services.HierarchySnapshot.EntryAsync(
                    new HierarchySnapshotRequest(correlationId, OperationName, OperationClass.PermanentDelete, objectId, containerXml ?? string.Empty),
                    JournalEntry.OnenoteBackupKind,
                    ct).ConfigureAwait(false);
                var backup = await services.SectionBackup.TakeAsync(objectId, entry, ct).ConfigureAwait(false);
                JournalEntryId = backup.Entry.EntryId;
                BackupFile = backup.BackupFile;

                // The entries and the backup took time (Publish up to timeouts.publishSeconds), and OneNote or sync may have
                // changed the section meanwhile: anything not in the copy would go uncopied. Compare with step 3 again.
                await CheckSectionUnchangedAsync(ct).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException("Step 3 hasn't read the target.");
        }

        // Just before step 7, under the lock: the grants again, since a revoke may have landed during a long backup.
        RecheckGrants();
    }

    /// <summary>
    /// After a section's backup: its <c>lastModifiedTime</c>, its page IDs in order and each page's
    /// <c>lastModifiedTime</c> must still be step 3's, else <see cref="ErrorCode.StaleWrite"/>. Nothing is deleted; the
    /// backup and the entries stay, and the message names the <c>onenoteBackup</c> entry.
    /// </summary>
    private async Task CheckSectionUnchangedAsync(CancellationToken ct)
    {
        var self = await ReadTargetAsync(ct).ConfigureAwait(false);
        var pages = await services.Gateway.GetHierarchyAsync(objectId, HierarchyScope.Pages, ct).ConfigureAwait(false);
        var before = XmlReads.Readable(() => PermanentDeleteTarget.PageDates(containerXml ?? string.Empty), $"GetHierarchy({objectId})");
        var after = XmlReads.Readable(() => PermanentDeleteTarget.PageDates(pages), $"GetHierarchy({objectId})");
        if (!string.Equals(self.LastModifiedTime, target?.LastModifiedTime, StringComparison.Ordinal) || !before.SequenceEqual(after))
        {
            throw new OneNoteException(
                ErrorCode.StaleWrite,
                string.Create(CultureInfo.InvariantCulture, $"Section {objectId} changed while it was being backed up, so nothing was deleted. The backup made before the change is journal entry {JournalEntryId:D}."),
                userAction: "Re-read the section (get_hierarchy) and, if the user still wants it deleted for good, try again with its new lastModifiedTime.");
        }
    }

    /// <summary>
    /// Re-reads <c>settings.json</c> (AD-10) just before step 7: the notebook's write grant gone is
    /// <see cref="ErrorCode.WriteNotGranted"/>; the permanent-delete grant gone, when this call wasn't confirmed in the
    /// dialog, is <see cref="ErrorCode.PermanentDeleteNotGranted"/>. Nothing is deleted either way.
    /// </summary>
    private void RecheckGrants()
    {
        var granted = grantTarget ?? throw new InvalidOperationException("Step 1 hasn't run.");
        var (write, permanentDelete) = services.WriteAccess.StoredGrants(granted.NotebookKey);
        if (!write)
        {
            throw WriteAccess.NotGranted(granted.NotebookId, "it was revoked while this delete ran, so nothing was deleted.");
        }

        if (!permanentDelete && !confirmed)
        {
            throw new OneNoteException(
                ErrorCode.PermanentDeleteNotGranted,
                $"Notebook {granted.NotebookId}'s permanent-delete grant was revoked while this delete ran, so {objectId} wasn't deleted.",
                userAction: WriteAccess.PermanentDeleteNotGrantedUserAction);
        }
    }

    // --- Step 7 ---------------------------------------------------------------------------------------------------------

    public override async Task<IReadOnlyList<ToolWarning>> CallAsync(CancellationToken ct)
    {
        try
        {
            await services.Gateway.DeleteHierarchyAsync(objectId, expectedLastModified ?? string.Empty, permanent: true, ct).ConfigureAwait(false);
        }
        catch (OneNoteException ex) when (FailureNote is { } note)
        {
            // The pipeline still decides outcomeUnknown (AD-9); the message names the entry (2.11 plan Q1).
            throw new OneNoteException(ex.Code, $"{ex.Message} {note}", ex.ComHResult, ex, ex.OutcomeUnknown, ex.UserAction, ex.Retryable);
        }

        return [.. warnings];
    }

    // --- Helpers --------------------------------------------------------------------------------------------------------

    private async Task<PermanentDeleteTarget> ReadTargetAsync(CancellationToken ct)
    {
        var self = await services.Gateway.GetHierarchyAsync(objectId, HierarchyScope.Self, ct).ConfigureAwait(false);
        return XmlReads.Readable(() => PermanentDeleteTarget.Read(self), $"GetHierarchy({objectId})");
    }

    /// <summary>
    /// The refusals that need only the target's own read, in AD-7's order: the recycle bin itself
    /// (<see cref="ErrorCode.ObjectNotFound"/>), a notebook, a password-protected section, the Deleted Pages section (each
    /// <see cref="ErrorCode.NotSupported"/>; AD-7, AD-21, Q4 a).
    /// </summary>
    private void RefuseUndeletable(PermanentDeleteTarget found)
    {
        if (found.IsRecycleBin)
        {
            throw NotATarget("is the recycle bin itself");
        }

        if (found.Kind == HierarchyKind.Notebook)
        {
            throw new OneNoteException(
                ErrorCode.NotSupported,
                $"{objectId} is a notebook, which COM can't delete, so nothing was deleted.",
                userAction: NotebookUserAction);
        }

        if (found.Kind == HierarchyKind.Section && found.Encrypted)
        {
            throw new OneNoteException(
                ErrorCode.NotSupported,
                $"Section {objectId} is password-protected, so it can't be deleted permanently here; nothing was deleted.",
                userAction: ProtectedSectionUserAction);
        }

        if (found.Kind == HierarchyKind.Section && found.IsDeletedPages)
        {
            throw new OneNoteException(
                ErrorCode.NotSupported,
                $"Section {objectId} is Deleted Pages, which can't be deleted permanently here, so nothing was deleted.",
                userAction: DeletedPagesUserAction);
        }
    }

    /// <summary>A section group with any section or section group beneath it (<paramref name="groupPagesXml"/>, its <c>pages</c>-scope read): <see cref="ErrorCode.NotSupported"/>.</summary>
    private void RefuseNonEmptyGroup(string groupPagesXml)
    {
        var beneath = XmlReads.Readable(() => PermanentDeleteTarget.ContainersBeneath(groupPagesXml), $"GetHierarchy({objectId})");
        if (beneath > 0)
        {
            throw new OneNoteException(
                ErrorCode.NotSupported,
                string.Create(CultureInfo.InvariantCulture, $"Section group {objectId} holds {beneath} section(s) or section group(s), and only an empty one can be deleted permanently, so nothing was deleted."),
                userAction: NonEmptyGroupUserAction);
        }
    }

    /// <summary>A page in the recycle bin, or in a section that is: <see cref="ErrorCode.NotSupported"/> (COM can't, <c>0x8004200C</c>).</summary>
    private void RefuseRecycledPage(PermanentDeleteTarget found, HierarchyNode owner)
    {
        if (found.IsInRecycleBin || found.IsDeletedPages || owner.InRecycleBin)
        {
            throw new OneNoteException(
                ErrorCode.NotSupported,
                $"Page {objectId} is in the recycle bin, where COM can't delete it, so nothing was deleted.",
                userAction: RecycledPageUserAction);
        }
    }

    /// <summary>A page whose section is locked: <see cref="ErrorCode.SectionLocked"/>.</summary>
    private void RefuseLockedSection(HierarchyNode owner)
    {
        if (owner.Locked)
        {
            throw new OneNoteException(ErrorCode.SectionLocked, $"Section {owner.Id} is locked, so page {objectId} wasn't deleted.");
        }
    }

    /// <summary>
    /// A page with subpages in <paramref name="sectionPagesXml"/> (its section at <c>pages</c> scope; 5.5 plan Q2 a):
    /// <see cref="ErrorCode.InvalidArgument"/>, with the count. A page its section doesn't list is
    /// <see cref="ErrorCode.ObjectNotFound"/>.
    /// </summary>
    private void RefuseSubpages(string sectionPagesXml, string? sectionId)
    {
        var subpages = XmlReads.Readable(() => PermanentDeleteTarget.Subpages(sectionPagesXml, objectId), $"GetHierarchy({sectionId})")
            ?? throw NotATarget("isn't listed in its section");
        if (subpages > 0)
        {
            throw new OneNoteException(
                ErrorCode.InvalidArgument,
                string.Create(CultureInfo.InvariantCulture, $"Page {objectId} has {subpages} subpage(s), so nothing was deleted."),
                userAction: SubpagesUserAction,
                retryable: false);
        }
    }

    /// <summary>
    /// Whether another page in <paramref name="sectionPagesXml"/> shows as <paramref name="shown"/> in the confirmation
    /// (cleaned and cut by <see cref="PermanentDeleteDialog.Shown"/>, compared ignoring case; fresh review): two such pages
    /// would get word-for-word the same dialog.
    /// </summary>
    private bool SharesItsShownName(string sectionPagesXml, string shown) =>
        XmlReads.Readable(() => PermanentDeleteTarget.PageNames(sectionPagesXml), $"GetHierarchy({objectId})")
            .Any(page => !string.Equals(page.Id, objectId, StringComparison.Ordinal)
                && string.Equals(PermanentDeleteDialog.Shown(page.Name, out _), shown, StringComparison.OrdinalIgnoreCase));

    /// <summary>A page's hierarchy date as the confirmation shows it: UTC, to the second; null when OneNote gave none.</summary>
    private static string? ShownDate(string? lastModifiedTime) =>
        OneNoteDates.ToUtc(lastModifiedTime) is { } date
            ? date.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// AD-17 as amended by 5.2 (Fable N1): in a protected section with no read grant at the call's start, a page delete's
    /// basis may carry only the date; a <c>pageHash</c> is <see cref="ErrorCode.ReadNotGranted"/>, never prompted.
    /// </summary>
    private void CheckWithoutReadGrant(string sectionId)
    {
        if (basis?.PageHash is not null)
        {
            throw new OneNoteException(
                ErrorCode.ReadNotGranted,
                $"Section {sectionId} is password-protected and has no read grant, so a permanent delete there may carry only basis.lastModifiedTime. Nothing was deleted.",
                userAction: ReadFirstUserAction);
        }
    }

    private void CompareDate(string sent, string? current, string what)
    {
        var date = OneNoteDates.ToUtc(sent)
            ?? throw new OneNoteException(
                ErrorCode.InvalidArgument,
                $"The basis lastModifiedTime for {objectId} isn't a date OneNote writes, so nothing was deleted.",
                userAction: BasisUserAction,
                retryable: false);
        if (OneNoteDates.ToUtc(current) != date)
        {
            throw Stale(what);
        }
    }

    private OneNoteException Stale(string what) =>
        new(ErrorCode.StaleWrite, $"The {what} {objectId} changed since the basis was read, so nothing was deleted.");

    private OneNoteException NotATarget(string why) =>
        new(ErrorCode.ObjectNotFound, $"Object {objectId} {why}, so nothing was deleted.", userAction: NotATargetUserAction);

    private static string KindName(HierarchyKind kind) => kind switch
    {
        HierarchyKind.Page => "page",
        HierarchyKind.Section => "section",
        _ => "section group",
    };
}
