using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// <c>restore_from_recycle_bin</c> (story 3.5; AD-7, AD-8, AD-21): one <c>HierarchyWrite</c> that sends one
/// <c>UpdateHierarchy</c> of the target parent's XML listing the recycled item, as a move does (spike 3.1 Implication 6).
/// A restored page gets a new ID; a section keeps its ID.
/// <list type="number">
/// <item>Step 1, before the lock: the arguments (an empty <c>objectId</c>, an <c>entryId</c> that isn't a GUID, a
/// <c>position</c> below 1: <see cref="ErrorCode.InvalidArgument"/>). Then what the object is (only a page in Deleted
/// Pages or a section directly in the recycle bin), its soft-delete journal entry (<see cref="SoftDeleteEntries"/>, plain
/// metadata only), where it goes, and every refusal below, read now, so no grant dialog is shown and no lock taken for a
/// call that will be refused. Last, the write grant of the item's notebook and of the target's (AD-6); the pipeline
/// re-resolves both under the lock (story 3.12).</item>
/// <item value="3">Under the lock, the same reads again; a target other than step 1's is
/// <see cref="ErrorCode.WriteNotGranted"/>, retryable.</item>
/// <item>The refusals, again (step 4 repeats them all): a target in the recycle bin or of the wrong kind
/// (<see cref="ErrorCode.InvalidArgument"/>); a locked target section (<see cref="ErrorCode.SectionLocked"/>); a page's
/// <c>position</c> in a protected target without a read grant held at the call's start
/// (<see cref="ErrorCode.ReadNotGranted"/>); a sibling section's name, a position past the end or in a subpage block, and
/// a section landing on a key holding another section's read grant (<see cref="ErrorCode.InvalidArgument"/>). The XML must
/// pass the AD-16 gate. A plain section whose <c>parentId</c> is in another notebook (Q7) is restored here under its
/// original parent in its own notebook, last among the sections (<see cref="MoveTargetId"/> names where the use case then
/// moves it); with no original parent it is <see cref="ErrorCode.InvalidArgument"/>.</item>
/// <item value="6">One <c>hierarchy</c> journal entry of the target parent; nothing else, since the item stays in the bin
/// if the call fails.</item>
/// <item>One <c>UpdateHierarchy</c>, never retried. <c>0x80042004</c> keeps <see cref="ErrorCode.ObjectNotFound"/> and
/// sets <c>outcomeUnknown</c>, after a best-effort re-read, as a move does.</item>
/// </list>
/// Between 7 and 8, a section in a cloud notebook settles 10 s under the lock (warning <c>settled</c>), then the target is
/// re-read: a page's new ID is the one new ID there, preferring its name; a section must be back under the target with its
/// ID and gone from the bin. Messages carry IDs, counts and dates only, never a name (AD-9).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RestoreFromRecycleBinWrite : WriteOperation<MoveResult>
{
    public const string OperationName = "restore_from_recycle_bin";

    /// <summary>What to do about an object ID that isn't a restorable item.</summary>
    public const string ObjectUserAction =
        "Pass as objectId the ID of a page in a notebook's Deleted Pages, or of a section in its recycle bin. Read the notebook with get_hierarchy (scope pages) to find them.";

    /// <summary>What to do about a live item.</summary>
    public const string LiveUserAction =
        "It isn't in the recycle bin, so there is nothing to restore. To move it, use move_hierarchy_object.";

    /// <summary>What to do about the recycle bin itself, or Deleted Pages itself.</summary>
    public const string BinItselfUserAction =
        "Pass the ID of a page inside Deleted Pages, or of a section in the recycle bin, not the recycle bin or Deleted Pages itself.";

    /// <summary>What to do about a page inside a recycled section.</summary>
    public const string RecycledSectionPageUserAction =
        "Restore the section it is in instead (pass that section's ID as objectId): its pages come back with it.";

    /// <summary>What to do about a section group (5.1 constraint 8: OneNote bins a group's sections one by one).</summary>
    public const string GroupUserAction =
        "A section group isn't restored whole: OneNote puts a deleted group's sections in the recycle bin one by one. Restore each section, and rebuild the group with create_section_group and move_hierarchy_object if the user wants it back.";

    /// <summary>What to do about an <c>entryId</c> that isn't a GUID, or isn't a soft-delete entry for this item.</summary>
    public const string EntryIdUserAction =
        "Pass as entryId one of the entry IDs a refusal of this item listed (a soft-delete entry that matches this item, by its ID or its name), or leave entryId out.";

    /// <summary>What to do when several journal entries match (Q1 a).</summary>
    public const string CandidatesUserAction =
        "Ask the user which one they mean, showing each candidate's deletion time and original parent (read it with get_hierarchy), then retry with that candidate's entry ID as entryId.";

    /// <summary>What to do when the server can't tell where the item goes.</summary>
    public const string NoTargetUserAction =
        "The server can't tell where this item came from: no journal entry matches it (deleted in OneNote, deleted with its section group, or older than the journal's retention period), or its original parent is gone. Ask the user where it should go, then retry with parentId: a section for a page; a notebook or section group in the section's own notebook for a section.";

    /// <summary>What to do about a page's target that can't take it.</summary>
    public const string PageTargetUserAction =
        "Pass as parentId the ID of a section that isn't in the recycle bin: a page is restored into a section. Read the hierarchy (get_hierarchy) for a current ID.";

    /// <summary>What to do about a section's target that can't take it.</summary>
    public const string SectionTargetUserAction =
        "Pass as parentId the ID of a notebook or section group that isn't in the recycle bin, in the section's own notebook: a section is restored into a notebook or section group. Read the hierarchy (get_hierarchy) for a current ID.";

    /// <summary>What to do about a section sent to another notebook with no original parent in its own (Q7).</summary>
    public const string OwnNotebookParentUserAction =
        "A section going to another notebook is first restored in its own notebook, and the server can't tell where it came from there (no journal entry matches it, or its original parent is gone). Ask the user where it should go in its own notebook and retry with that notebook or section group as parentId, then move it with move_hierarchy_object.";

    /// <summary>What to do about a protected section sent elsewhere (AD-8).</summary>
    public const string ProtectedElsewhereUserAction =
        "Leave parentId out: a password-protected section goes back only to the place it was deleted from. If the user wants it elsewhere afterwards, they move it in OneNote.";

    /// <summary>What to do about a protected section whose place can't be found (AD-8).</summary>
    public const string ProtectedOrphanUserAction =
        "Ask the user to restore it from the Recycle Bin in OneNote: a password-protected section goes back only to the place it was deleted from, and the server can't find that place (no journal entry matches it, or that notebook or section group is gone).";

    /// <summary>What to do about a page position the server refuses.</summary>
    public const string PagePositionUserAction =
        "Read the target section with get_hierarchy (scope children) and pass a position from 1 to one more than its page count, at a top-level page rather than a subpage; or leave position out to put the page last.";

    /// <summary>What to do about a section position the server refuses.</summary>
    public const string SectionPositionUserAction =
        "Read the target with get_hierarchy (scope children) and pass a position from 1 to one more than its count of sections; or leave position out to put the section last among them.";

    /// <summary>What to do about a position below 1, before the item's kind is known.</summary>
    public const string AnyPositionUserAction =
        "Pass a position from 1: among the target section's pages for a page, among the target's sections for a section; or leave position out to put the item last.";

    /// <summary>What to do about a sibling's name.</summary>
    public const string NameClashUserAction =
        "The target already holds a section of that name. Ask the user to rename that live section first (rename_hierarchy_object), or restore this one elsewhere with parentId.";

    /// <summary>What to do when the restore would put a section on a location that holds another section's read grant (AD-21).</summary>
    public const string GrantLocationUserAction =
        "A read grant is stored for a section at the place this restore would put the section, and the restore would hand it over. Ask the user to revoke that read grant first (list_grants shows it; the user runs the grant command), or restore it elsewhere with parentId.";

    /// <summary>What to do when the server can't tell where the section would land.</summary>
    public const string UnpredictableUserAction =
        "Ask the user to restore it from the Recycle Bin in OneNote: the server can't tell where this restore would put the section, so it can't check it against read grants.";

    /// <summary>What to do when the target changed between step 1 and the lock.</summary>
    public const string TargetChangedUserAction =
        "Where the item would go changed while this call ran. Re-read the hierarchy (get_hierarchy) and retry.";

    /// <summary>What to do after the warning <c>restoredByName</c> (Q6).</summary>
    public const string RestoredByNameUserAction =
        "The item was matched to its journal entry by name, not by ID. Tell the user where it went (meta.parentId), and check it is where they expected.";

    /// <summary>What to do after the warning <c>settled</c>.</summary>
    public const string SettledUserAction =
        "The restore touched a cloud notebook (OneDrive or SharePoint). Before relying on it, ask the user to check in OneNote that it synced (no sync error on the notebook).";

    private readonly HierarchyWriteServices services;
    private readonly HierarchyWalk walk;
    private readonly Func<IReadOnlyList<JournalEntry>> loadEntries;
    private readonly Func<TimeSpan, CancellationToken, Task> settle;
    private readonly string correlationId;
    private readonly string parentArgument;
    private readonly string entryArgument;
    private readonly int? position;
    private readonly DateTimeOffset callStart;

    private Guid? chosenEntry;
    private IReadOnlyList<JournalEntry>? loaded;
    private string? authorizedTarget;
    private State? state;
    private Plan? plan;
    private bool settled;

    /// <param name="loadEntries">Reads every journal entry, from metadata only (<see cref="SoftDeleteEntries.Load"/> of the folder <c>journal.path</c> names now).</param>
    /// <param name="settle">Waits the cloud settle out (<see cref="MoveContainerWrite.SettleTime"/>); a test passes its own.</param>
    public RestoreFromRecycleBinWrite(
        HierarchyWriteServices services,
        Func<IReadOnlyList<JournalEntry>> loadEntries,
        Func<TimeSpan, CancellationToken, Task> settle,
        string correlationId,
        string objectId,
        string? parentId,
        string? entryId,
        int? position)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(loadEntries);
        ArgumentNullException.ThrowIfNull(settle);
        this.services = services;
        this.loadEntries = loadEntries;
        this.settle = settle;
        walk = new HierarchyWalk(services.Gateway);
        this.correlationId = correlationId ?? string.Empty;
        ObjectId = objectId ?? string.Empty;
        parentArgument = parentId?.Trim() ?? string.Empty;
        entryArgument = entryId?.Trim() ?? string.Empty;
        this.position = position;
        callStart = services.ReadAccess.Now();
    }

    public override OperationClass Class => OperationClass.HierarchyWrite;

    /// <summary>The recycled item (its ID in the bin).</summary>
    public string ObjectId { get; }

    /// <summary>Where the item goes (<c>meta.parentId</c>); null until step 1 decides it.</summary>
    public string? TargetId => state?.Target.Id ?? authorizedTarget;

    /// <summary>The target's journal entry (<c>meta.journalEntryId</c>); null until step 6 stores it.</summary>
    public Guid? JournalEntryId { get; private set; }

    /// <summary>
    /// Q7: the notebook or section group in another notebook that the caller named for a plain section, which the use case
    /// then moves the restored section to with <see cref="MoveContainerWrite"/>; null when no move follows.
    /// </summary>
    public string? MoveTargetId => state?.MoveTargetId;

    /// <summary>A restored page's recycled ID is dropped from the search page-text cache (AD-7, AD-12).</summary>
    public override IReadOnlyCollection<string> WrittenPageIds =>
        state?.Item.Kind == SoftDeleteKind.Page && ObjectId.Length > 0 ? [ObjectId] : [];

    public override string? FailureNote => JournalEntryId is { } id
        ? string.Create(CultureInfo.InvariantCulture, $"The target's hierarchy as it was before this restore is in journal entry {id:D}.")
        : null;

    public override async Task AuthorizeAsync(CancellationToken ct)
    {
        // Step 1, before the lock and before any grant: the arguments (AD-7).
        if (ObjectId.Length == 0)
        {
            throw Invalid("No objectId was given, so nothing was restored.", ObjectUserAction);
        }

        if (entryArgument.Length > 0)
        {
            chosenEntry = Guid.TryParse(entryArgument, out var id)
                ? id
                : throw Invalid("The argument entryId isn't a GUID, so nothing was restored.", EntryIdUserAction);
        }

        if (position is < 1)
        {
            throw Invalid(string.Create(CultureInfo.InvariantCulture, $"position {position} is below 1, so nothing was restored."), AnyPositionUserAction);
        }

        // What the item is, its entry, where it goes, and every refusal, before any grant dialog (AD-7 step 1.2).
        var now = await ReadStateAsync(ct).ConfigureAwait(false);
        Check(now);
        authorizedTarget = now.Target.Id;

        // AD-6: the item's notebook, then the target's; the pipeline asks once per notebook and re-resolves both (3.12).
        await services.WriteAccess.CheckAsync(ObjectId, () => ObjectNotFound("isn't inside a notebook"), ct).ConfigureAwait(false);
        await services.WriteAccess.CheckContainerAsync(now.Target.Id, () => Invalid($"Object {now.Target.Id} isn't inside a notebook, so nothing was restored.", TargetUserAction(now.Item.Kind)), ct).ConfigureAwait(false);
    }

    public override async Task ReadAsync(CancellationToken ct)
    {
        var now = await ReadStateAsync(ct).ConfigureAwait(false);
        if (!string.Equals(now.Target.Id, authorizedTarget, StringComparison.Ordinal))
        {
            throw new OneNoteException(
                ErrorCode.WriteNotGranted,
                $"Where {ObjectId} would be restored changed while this call ran (now {now.Target.Id}, checked {authorizedTarget}), so nothing was restored.",
                userAction: TargetChangedUserAction,
                retryable: true);
        }

        state = now;
    }

    public override void Validate() => plan = Check(state ?? throw new InvalidOperationException("Step 3 hasn't run."));

    public override async Task SnapshotAsync(CancellationToken ct)
    {
        // AD-8: one hierarchy entry of the target parent; the item stays in the bin if the call fails.
        var now = Planned.State;
        JournalEntryId = (await services.Snapshot.TakeAsync(
            new HierarchySnapshotRequest(correlationId, OperationName, OperationClass.HierarchyWrite, now.Target.Id, now.TargetXml),
            ct).ConfigureAwait(false)).EntryId;
    }

    public override async Task<MoveResult> CallAsync(CancellationToken ct)
    {
        var now = Planned;
        try
        {
            await services.Gateway.UpdateHierarchyAsync(now.Xml, ct).ConfigureAwait(false);
        }
        catch (OneNoteException ex) when (ex.ComHResult == MovePageWrite.PartialCopyHResult)
        {
            // As a move (AD-7 Moves): a partial copy is possible, so the outcome is unknown; the code stays (AD-9).
            await SettleQuietlyAsync(now, ct).ConfigureAwait(false);
            var found = await PartialCopyFindingsAsync(now, ct).ConfigureAwait(false);
            throw new OneNoteException(ex.Code, WithNote($"{ex.Message} {found}"), ex.ComHResult, ex, outcomeUnknown: true, ex.UserAction, retryable: false);
        }
        catch (OneNoteException ex) when (FailureNote is not null)
        {
            // The pipeline still decides outcomeUnknown (AD-9); the message names the journal entry (2.11 Q1).
            await SettleQuietlyAsync(now, ct).ConfigureAwait(false);
            throw new OneNoteException(ex.Code, WithNote(ex.Message), ex.ComHResult, ex, ex.OutcomeUnknown, ex.UserAction, ex.Retryable);
        }

        var warnings = new List<ToolWarning>();
        if (now.State.ByName && now.State.Entry is { } entry)
        {
            warnings.Add(new ToolWarning(
                ToolWarnings.RestoredByName,
                string.Create(CultureInfo.InvariantCulture, $"{Capitalized(now.State.Item.Kind)} {ObjectId} was matched to journal entry {entry.EntryId:D} by its name, and restored under {now.State.Target.Id}."),
                RestoredByNameUserAction));
        }

        return new MoveResult(now.State.Item.Kind == SoftDeleteKind.Section ? ObjectId : null, warnings);
    }

    /// <summary>Between 7 and 8: a cloud section's settle, then the target re-read and the item found (AD-7 Moves read-back).</summary>
    public override async Task<MoveResult> AfterCallAsync(MoveResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        var now = Planned;
        var done = new List<ToolWarning>(result.Warnings);
        if (now.TouchesCloud)
        {
            // Set first: a cancel or the deadline cutting the settle short still reports it (3.12 review).
            settled = true;
            done.Add(SettledWarning());
            await settle(MoveContainerWrite.SettleTime, ct).ConfigureAwait(false);
        }

        var target = now.State.Target;
        var after = await ReadChildrenAsync(target.Id, ct).ConfigureAwait(false);
        if (now.State.Item.Kind == SoftDeleteKind.Page)
        {
            var newId = MoveReadBack.NewPageId(target.Children, after, now.State.Item.Name, now.Index);
            if (newId is null)
            {
                done.Add(Differs($"Section {target.Id} read back with no new page in it, so the restore of page {ObjectId} wasn't confirmed."));
            }
            else if (after.Children.ToList().FindIndex(c => string.Equals(c.Id, newId, StringComparison.Ordinal)) is var at && at != now.Index)
            {
                done.Add(Differs(string.Create(CultureInfo.InvariantCulture, $"Page {ObjectId} came back as {newId} at position {at + 1} in section {target.Id}, not at position {now.Index + 1}.")));
            }

            var binPages = await services.Gateway.GetHierarchyAsync(now.State.Item.BinId, HierarchyScope.Pages, ct).ConfigureAwait(false);
            var stillInBin = XmlReads.Readable(() => RecycleBin.DeletedPages(binPages), "GetHierarchy").Any(p => string.Equals(p.Id, ObjectId, StringComparison.Ordinal));
            if (stillInBin)
            {
                done.Add(Differs($"Page {ObjectId} is still listed in Deleted Pages."));
            }

            return new MoveResult(newId ?? (stillInBin ? ObjectId : null), done);
        }

        var sections = after.Children.Where(c => c.Kind == HierarchyKind.Section).ToList();
        var index = sections.FindIndex(c => string.Equals(c.Id, ObjectId, StringComparison.Ordinal));
        if (index < 0)
        {
            done.Add(Differs($"Section {ObjectId} didn't read back in {HierarchyRules.Noun(target.Kind)} {target.Id}, so the restore wasn't confirmed."));
        }
        else if (index != now.Index)
        {
            done.Add(Differs(string.Create(CultureInfo.InvariantCulture, $"Section {ObjectId} came back at position {index + 1} among the sections of {target.Id}, not at position {now.Index + 1}.")));
        }

        var selfXml = await services.Gateway.GetHierarchyAsync(ObjectId, HierarchyScope.Self, ct).ConfigureAwait(false);
        if (XmlReads.Readable(() => HierarchyTarget.Read(selfXml), "GetHierarchy").InRecycleBin)
        {
            done.Add(Differs($"Section {ObjectId} still reads back as in the recycle bin."));
        }

        return new MoveResult(ObjectId, done);
    }

    /// <summary>The settle or the read-back threw (its deadline or a cancel included): the restore stands, with <c>readBackFailed</c>.</summary>
    public override MoveResult AfterCallFailed(MoveResult result, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(exception);
        var added = new List<ToolWarning>();
        if (settled)
        {
            added.Add(SettledWarning());
        }

        added.Add(new ToolWarning(
            ToolWarnings.ReadBackFailed,
            $"The restore of {ObjectId} into {TargetId} succeeded, but it couldn't be re-read afterwards ({MoveReadBack.Why(exception)}), so where it is now wasn't confirmed.",
            ReadBackUserAction()));
        return result with { Warnings = [.. result.Warnings, .. added] };
    }

    private Plan Planned => plan ?? throw new InvalidOperationException("Step 4 hasn't run.");

    /// <summary>Everything step 4 checks, read live: the item, its notebook, its journal entry, and the target with its notebook.</summary>
    private async Task<State> ReadStateAsync(CancellationToken ct)
    {
        var item = await ItemAsync(ct).ConfigureAwait(false);
        var notebook = await walk.OwningNotebookAsync(ObjectId, ct).ConfigureAwait(false)
            ?? throw ObjectNotFound("isn't inside a notebook");
        var notebookXml = await services.Gateway.GetHierarchyAsync(notebook.Id, HierarchyScope.Sections, ct).ConfigureAwait(false);
        var liveSections = XmlReads.Readable(() => NotebookSections.Find(notebookXml), "GetHierarchy").Where(s => !s.InRecycleBin).ToList();
        var liveGroups = XmlReads.Readable(() => NotebookSections.FindGroups(notebookXml), "GetHierarchy");

        var mine = SoftDeleteEntries.Of(loaded ??= loadEntries(), GrantKeys.NotebookKey(notebook.Path));
        var (entry, byName) = Match(item, mine);
        var targetId = TargetOf(item, entry, notebook, liveSections, liveGroups);

        var targetXml = await services.Gateway.GetHierarchyAsync(targetId, HierarchyScope.Children, ct).ConfigureAwait(false);
        var target = XmlReads.Readable(() => HierarchyTarget.Read(targetXml), "GetHierarchy");
        var targetNotebook = await walk.OwningNotebookAsync(targetId, ct).ConfigureAwait(false);
        var targetNotebookPath = targetNotebook?.Path ?? (target.Kind == HierarchyKind.Notebook ? target.Path : null);
        string? moveTarget = null;
        if (item.Kind == SoftDeleteKind.Section && !SameNotebook(targetNotebookPath, notebook.Path))
        {
            // Q7 (Phill, 2026-11-17): restored in place in its own notebook first; the use case then moves it there.
            CheckSectionTarget(target);
            moveTarget = target.Id;
            var original = entry is null ? null : SectionParent(entry, notebook, liveGroups);
            if (original is null)
            {
                throw Invalid(
                    $"Section {ObjectId} has no original parent in its own notebook (no journal entry matches it, or that parent is gone), so it can't be restored there and then moved to {moveTarget}, and nothing was restored.",
                    OwnNotebookParentUserAction);
            }

            targetXml = await services.Gateway.GetHierarchyAsync(original, HierarchyScope.Children, ct).ConfigureAwait(false);
            target = XmlReads.Readable(() => HierarchyTarget.Read(targetXml), "GetHierarchy");
            targetNotebookPath = notebook.Path;
        }

        return new State(item, notebook.Path, entry, byName, target, targetXml, targetNotebookPath, moveTarget);
    }

    /// <summary>
    /// What the object is: a page in Deleted Pages, or a section directly in the recycle bin. Everything else is refused
    /// with its own userAction. The walk comes first, never a <c>self</c> read, as for a move (3.3 S1); an ID OneNote
    /// doesn't know fails there with <see cref="ErrorCode.ObjectNotFound"/> (AD-9).
    /// </summary>
    private async Task<Item> ItemAsync(CancellationToken ct)
    {
        var section = await walk.OwningSectionAsync(ObjectId, ct).ConfigureAwait(false);
        if (section is not null)
        {
            var sectionXml = await services.Gateway.GetHierarchyAsync(section.Id, HierarchyScope.Self, ct).ConfigureAwait(false);
            var home = XmlReads.Readable(() => HierarchyTarget.Read(sectionXml), "GetHierarchy");
            if (!home.IsDeletedPages)
            {
                throw home.InRecycleBin
                    ? Invalid($"Page {ObjectId} is inside section {home.Id}, which is itself in the recycle bin, so nothing was restored.", RecycledSectionPageUserAction)
                    : Invalid($"Page {ObjectId} isn't in the recycle bin, so nothing was restored.", LiveUserAction);
            }

            var binId = await services.Gateway.GetHierarchyParentAsync(home.Id, ct).ConfigureAwait(false);
            var binPages = await services.Gateway.GetHierarchyAsync(binId, HierarchyScope.Pages, ct).ConfigureAwait(false);
            var deleted = XmlReads.Readable(() => RecycleBin.DeletedPages(binPages), "GetHierarchy");
            var page = deleted.FirstOrDefault(p => string.Equals(p.Id, ObjectId, StringComparison.Ordinal))
                ?? throw ObjectNotFound("isn't listed in Deleted Pages");
            return new Item(SoftDeleteKind.Page, ObjectId, page.Name, string.Empty, false, binId, [.. deleted.Select(p => p.Id)]);
        }

        var selfXml = await services.Gateway.GetHierarchyAsync(ObjectId, HierarchyScope.Self, ct).ConfigureAwait(false);
        var self = XmlReads.Readable(() => HierarchyTarget.Read(selfXml), "GetHierarchy");
        if (self.IsRecycleBin)
        {
            throw Invalid($"Object {ObjectId} is a notebook's recycle bin itself, so nothing was restored.", BinItselfUserAction);
        }

        if (self.IsDeletedPages)
        {
            throw Invalid($"Object {ObjectId} is a notebook's Deleted Pages section itself, so nothing was restored.", BinItselfUserAction);
        }

        if (!self.InRecycleBin)
        {
            throw Invalid($"{Capitalized(self.Kind)} {ObjectId} isn't in the recycle bin, so nothing was restored.", LiveUserAction);
        }

        if (self.Kind == HierarchyKind.SectionGroup)
        {
            throw Invalid($"Object {ObjectId} is a section group, and a section group isn't restored whole, so nothing was restored.", GroupUserAction);
        }

        if (self.Kind != HierarchyKind.Section)
        {
            throw Invalid($"Object {ObjectId} is a {HierarchyRules.Noun(self.Kind)}, not a page or section, so nothing was restored.", ObjectUserAction);
        }

        var parentId = await services.Gateway.GetHierarchyParentAsync(ObjectId, ct).ConfigureAwait(false);
        var parentXml = string.IsNullOrEmpty(parentId) ? null : await services.Gateway.GetHierarchyAsync(parentId, HierarchyScope.Children, ct).ConfigureAwait(false);
        if (parentXml is null || !XmlReads.Readable(() => HierarchyTarget.Read(parentXml), "GetHierarchy").IsRecycleBin)
        {
            throw Invalid($"Section {ObjectId} isn't directly in its notebook's recycle bin, so nothing was restored.", ObjectUserAction);
        }

        var binned = XmlReads.Readable(() => HierarchyNode.Sections(parentXml), "GetHierarchy").Select(s => s.Id).ToList();
        return new Item(SoftDeleteKind.Section, ObjectId, self.Name, self.Path, self.Encrypted, parentId!, binned);
    }

    /// <summary>The item's soft-delete entry (AD-8; Q1, Q5, Q10): <c>entryId</c> when given, else the match; several are refused, listed.</summary>
    private (JournalEntry? Entry, bool ByName) Match(Item item, IReadOnlyList<JournalEntry> mine)
    {
        if (chosenEntry is { } id)
        {
            // Review fix 1: only an entry the item itself matches (by ID or by name), so no other item's place or key is borrowed.
            var allowed = item.Kind == SoftDeleteKind.Page
                ? SoftDeleteEntries.PageMatches(mine, ObjectId, item.Name, item.Binned)
                : SoftDeleteEntries.SectionMatches(mine, ObjectId, item.Name, item.Path, item.Encrypted, item.Binned);
            var chosen = allowed.FirstOrDefault(e => e.EntryId == id)
                ?? throw Invalid(
                    string.Create(CultureInfo.InvariantCulture, $"Journal entry {id:D} isn't a soft-delete entry that matches {Noun(item.Kind)} {ObjectId} in its notebook, so nothing was restored."),
                    EntryIdUserAction);
            return (chosen, false);
        }

        var match = item.Kind == SoftDeleteKind.Page
            ? SoftDeleteEntries.MatchPage(mine, ObjectId, item.Name, item.Binned)
            : SoftDeleteEntries.MatchSection(mine, ObjectId, item.Name, item.Path, item.Encrypted, item.Binned);
        if (match.Candidates.Count > 0)
        {
            throw Invalid(
                string.Create(CultureInfo.InvariantCulture, $"{match.Candidates.Count} journal entries match {Noun(item.Kind)} {ObjectId}, so nothing was restored. Candidates: {SoftDeleteEntries.Describe(match.Candidates)}."),
                CandidatesUserAction);
        }

        return (match.Entry, match.ByName);
    }

    /// <summary>Where the item goes (Q4 a): a protected section's own place only; else the caller's <c>parentId</c>; else the entry's original parent.</summary>
    private string TargetOf(Item item, JournalEntry? entry, HierarchyNode notebook, IReadOnlyList<NotebookSection> liveSections, IReadOnlyList<NotebookSectionGroup> liveGroups)
    {
        if (item.Kind == SoftDeleteKind.Section && item.Encrypted)
        {
            // AD-8: the parent whose path holds the entry's SectionKey; no parentNamePath fallback, no override.
            var folder = entry?.SectionKey is { } key ? SoftDeleteEntries.FolderOf(key) : null;
            var own = folder is null ? null
                : GrantKeys.SameLocation(notebook.Path, folder) ? notebook.Id
                : liveGroups.FirstOrDefault(g => GrantKeys.SameLocation(g.Path, folder))?.Id;
            // Review fix 5: the entry's parentId is only a shortcut that must agree with the SectionKey place.
            var shortcut = entry?.ParentId is { Length: > 0 } parent
                ? string.Equals(parent, notebook.Id, StringComparison.Ordinal) ? notebook.Path : liveGroups.FirstOrDefault(g => string.Equals(g.Id, parent, StringComparison.Ordinal))?.Path
                : null;
            if (own is null || (shortcut is not null && !GrantKeys.SameLocation(shortcut, folder)))
            {
                throw new OneNoteException(
                    ErrorCode.ObjectNotFound,
                    entry is null
                        ? $"Password-protected section {ObjectId} has no journal entry, so the place it was deleted from is unknown, and nothing was restored."
                        : own is null
                            ? $"The place password-protected section {ObjectId} was deleted from (journal entry {entry.EntryId:D}) is gone, so nothing was restored."
                            : $"Journal entry {entry.EntryId:D} of password-protected section {ObjectId} names a parent ({entry.ParentId}) that isn't the place its SectionKey records, so where it was deleted from is uncertain, and nothing was restored.",
                    userAction: ProtectedOrphanUserAction,
                    retryable: false);
            }

            if (parentArgument.Length > 0 && !string.Equals(parentArgument, own, StringComparison.Ordinal))
            {
                throw Invalid($"Section {ObjectId} is password-protected and goes back only to {own}, the place it was deleted from, not {parentArgument}, so nothing was restored.", ProtectedElsewhereUserAction);
            }

            return own;
        }

        if (parentArgument.Length > 0)
        {
            return parentArgument;
        }

        var original = entry is null ? null : item.Kind == SoftDeleteKind.Page
            ? PageParent(entry, liveSections)
            : SectionParent(entry, notebook, liveGroups);
        return original is null
            ? throw Invalid(
                entry is null
                    ? $"No journal entry matches {Noun(item.Kind)} {ObjectId}, and no parentId was given, so nothing was restored."
                    : string.Create(CultureInfo.InvariantCulture, $"The original parent of {Noun(item.Kind)} {ObjectId} (journal entry {entry.EntryId:D}) is gone, and no parentId was given, so nothing was restored."),
                NoTargetUserAction)
            : original;
    }

    /// <summary>A page's original section: the entry's <c>parentId</c> if it is live, else the one live section at its <c>parentNamePath</c>.</summary>
    private static string? PageParent(JournalEntry entry, IReadOnlyList<NotebookSection> live)
    {
        if (entry.ParentId.Length > 0 && live.Any(s => string.Equals(s.Id, entry.ParentId, StringComparison.Ordinal)))
        {
            return entry.ParentId;
        }

        var byPath = JournalTargets.SectionsAt(live, entry.ParentNamePath);
        return byPath.Count == 1 ? byPath[0].Id : null;
    }

    /// <summary>
    /// A section's original parent: the entry's <c>parentId</c> if it is the notebook or a live group, else the one live
    /// group at its <c>parentNamePath</c>, where an empty path is the notebook itself.
    /// </summary>
    private static string? SectionParent(JournalEntry entry, HierarchyNode notebook, IReadOnlyList<NotebookSectionGroup> live)
    {
        if (entry.ParentId.Length > 0
            && (string.Equals(entry.ParentId, notebook.Id, StringComparison.Ordinal) || live.Any(g => string.Equals(g.Id, entry.ParentId, StringComparison.Ordinal))))
        {
            return entry.ParentId;
        }

        if (entry.ParentNamePath.Length == 0)
        {
            return notebook.Id;
        }

        var byPath = JournalTargets.GroupsAt(live, entry.ParentNamePath);
        return byPath.Count == 1 ? byPath[0].Id : null;
    }

    /// <summary>Step 4's refusals, in order; returns what the later steps use. Step 1 runs it too.</summary>
    private Plan Check(State now)
    {
        var target = now.Target;
        var kind = now.Item.Kind;
        if (HierarchyRules.RecycleBinProblem(target.IsRecycleBin, target.InRecycleBin) is { } binned)
        {
            throw Invalid($"Object {target.Id} {binned}, so nothing was restored.", TargetUserAction(kind));
        }

        int at;
        string xml;
        if (kind == SoftDeleteKind.Page)
        {
            if (target.Kind != HierarchyKind.Section)
            {
                throw Invalid($"Object {target.Id} is a {HierarchyRules.Noun(target.Kind)}, not a section, and a page is restored into a section, so nothing was restored.", PageTargetUserAction);
            }

            if (target.Locked)
            {
                throw new OneNoteException(ErrorCode.SectionLocked, $"Section {target.Id} is locked, so page {ObjectId} wasn't restored.");
            }

            // As a move (3.4 security review; AD-7 step 1.2): a position in a protected section needs its read grant, held at the call's start.
            if (position is not null && target.Encrypted && !services.ReadAccess.HoldsReadGrantAt(GrantKeys.SectionKey(target.Path), callStart))
            {
                throw new OneNoteException(
                    ErrorCode.ReadNotGranted,
                    $"Section {target.Id} is password-protected, and placing a page at a position in it needs a read grant for it held when the call starts, so nothing was restored.",
                    userAction: MovePageWrite.ProtectedPositionUserAction);
            }

            // As a move (3.4 Q2): 1-based among the target's live pages, before a top-level page; omitted, last. No page count in a message.
            var count = target.Children.Count;
            at = position ?? count + 1;
            if (at > count + 1)
            {
                throw Invalid(string.Create(CultureInfo.InvariantCulture, $"position {at} is past the end of section {target.Id}, so nothing was restored."), PagePositionUserAction);
            }

            if (at <= count && target.Children[at - 1].PageLevel > 1)
            {
                throw Invalid(string.Create(CultureInfo.InvariantCulture, $"position {at} in section {target.Id} is inside a subpage block (the page there is a subpage), so nothing was restored."), PagePositionUserAction);
            }

            xml = HierarchyUpdate.MovePage(target.Id, target.Children, ObjectId, at - 1);
        }
        else
        {
            CheckSectionTarget(target);
            if (!SameNotebook(now.TargetNotebookPath, now.NotebookPath))
            {
                // Q7 restores in place first (ReadStateAsync), so a target in another notebook never reaches here.
                throw new OneNoteException(ErrorCode.Internal, $"The restore of section {ObjectId} was aimed at {target.Id} outside its own notebook, so nothing was sent.");
            }

            var siblings = target.Children.Where(c => c.Kind == HierarchyKind.Section).ToList();
            if (siblings.Any(c => string.Equals(c.Name, now.Item.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw Invalid($"{Capitalized(target.Kind)} {target.Id} already holds a section with the name of {ObjectId}, so nothing was restored.", NameClashUserAction);
            }

            // Q7: a caller's position belongs to the move; the restore in place puts the section last.
            at = (now.MoveTargetId is null ? position : null) ?? siblings.Count + 1;
            if (at > siblings.Count + 1)
            {
                throw Invalid(
                    string.Create(CultureInfo.InvariantCulture, $"position {at} is past the end of the {siblings.Count} section(s) in {HierarchyRules.Noun(target.Kind)} {target.Id}, so nothing was restored."),
                    SectionPositionUserAction);
            }

            if (GrantLocationRefusal(now) is { } refusal)
            {
                throw refusal;
            }

            xml = HierarchyUpdate.MoveContainer(target.Kind, target.Id, siblings, HierarchyKind.Section, ObjectId, at - 1);
        }

        // AD-16: the hard gate for UpdateHierarchy. The XML is ours, so a failure is our bug, never sent.
        var problems = SchemaValidation.Problems(xml);
        if (problems.Count > 0)
        {
            throw new OneNoteException(
                ErrorCode.Internal,
                string.Create(CultureInfo.InvariantCulture, $"The server built UpdateHierarchy XML that the 2013 schema refuses ({problems.Count} problem(s), the first at {problems[0].DisplayName ?? "the document"}), so nothing was sent."));
        }

        var cloud = kind == SoftDeleteKind.Section && CloudNotebookCreate.IsUrl(now.NotebookPath);
        return new Plan(now, xml, at - 1, cloud);
    }

    /// <summary>A section's target: a live notebook or section group (the recycle bin was refused before).</summary>
    private static void CheckSectionTarget(HierarchyTarget target)
    {
        if (HierarchyRules.RecycleBinProblem(target.IsRecycleBin, target.InRecycleBin) is { } binned)
        {
            throw Invalid($"Object {target.Id} {binned}, so nothing was restored.", SectionTargetUserAction);
        }

        if (target.Kind is not (HierarchyKind.Notebook or HierarchyKind.SectionGroup))
        {
            throw Invalid($"Object {target.Id} is a {HierarchyRules.Noun(target.Kind)}, not a notebook or section group, and a section is restored into a notebook or section group, so nothing was restored.", SectionTargetUserAction);
        }
    }

    private static bool SameNotebook(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && GrantKeys.Comparer.Equals(GrantKeys.NotebookKey(a), GrantKeys.NotebookKey(b));

    /// <summary>
    /// As a move (AD-7 Moves, AD-21): a section landing on a location whose key holds a stored read grant or a live
    /// <c>once</c> window is refused, except its own former key, where it was before the delete (AD-8). A path the server
    /// can't predict is refused too.
    /// </summary>
    private OneNoteException? GrantLocationRefusal(State now)
    {
        if (MovedLocations.Section(now.Item.Path, now.Target.Path) is not { } after)
        {
            return new OneNoteException(
                ErrorCode.NotSupported,
                $"The server can't tell where restoring section {ObjectId} would put it, so it can't check the restore against read grants, and nothing was restored.",
                userAction: UnpredictableUserAction,
                retryable: false);
        }

        var landing = GrantKeys.SectionKey(after);
        var former = now.Entry?.SectionKey ?? (now.Entry is { } entry && SoftDeleteEntries.OldPath(entry) is { } old ? GrantKeys.SectionKey(old) : null);
        if (former is not null && GrantKeys.SameLocation(former, landing))
        {
            return null;
        }

        return services.ReadAccess.HoldsReadGrantAtLocation(landing, services.ReadAccess.Now())
            ? Invalid($"Restoring section {ObjectId} would put it at a location that holds another section's read grant, so nothing was restored.", GrantLocationUserAction)
            : null;
    }

    /// <summary>The settle before a failed cloud restore's re-read (as a move, 3.12 Q4); a cancel or the deadline cuts it short silently.</summary>
    private async Task SettleQuietlyAsync(Plan now, CancellationToken ct)
    {
        if (!now.TouchesCloud)
        {
            return;
        }

        try
        {
            await settle(MoveContainerWrite.SettleTime, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The original failure is what the caller gets; the settle only spaces the next write.
        }
    }

    /// <summary>After <c>0x80042004</c>: what a best-effort re-read of the target finds (as a move, AD-7 Moves).</summary>
    private async Task<string> PartialCopyFindingsAsync(Plan now, CancellationToken ct)
    {
        var target = now.State.Target;
        string found;
        try
        {
            var after = await ReadChildrenAsync(target.Id, ct).ConfigureAwait(false);
            if (now.State.Item.Kind == SoftDeleteKind.Section)
            {
                found = after.Children.Any(c => string.Equals(c.Id, ObjectId, StringComparison.Ordinal))
                    ? $"{ObjectId} is in {target.Id}"
                    : $"{ObjectId} isn't in {target.Id}";
            }
            else
            {
                var before = target.Children.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                var copies = after.Children
                    .Where(c => !before.Contains(c.Id) && string.Equals(c.Name, now.State.Item.Name, StringComparison.Ordinal))
                    .Select(c => c.Id)
                    .ToList();
                found = copies.Count == 0
                    ? $"section {target.Id} has no new page with its name"
                    : target.Encrypted
                        ? string.Create(CultureInfo.InvariantCulture, $"section {target.Id} has {copies.Count} new page(s) with its name")
                        : string.Create(CultureInfo.InvariantCulture, $"section {target.Id} has {copies.Count} new page(s) with its name ({string.Join(", ", copies)})");
            }
        }
#pragma warning disable CA1031 // Best effort: the original failure is what the caller gets; the re-read only adds to its message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            found = $"{target.Id} couldn't be re-read ({MoveReadBack.Why(ex)})";
        }

        return $"The restore may have left a partial copy. Re-read straight after the failure: {found}; a copy in a cloud notebook can land later.";
    }

    private async Task<HierarchyTarget> ReadChildrenAsync(string id, CancellationToken ct)
    {
        var xml = await services.Gateway.GetHierarchyAsync(id, HierarchyScope.Children, ct).ConfigureAwait(false);
        return XmlReads.Readable(() => HierarchyTarget.Read(xml), "GetHierarchy");
    }

    private ToolWarning SettledWarning() => new(
        ToolWarnings.Settled,
        string.Create(CultureInfo.InvariantCulture, $"The restore of {ObjectId} touched a cloud notebook, so the server waited {MoveContainerWrite.SettleTime.TotalSeconds:0} s before re-reading it, as OneNote can fail or block on a write straight after such a move."),
        SettledUserAction);

    private ToolWarning Differs(string message) => new(ToolWarnings.ReadBackDiffers, message, ReadBackUserAction());

    private string ReadBackUserAction() =>
        $"Read {TargetId} with get_hierarchy (scope children) to find where {ObjectId} is now, and its current ID.";

    private string WithNote(string message) => FailureNote is { } note ? $"{message} {note}" : message;

    private OneNoteException ObjectNotFound(string why) =>
        new(ErrorCode.ObjectNotFound, $"Object {ObjectId} {why}, so nothing was restored.", userAction: ObjectUserAction);

    private static OneNoteException Invalid(string message, string userAction) =>
        new(ErrorCode.InvalidArgument, message, userAction: userAction, retryable: false);

    private static string TargetUserAction(SoftDeleteKind kind) =>
        kind == SoftDeleteKind.Page ? PageTargetUserAction : SectionTargetUserAction;

    private static string Noun(SoftDeleteKind kind) => kind == SoftDeleteKind.Page ? "page" : "section";

    private static string Capitalized(SoftDeleteKind kind) => kind == SoftDeleteKind.Page ? "Page" : "Section";

    private static string Capitalized(HierarchyKind kind) => HierarchyBasis.Capitalized(HierarchyRules.Noun(kind));

    /// <summary>The recycled item: its kind, ID, name, path (a section's, in the bin), protection, the bin node it is under (the recycle bin), and the IDs of every item of its kind in the bin.</summary>
    private sealed record Item(SoftDeleteKind Kind, string Id, string Name, string Path, bool Encrypted, string BinId, IReadOnlyList<string> Binned);

    /// <summary>What step 3 read: the item, its notebook's path, its entry and whether it matched by name, and the target with its XML and notebook path.</summary>
    private sealed record State(
        Item Item,
        string NotebookPath,
        JournalEntry? Entry,
        bool ByName,
        HierarchyTarget Target,
        string TargetXml,
        string? TargetNotebookPath,
        string? MoveTargetId);

    /// <summary>What step 4 decided: the state it checked, the XML to send, the 0-based index sent, and whether a cloud section settles.</summary>
    private sealed record Plan(State State, string Xml, int Index, bool TouchesCloud);
}
