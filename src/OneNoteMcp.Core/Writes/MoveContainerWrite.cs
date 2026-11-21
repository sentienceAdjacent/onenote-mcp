using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// <c>move_hierarchy_object</c> for a section or section group (story 3.12; AD-5, AD-7 Moves, AD-21): one
/// <c>HierarchyWrite</c> that sends one <c>UpdateHierarchy</c> of the new parent's XML (a notebook or section group) with
/// its same-kind children and the moved node at the position sent (spike 3.1's <c>m3</c>/<c>m4</c> shape). The node keeps
/// its ID.
/// <list type="number">
/// <item>Step 1, before the lock: an empty <c>objectId</c> or <c>parentId</c>, a <c>position</c> below 1 and a basis with a
/// hash are <see cref="ErrorCode.InvalidArgument"/>. Then the write grant of the node's notebook and of the new parent's
/// (AD-6; the pipeline asks once per notebook), then step 4's refusals against a read made now.</item>
/// <item value="3">Under the lock, both notebooks are re-resolved (AD-7): a <c>NotebookKey</c> other than step 1's is
/// <see cref="ErrorCode.WriteNotGranted"/>, retryable, before COM. Then the node at <c>self</c>, its parent and the new
/// parent at <c>children</c>, and a group at <c>sections</c> scope.</item>
/// <item>Refusals, in this order: a protected section, or a group holding one (<see cref="ErrorCode.NotSupported"/>, AD-21,
/// checked first); the node or the new parent in the recycle bin (<see cref="ErrorCode.ObjectNotFound"/>); a new parent
/// that isn't a notebook or section group, is the node or is inside it, or is the node's own parent
/// (<see cref="ErrorCode.InvalidArgument"/>); a node leaving a cloud notebook for another notebook's group (J1,
/// <see cref="ErrorCode.NotSupported"/>); a same-kind sibling's name, a position past the end, and a section landing on a
/// key that holds a read grant (<see cref="ErrorCode.InvalidArgument"/>; an unpredictable path is
/// <see cref="ErrorCode.NotSupported"/>). The XML must pass the AD-16 gate. No <see cref="ErrorCode.SectionLocked"/>: the
/// new parent is never a section, and a locked section is protected.</item>
/// <item>The basis is the node's own hierarchy <c>lastModifiedTime</c> (AD-17), through <see cref="HierarchyBasis"/>.</item>
/// <item>One <c>hierarchy</c> journal entry per parent, old then new (AD-8).</item>
/// <item>One <c>UpdateHierarchy</c>. <c>0x80042004</c> keeps <see cref="ErrorCode.ObjectNotFound"/> and sets
/// <c>outcomeUnknown</c>, after a best-effort re-read of both parents; <c>0x80042018</c> on a group move is
/// <see cref="ErrorCode.NotSupported"/> (J3).</item>
/// </list>
/// Between 7 and 8, a move touching a cloud notebook settles 10 s under the lock (J5; the warning <c>settled</c>), and then
/// both parents are re-read and the node found by its ID, and the moved sections' real paths are checked against stored read
/// grants (warning <c>readGrantAtLocation</c>; 3.12 security review). A failed call on such a move settles before its re-read too
/// (3.12 Q4). Messages carry IDs and counts only, never a name (AD-9).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MoveContainerWrite : WriteOperation<MoveResult>
{
    /// <summary>hrFolderDoesNotExist: a group moved to a cloud notebook's root (spike 3.1 Implication 5; J3).</summary>
    public const int FolderMissingHResult = unchecked((int)0x80042018);

    /// <summary>What to do after <c>noBasis</c> or <c>partialBasis</c>.</summary>
    public const string NoBasisUserAction =
        "Pass the section's or section group's lastModifiedTime attribute from a get_hierarchy read as basis: {lastModifiedTime}, so a change made since your read is caught.";

    /// <summary>What to do after <see cref="ErrorCode.StaleWrite"/>.</summary>
    public const string StaleUserAction =
        "Re-read the section or section group with get_hierarchy, check the move still makes sense, and retry with its current lastModifiedTime. Straight after another hierarchy write a date can move about a second late, so if nothing else changed, re-read and retry once.";

    /// <summary>What to do about an object ID that isn't a live section or section group.</summary>
    public const string ObjectUserAction =
        "Pass as objectId the ID of a page, section or section group that isn't in the recycle bin. Read the hierarchy (get_hierarchy) for a current ID.";

    /// <summary>What to do about a new parent that can't take a section or section group.</summary>
    public const string TargetUserAction =
        "Pass as parentId the ID of a notebook or section group that isn't in the recycle bin and isn't the moved section group or inside it: a section or section group moves into a notebook or section group. Read the hierarchy (get_hierarchy) for a current ID.";

    /// <summary>What to do about a protected section, or a group holding one (AD-21; epic 3 Q9, Q14).</summary>
    public const string ProtectedUserAction =
        "Ask the user to move it in OneNote: a password-protected section, or a section group holding one, isn't moved through this server, because the move could take its content out of the section's encryption.";

    /// <summary>What to do when the new parent is the node's own parent.</summary>
    public const string SameParentUserAction =
        "It is already there. To change its place among its siblings, use reorder_hierarchy_children on its parent.";

    /// <summary>What to do about a section or group leaving a cloud notebook for another notebook's group (J1).</summary>
    public const string CloudIntoGroupUserAction =
        "Move it to the other notebook's root first (pass that notebook's ID as parentId), then into the section group: OneNote fails a move out of a cloud notebook straight into another notebook's section group.";

    /// <summary>What to do about a sibling's name.</summary>
    public const string NameClashUserAction =
        "The new parent already holds one of that name. Ask the user to rename one of them first (rename_hierarchy_object), then move it.";

    /// <summary>What to do about a position the server refuses.</summary>
    public const string PositionUserAction =
        "Read the new parent with get_hierarchy (scope children) and pass a position from 1 to one more than its count of sections (to move a section) or section groups (to move a group); or leave position out to put it last.";

    /// <summary>What to do when the move would put a section on a location that holds a read grant (AD-21).</summary>
    public const string GrantLocationUserAction =
        "A read grant is stored for a section at the place this move would put a section, and the move would hand it over. Ask the user to revoke that read grant first (list_grants shows it; the user runs the grant command), or move it elsewhere.";

    /// <summary>What to do after the warning <c>readGrantAtLocation</c> (3.12 security review).</summary>
    public const string ReadGrantAtLocationUserAction =
        "The move went through, but a read grant stored for an earlier section at the moved section's new location now covers it, and would let its pages be read without asking if it is password-protected later. Tell the user, and ask them to revoke that read grant unless they mean it to cover this section (list_grants shows it; the user runs the grant command).";

    /// <summary>What to do when the server can't tell where the sections would land.</summary>
    public const string UnpredictableUserAction =
        "Ask the user to move it in OneNote: the server can't tell where this move would put its sections, so it can't check the move against read grants.";

    /// <summary>What to do after <c>0x80042018</c> on a group move (J3).</summary>
    public const string GroupFolderUserAction =
        "OneNote couldn't move this section group there. Move its sections one by one with move_hierarchy_object (into a section group made there with create_section_group), or ask the user to move the group in OneNote.";

    /// <summary>What to do when a notebook changed under the lock (AD-7).</summary>
    public const string NotebookChangedUserAction =
        "The object or the new parent moved to another notebook while this call ran. Re-read the hierarchy (get_hierarchy) and retry; the retry checks the write grant of the notebook each is in now.";

    private readonly HierarchyWriteServices services;
    private readonly HierarchyWalk walk;
    private readonly Func<TimeSpan, CancellationToken, Task> settle;
    private readonly string correlationId;
    private readonly int? position;
    private readonly PageWriteBasis? basis;
    private readonly List<ToolWarning> warnings = [];

    private WriteTarget? objectNotebook;
    private WriteTarget? targetNotebook;
    private State? state;
    private Plan? plan;
    private bool settled;

    /// <param name="settle">Waits the settle out (<see cref="CloudSettle.Time"/>); a test passes its own.</param>
    public MoveContainerWrite(
        HierarchyWriteServices services,
        string correlationId,
        string objectId,
        string parentId,
        int? position,
        PageWriteBasis? basis,
        Func<TimeSpan, CancellationToken, Task> settle)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settle);
        this.services = services;
        this.settle = settle;
        walk = new HierarchyWalk(services.Gateway);
        this.correlationId = correlationId ?? string.Empty;
        ObjectId = objectId ?? string.Empty;
        TargetId = parentId ?? string.Empty;
        this.position = position;
        this.basis = basis;
    }

    public override OperationClass Class => OperationClass.HierarchyWrite;

    /// <summary>Step 3 re-resolves both notebooks itself (AD-7).</summary>
    public override bool ReResolvesNotebooks => true;

    /// <summary>The section or section group moved; it keeps its ID.</summary>
    public string ObjectId { get; }

    /// <summary>The new parent (<c>meta.parentId</c>).</summary>
    public string TargetId { get; }

    /// <summary>The old parent's journal entry (<c>meta.journalEntryId</c>); null until step 6 stores it.</summary>
    public Guid? SourceEntryId { get; private set; }

    /// <summary>The new parent's journal entry; null until step 6 stores it.</summary>
    public Guid? TargetEntryId { get; private set; }

    /// <summary>The journal entries step 6 stored, old parent then new (<c>meta.journalEntryIds</c>, 3.4 Q1).</summary>
    public IReadOnlyList<Guid> JournalEntryIds => [.. new[] { SourceEntryId, TargetEntryId }.OfType<Guid>()];

    public override string? FailureNote => (SourceEntryId, TargetEntryId) switch
    {
        ({ } source, { } target) => string.Create(CultureInfo.InvariantCulture, $"Both parents' hierarchy as it was before this move is in journal entries {source:D} (old parent) and {target:D} (new parent)."),
        ({ } source, null) => string.Create(CultureInfo.InvariantCulture, $"The old parent's hierarchy as it was before this move is in journal entry {source:D}."),
        _ => null,
    };

    public override async Task AuthorizeAsync(CancellationToken ct)
    {
        // Step 1, before the lock and before any grant: the arguments (AD-7, AD-17).
        if (ObjectId.Length == 0)
        {
            throw HierarchyWriteSteps.Invalid("No objectId was given, so nothing was moved.", ObjectUserAction);
        }

        if (TargetId.Length == 0)
        {
            throw HierarchyWriteSteps.Invalid("No parentId was given, so nothing was moved.", TargetUserAction);
        }

        if (position is < 1)
        {
            throw HierarchyWriteSteps.Invalid(string.Create(CultureInfo.InvariantCulture, $"position {position} is below 1, so nothing was moved."), PositionUserAction);
        }

        HierarchyBasis.CheckArgument(basis, NoBasisUserAction);

        // AD-6: both notebooks' write grants, the moved node's first; the pipeline asks once per notebook (3.4 Q3).
        objectNotebook = await services.WriteAccess.CheckAsync(ObjectId, () => ObjectNotFound("isn't inside a notebook"), ct).ConfigureAwait(false);
        targetNotebook = await services.WriteAccess.CheckContainerAsync(TargetId, () => BadTarget("isn't inside a notebook"), ct).ConfigureAwait(false);

        // Step 4's refusals against a read made now, so the lock is never taken for one; step 4 checks again under it.
        Check(await ReadStateAsync(ct).ConfigureAwait(false));
    }

    public override async Task ReadAsync(CancellationToken ct)
    {
        // AD-7: both notebooks re-resolved under the lock, before COM.
        var now = await ReadStateAsync(ct).ConfigureAwait(false);
        HierarchyWriteSteps.CheckMoveNotebook("Object", ObjectId, now.ObjectNotebookPath, objectNotebook, NotebookChangedUserAction);
        HierarchyWriteSteps.CheckMoveNotebook("Target", TargetId, now.TargetNotebookPath, targetNotebook, NotebookChangedUserAction);
        state = now;
    }

    public override void Validate() => plan = Check(state ?? throw new InvalidOperationException("Step 3 hasn't run."));

    public override Task CheckBasisAsync(CancellationToken ct)
    {
        var now = Planned;
        if (HierarchyBasis.Compare(basis, now.State.Self.LastModifiedTime, HierarchyRules.Noun(now.State.Self.Kind), ObjectId, "moved", NoBasisUserAction, StaleUserAction) is { } warning)
        {
            warnings.Add(warning);
        }

        return Task.CompletedTask;
    }

    public override async Task SnapshotAsync(CancellationToken ct)
    {
        // AD-8: a hierarchy entry per parent, old then new (AD-7 Moves).
        var now = Planned.State;
        SourceEntryId = (await services.Snapshot.TakeAsync(
            new HierarchySnapshotRequest(correlationId, MoveWrites.OperationName, OperationClass.HierarchyWrite, now.Source.Id, now.SourceXml, services.ReadAccess.Protection()),
            ct).ConfigureAwait(false)).EntryId;
        TargetEntryId = (await services.Snapshot.TakeAsync(
            new HierarchySnapshotRequest(correlationId, MoveWrites.OperationName, OperationClass.HierarchyWrite, now.Target.Id, now.TargetXml, services.ReadAccess.Protection()),
            ct).ConfigureAwait(false)).EntryId;
    }

    public override async Task<MoveResult> CallAsync(CancellationToken ct)
    {
        var now = Planned;
        try
        {
            await services.Gateway.UpdateHierarchyAsync(now.Xml, ct).ConfigureAwait(false);
        }
        catch (OneNoteException ex) when (ex.ComHResult == PartialCopyReRead.HResult)
        {
            // AD-7 Moves: a partial copy is possible, so the outcome is unknown; the code stays (AD-9). On a cloud move the
            // settle runs first (3.12 Q4), so the re-read can see a late copy and the next write waits.
            await CloudSettle.QuietlyAsync(settle, now.TouchesCloud, ct).ConfigureAwait(false);
            var self = now.State.Self;
            var found = await PartialCopyReRead.FindingsAsync(
                services.Gateway, services.ReadAccess.Protection(), "move", now.State.Source, now.State.Target, ObjectId, self.Kind, self.Name, ct).ConfigureAwait(false);
            throw new OneNoteException(ex.Code, WithNote($"{ex.Message} {found}"), ex.ComHResult, ex, outcomeUnknown: true, ex.UserAction, retryable: false);
        }
        catch (OneNoteException ex) when (ex.ComHResult == FolderMissingHResult && now.State.Self.Kind == HierarchyKind.SectionGroup)
        {
            // J3: OneNote moved nothing; not outcomeUnknown.
            await CloudSettle.QuietlyAsync(settle, now.TouchesCloud, ct).ConfigureAwait(false);
            throw new OneNoteException(
                ErrorCode.NotSupported,
                WithNote($"OneNote couldn't move section group {ObjectId} into {HierarchyRules.Noun(now.State.Target.Kind)} {TargetId} (hrFolderDoesNotExist), so nothing was moved."),
                ex.ComHResult,
                ex,
                outcomeUnknown: false,
                GroupFolderUserAction,
                retryable: false);
        }
        catch (OneNoteException ex) when (FailureNote is not null)
        {
            // The pipeline still decides outcomeUnknown (AD-9); the message names the journal entries (2.11 Q1). A cloud move
            // settles first, so the next write waits (3.12 Q4).
            await CloudSettle.QuietlyAsync(settle, now.TouchesCloud, ct).ConfigureAwait(false);
            throw new OneNoteException(ex.Code, WithNote(ex.Message), ex.ComHResult, ex, ex.OutcomeUnknown, ex.UserAction, ex.Retryable);
        }

        return new MoveResult(ObjectId, [.. warnings]);
    }

    /// <summary>Between 7 and 8: the settle on a cloud move (J5), then both parents re-read and the node found by its ID.</summary>
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
            await settle(CloudSettle.Time, ct).ConfigureAwait(false);
        }

        var sourceAfter = await HierarchyWriteSteps.ReadChildrenAsync(services.Gateway, now.State.Source.Id, ct).ConfigureAwait(false);
        var targetAfter = await HierarchyWriteSteps.ReadChildrenAsync(services.Gateway, now.State.Target.Id, ct).ConfigureAwait(false);
        var inTarget = targetAfter.Children.Any(c => string.Equals(c.Id, ObjectId, StringComparison.Ordinal));
        var stillInSource = sourceAfter.Children.Any(c => string.Equals(c.Id, ObjectId, StringComparison.Ordinal));
        var noun = HierarchyRules.Noun(now.State.Self.Kind);
        if (!inTarget)
        {
            done.Add(new ToolWarning(
                ToolWarnings.ReadBackDiffers,
                $"{HierarchyBasis.Capitalized(noun)} {ObjectId} didn't read back in {HierarchyRules.Noun(now.State.Target.Kind)} {TargetId}, so the move wasn't confirmed.",
                ReadBackUserAction()));
        }

        if (stillInSource)
        {
            done.Add(new ToolWarning(
                ToolWarnings.ReadBackDiffers,
                $"{HierarchyBasis.Capitalized(noun)} {ObjectId} still reads back under {now.State.Source.Id}.",
                ReadBackUserAction()));
        }

        if (await GrantLocationWarningAsync(now, ct).ConfigureAwait(false) is { } granted)
        {
            done.Add(granted);
        }

        return new MoveResult(ObjectId, done);
    }

    /// <summary>
    /// 3.12 security review (Phill, 2026-11-17): step 4's check works on predicted paths, and OneNote may spell the real
    /// ones its own way, so the moved sections' paths are re-read and checked against stored read grants and live
    /// <c>once</c> windows (<see cref="ReadAccess.HoldsReadGrantAtLocation"/>). A section still at its own path never
    /// counts. The move stands; the warning names the node's ID and a count only.
    /// </summary>
    private async Task<ToolWarning?> GrantLocationWarningAsync(Plan now, CancellationToken ct)
    {
        var self = now.State.Self;
        var section = self.Kind == HierarchyKind.Section;
        IReadOnlyList<string> before = section
            ? [self.Path]
            : XmlReads.Readable(() => HierarchyTarget.SectionPathsBelow(now.State.BelowXml ?? string.Empty), "GetHierarchy");
        var xml = await services.Gateway.GetHierarchyAsync(ObjectId, section ? HierarchyScope.Self : HierarchyScope.Sections, ct).ConfigureAwait(false);
        IReadOnlyList<string> after = section
            ? [XmlReads.Readable(() => HierarchyTarget.Read(xml), "GetHierarchy").Path]
            : XmlReads.Readable(() => HierarchyTarget.SectionPathsBelow(xml), "GetHierarchy");

        var own = before.Where(p => !string.IsNullOrEmpty(p)).Select(GrantKeys.SectionKey).ToHashSet(GrantKeys.Comparer);
        var at = services.ReadAccess.Now();
        var taken = after.Count(p =>
            !string.IsNullOrEmpty(p)
            && !own.Contains(GrantKeys.SectionKey(p))
            && services.ReadAccess.HoldsReadGrantAtLocation(GrantKeys.SectionKey(p), at));
        if (taken == 0)
        {
            return null;
        }

        var message = section
            ? $"Section {ObjectId} was moved, and it reads back at a location that holds a read grant, so that grant now covers it."
            : string.Create(CultureInfo.InvariantCulture, $"Section group {ObjectId} was moved, and {taken} section(s) inside it read back at locations that hold a read grant, so those grants now cover them.");
        return new ToolWarning(ToolWarnings.ReadGrantAtLocation, message, ReadGrantAtLocationUserAction);
    }

    /// <summary>The settle or the read-back threw (its deadline or a cancel included): the move stands, with <c>readBackFailed</c>.</summary>
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
            $"The move of {ObjectId} to {TargetId} succeeded, but it couldn't be re-read afterwards ({MoveReadBack.Why(exception)}), so where it is now wasn't confirmed.",
            ReadBackUserAction()));
        return result with { Warnings = [.. result.Warnings, .. added] };
    }

    private Plan Planned => plan ?? throw new InvalidOperationException("Step 4 hasn't run.");

    /// <summary>Everything step 4 checks, read live: the node, both parents, a group's sections, both notebooks.</summary>
    private async Task<State> ReadStateAsync(CancellationToken ct)
    {
        var selfXml = await services.Gateway.GetHierarchyAsync(ObjectId, HierarchyScope.Self, ct).ConfigureAwait(false);
        var self = XmlReads.Readable(() => HierarchyTarget.Read(selfXml), "GetHierarchy");
        var parentId = await services.Gateway.GetHierarchyParentAsync(ObjectId, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(parentId))
        {
            throw ObjectNotFound("has no parent");
        }

        var sourceXml = await services.Gateway.GetHierarchyAsync(parentId, HierarchyScope.Children, ct).ConfigureAwait(false);
        var source = XmlReads.Readable(() => HierarchyTarget.Read(sourceXml), "GetHierarchy");
        var targetXml = await services.Gateway.GetHierarchyAsync(TargetId, HierarchyScope.Children, ct).ConfigureAwait(false);
        var target = XmlReads.Readable(() => HierarchyTarget.Read(targetXml), "GetHierarchy");
        var belowXml = self.Kind == HierarchyKind.SectionGroup
            ? await services.Gateway.GetHierarchyAsync(ObjectId, HierarchyScope.Sections, ct).ConfigureAwait(false)
            : null;

        var objectNotebookNode = await walk.OwningNotebookAsync(ObjectId, ct).ConfigureAwait(false);
        var targetNotebookNode = await walk.OwningNotebookAsync(TargetId, ct).ConfigureAwait(false);
        var targetNotebookPath = targetNotebookNode?.Path ?? (target.Kind == HierarchyKind.Notebook ? target.Path : null);
        return new State(self, source, sourceXml, target, targetXml, belowXml, objectNotebookNode?.Path, targetNotebookPath);
    }

    /// <summary>Step 4's refusals, in the plan's order; returns what the later steps use. Step 1 runs it too.</summary>
    private Plan Check(State now)
    {
        var self = now.Self;
        var target = now.Target;
        var noun = HierarchyRules.Noun(self.Kind);
        if (self.Kind is not (HierarchyKind.Section or HierarchyKind.SectionGroup))
        {
            throw HierarchyWriteSteps.Invalid($"Object {ObjectId} is a {noun}, not a section or section group, so nothing was moved.", ObjectUserAction);
        }

        // AD-7, AD-21 (epic 3 Q9, Q14, 3.2 H2): a protected section, or a group holding one, first, locked or not.
        var protection = services.ReadAccess.Protection();
        if (protection.IsProtected(self))
        {
            throw new OneNoteException(
                ErrorCode.NotSupported,
                $"Section {ObjectId} {Protection.Phrase(self.EncryptedMarker)}, and moving a protected section isn't supported, so nothing was moved.",
                userAction: ProtectedUserAction,
                retryable: false);
        }

        var below = now.BelowXml is { } xml ? XmlReads.Readable(() => EncryptedSections.Find(xml, protection.UnderRoot), "GetHierarchy") : [];
        if (below.Count > 0)
        {
            throw new OneNoteException(
                ErrorCode.NotSupported,
                string.Create(CultureInfo.InvariantCulture, $"Section group {ObjectId} holds {below.Count} protected section(s) (password-protected, or under the opened-copies folder, an export root or the backups folder), and moving a protected section isn't supported, so nothing was moved."),
                userAction: ProtectedUserAction,
                retryable: false);
        }

        // AD-7: the recycle bin is restore's only.
        if (HierarchyRules.RecycleBinProblem(self.IsRecycleBin, self.InRecycleBin) is { } binnedObject)
        {
            throw ObjectNotFound(binnedObject);
        }

        if (HierarchyRules.RecycleBinProblem(target.IsRecycleBin, target.InRecycleBin) is { } binnedTarget)
        {
            throw new OneNoteException(ErrorCode.ObjectNotFound, $"Object {TargetId} {binnedTarget}, so nothing was moved.", userAction: TargetUserAction);
        }

        if (target.Kind is not (HierarchyKind.Notebook or HierarchyKind.SectionGroup))
        {
            throw BadTarget($"is a {HierarchyRules.Noun(target.Kind)}, not a notebook or section group, and a {noun} moves into a notebook or section group");
        }

        var inside = now.BelowXml is { } groupXml ? XmlReads.Readable(() => HierarchyTarget.ContainerIdsBelow(groupXml), "GetHierarchy") : [];
        if (string.Equals(TargetId, ObjectId, StringComparison.Ordinal) || inside.Contains(TargetId, StringComparer.Ordinal))
        {
            throw BadTarget($"is section group {ObjectId} itself or inside it");
        }

        if (string.Equals(now.Source.Id, target.Id, StringComparison.Ordinal))
        {
            throw HierarchyWriteSteps.Invalid($"{HierarchyBasis.Capitalized(noun)} {ObjectId} is already in {HierarchyRules.Noun(target.Kind)} {TargetId}, so nothing was moved.", SameParentUserAction);
        }

        // J1 (spike 3.1 Implications 5, 15): out of a cloud notebook straight into another notebook's group fails 0x80042004.
        var otherNotebook = !string.IsNullOrEmpty(now.TargetNotebookPath) && !SameKey(now.ObjectNotebookPath, now.TargetNotebookPath);
        if (CloudNotebookCreate.IsUrl(now.ObjectNotebookPath) && otherNotebook && target.Kind == HierarchyKind.SectionGroup)
        {
            throw new OneNoteException(
                ErrorCode.NotSupported,
                $"{HierarchyBasis.Capitalized(noun)} {ObjectId} is in a cloud notebook (OneDrive or SharePoint), and OneNote fails a move from one straight into another notebook's section group ({TargetId}), so nothing was moved.",
                userAction: CloudIntoGroupUserAction,
                retryable: false);
        }

        // 3.12 Q2: a same-kind sibling's name, as a rename refuses it (AD-7 Names).
        var siblings = target.Children.Where(c => c.Kind == self.Kind).ToList();
        if (siblings.Any(c => string.Equals(c.Name, self.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw HierarchyWriteSteps.Invalid($"{HierarchyBasis.Capitalized(HierarchyRules.Noun(target.Kind))} {TargetId} already holds a {noun} with the name of {ObjectId}, so nothing was moved.", NameClashUserAction);
        }

        // 3.12 Q1: 1-based among the new parent's children of the same kind; omitted, last.
        var at = position ?? siblings.Count + 1;
        if (at > siblings.Count + 1)
        {
            throw HierarchyWriteSteps.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"position {at} is past the end of the {siblings.Count} {noun}(s) in {HierarchyRules.Noun(target.Kind)} {TargetId}, so nothing was moved."),
                PositionUserAction);
        }

        if (GrantLocationRefusal(now) is { } refusal)
        {
            throw refusal;
        }

        var update = HierarchyUpdate.MoveContainer(target.Kind, target.Id, siblings, self.Kind, ObjectId, at - 1);

        // AD-16: the hard gate for UpdateHierarchy. The XML is ours, so a failure is our bug, never sent.
        HierarchyWriteSteps.EnsureSchemaValid(update);

        var cloud = CloudNotebookCreate.IsUrl(now.ObjectNotebookPath) || CloudNotebookCreate.IsUrl(now.TargetNotebookPath);
        return new Plan(now, update, cloud);
    }

    /// <summary>
    /// AD-7 Moves, AD-21 (story 5.4's rule for a rename): the refusal for a move that would put a section (the node, or one
    /// beneath a moved group) on a location whose key holds a stored read grant or a live <c>once</c> window. A path the
    /// server can't predict is refused too. IDs and counts only.
    /// </summary>
    private OneNoteException? GrantLocationRefusal(State now)
    {
        var self = now.Self;
        IReadOnlyList<(string Before, string After)>? moved = self.Kind == HierarchyKind.Section
            ? MovedLocations.Section(self.Path, now.Target.Path) is { } after ? [(self.Path, after)] : null
            : MovedLocations.SectionsInGroup(self.Path, now.Target.Path, XmlReads.Readable(() => HierarchyTarget.SectionPathsBelow(now.BelowXml ?? string.Empty), "GetHierarchy"));
        if (moved is null)
        {
            return new OneNoteException(
                ErrorCode.NotSupported,
                $"The server can't tell where moving {HierarchyRules.Noun(self.Kind)} {ObjectId} would put its sections, so it can't check the move against read grants, and nothing was moved.",
                userAction: UnpredictableUserAction,
                retryable: false);
        }

        var at = services.ReadAccess.Now();
        var taken = moved.Count(m =>
            !GrantKeys.Comparer.Equals(GrantKeys.SectionKey(m.Before), GrantKeys.SectionKey(m.After))
            && services.ReadAccess.HoldsReadGrantAtLocation(GrantKeys.SectionKey(m.After), at));
        if (taken == 0)
        {
            return null;
        }

        var message = self.Kind == HierarchyKind.Section
            ? $"Moving section {ObjectId} would put it at a location that holds a read grant, so nothing was moved."
            : string.Create(CultureInfo.InvariantCulture, $"Moving section group {ObjectId} would put {taken} section(s) inside it at locations that hold a read grant, so nothing was moved.");
        return HierarchyWriteSteps.Invalid(message, GrantLocationUserAction);
    }

    private static bool SameKey(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && GrantKeys.Comparer.Equals(GrantKeys.NotebookKey(a), GrantKeys.NotebookKey(b));

    private ToolWarning SettledWarning() => CloudSettle.SettledWarning($"The move of {ObjectId}");

    private OneNoteException ObjectNotFound(string why) =>
        new(ErrorCode.ObjectNotFound, $"Object {ObjectId} {why}, so nothing was moved.", userAction: ObjectUserAction);

    private OneNoteException BadTarget(string why) =>
        HierarchyWriteSteps.Invalid($"Object {TargetId} {why}, so nothing was moved.", TargetUserAction);

    private string ReadBackUserAction() =>
        $"Read {TargetId} with get_hierarchy (scope children) to find where {ObjectId} is now.";

    /// <summary>What step 3 read: the node, its parent and the new parent (each with its XML), a group's sections, both notebooks' paths.</summary>
    private sealed record State(
        HierarchyTarget Self,
        HierarchyTarget Source,
        string SourceXml,
        HierarchyTarget Target,
        string TargetXml,
        string? BelowXml,
        string? ObjectNotebookPath,
        string? TargetNotebookPath);

    /// <summary>What step 4 decided: the state it checked, the XML to send, and whether a cloud notebook is involved.</summary>
    private sealed record Plan(State State, string Xml, bool TouchesCloud);
}
