using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Writes;

/// <summary>What every <see cref="HierarchyWrite"/> uses: the gateway, both access checks and step 6's snapshot.</summary>
[SupportedOSPlatform("windows")]
public sealed record HierarchyWriteServices(IOneNoteGateway Gateway, WriteAccess WriteAccess, ReadAccess ReadAccess, HierarchySnapshot Snapshot);

/// <summary>What a hierarchy write reports: its warnings, in step order. No <c>meta.basis</c>: hierarchy reads carry none (AD-17).</summary>
public sealed record HierarchyWriteResult(IReadOnlyList<ToolWarning> Warnings);

/// <summary>
/// The <c>HierarchyWrite</c> class's shared steps (AD-7; story 5.4) for <c>UpdateHierarchy</c> renames and reorders, which
/// change one node: the renamed node itself, or the parent whose children are reordered.
/// <list type="number">
/// <item>The arguments (an empty ID, the name rule, a basis carrying anything but a <c>lastModifiedTime</c> OneNote could
/// have written), then the write grant on the node's notebook, or on the node itself when it is a notebook
/// (<see cref="WriteAccess.CheckContainerAsync"/>, with AD-6's grant dialog). No read grant is ever asked for (AD-21).</item>
/// <item value="3">Step 3 reads the node fresh; step 4 refuses before COM the recycle bin or anything in it
/// (<see cref="ErrorCode.ObjectNotFound"/>), a locked section (<see cref="ErrorCode.SectionLocked"/>) and each write's own
/// cases, decides whether anything would change (<c>unchanged</c>: no snapshot, no COM call), and builds the minimal XML,
/// which must pass the 2013 XSD (AD-16) or the write is <see cref="ErrorCode.Internal"/> and never sent.</item>
/// <item value="5">The basis (AD-17): the caller's <c>lastModifiedTime</c> must be step 3's instant (compared as UTC), else
/// <see cref="ErrorCode.StaleWrite"/>; none is <c>noBasis</c>. <c>UpdateHierarchy</c> takes no date, so this comparison is
/// the only check, and a weak one (a rename doesn't move the renamed node's date).</item>
/// <item value="6">One <c>hierarchy</c> journal entry of step 3's XML (<see cref="HierarchySnapshot"/>).</item>
/// <item value="8">Between 7 and 8, the read-back: the node re-read and compared with what was sent. Differences are
/// <c>readBackDiffers</c>, a failed re-read <c>readBackFailed</c>; neither fails the write.</item>
/// </list>
/// Messages carry IDs and counts only, never a name (AD-9), and in a password-protected section with no read grant at the
/// call's start, never a page ID (AD-7, AD-21).
/// </summary>
[SupportedOSPlatform("windows")]
public abstract class HierarchyWrite : WriteOperation<HierarchyWriteResult>
{
    /// <summary>At most this many IDs are named in one message; the rest are counted.</summary>
    public const int MaxIdsNamed = 20;

    /// <summary>What to do after <c>noBasis</c> or <c>partialBasis</c>.</summary>
    public const string NoBasisUserAction =
        "Pass the lastModifiedTime attribute of the node's own element from get_hierarchy (scope self for a rename; scope children for the parent of a reorder) as basis: {lastModifiedTime}, so a change made since your read is caught.";

    /// <summary>What to do after <see cref="ErrorCode.StaleWrite"/>; a spurious one can follow our own write (spike 5.1 constraint 2).</summary>
    public const string StaleUserAction =
        "Re-read the node with get_hierarchy, check the change still makes sense, and retry with its current lastModifiedTime. Straight after another hierarchy write its date can move about a second late, so if nothing else changed, re-read and retry once.";

    /// <summary>What to do about an ID that isn't a live notebook, section group or section.</summary>
    public const string NotFoundUserAction =
        "Pass the ID of a notebook, section group or section that isn't in the recycle bin. Read the hierarchy (get_hierarchy) for a current ID.";

    private readonly List<ToolWarning> warnings = [];
    private readonly PageWriteBasis? basis;
    private string? targetXml;
    private HierarchyTarget? target;
    private string? changesXml;

    protected HierarchyWrite(HierarchyWriteServices services, string correlationId, string operation, string targetId, PageWriteBasis? basis, DateTimeOffset callStart)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
        CorrelationId = correlationId ?? string.Empty;
        Operation = operation;
        TargetId = targetId ?? string.Empty;
        this.basis = basis;
        CallStart = callStart;
    }

    public override OperationClass Class => OperationClass.HierarchyWrite;

    protected HierarchyWriteServices Services { get; }

    protected string CorrelationId { get; }

    protected string Operation { get; }

    /// <summary>The node this write changes: the renamed node, or the reordered parent (<c>meta.objectId</c>).</summary>
    public string TargetId { get; }

    /// <summary>When the call started, for the read-grant check that decides what messages may name (AD-7).</summary>
    protected DateTimeOffset CallStart { get; }

    /// <summary>Step 3's read of the node.</summary>
    protected HierarchyTarget Target => target ?? throw new InvalidOperationException("Step 3 hasn't read the node.");

    /// <summary>True when step 4 found nothing to change: no basis check, no snapshot, no COM call, the warning <c>unchanged</c>.</summary>
    public bool Unchanged { get; private set; }

    /// <summary>The journal entry step 6 stored, for <c>meta.journalEntryId</c>; null until the snapshot is taken.</summary>
    public Guid? JournalEntryId { get; private set; }

    /// <summary>The journal entry sentence, for step-7 failures the operation or the pipeline reports (2.11 Q1).</summary>
    public override string? FailureNote => JournalEntryId is { } entryId
        ? string.Create(CultureInfo.InvariantCulture, $"Its hierarchy as it was before this write is in journal entry {entryId:D}.")
        : null;

    /// <summary>The words for the write in messages: "renamed" or "reordered".</summary>
    protected abstract string Done { get; }

    /// <summary>The scope step 3 reads the node at: <c>self</c> for a rename, <c>children</c> for a reorder.</summary>
    protected abstract HierarchyScope ReadScope { get; }

    public override async Task AuthorizeAsync(CancellationToken ct)
    {
        // Step 1, before the lock and before any grant check: the arguments (AD-7).
        CheckArguments();
        if (basis is not null)
        {
            if (basis.PageHash is not null || basis.ElementHashes is { Count: > 0 })
            {
                throw Invalid(
                    "A hierarchy write's basis carries only lastModifiedTime (AD-17), and this one carries a page hash, so nothing was written.",
                    NoBasisUserAction);
            }

            if (basis.LastModifiedTime is not null && OneNoteDates.ToUtc(basis.LastModifiedTime) is null)
            {
                throw Invalid("The basis lastModifiedTime isn't a date OneNote writes, so nothing was written.", NoBasisUserAction);
            }
        }

        await Services.WriteAccess.CheckContainerAsync(TargetId, () => NotFound("isn't inside a notebook"), ct).ConfigureAwait(false);
        await PreCheckAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Step 1's own argument checks, before the grant (AD-7).</summary>
    protected abstract void CheckArguments();

    /// <summary>
    /// Step 1's last check, after the write grant and before the lock: a refusal that can be told from a read now, so
    /// the lock is never taken for it. Step 4 checks again against step 3's fresh read.
    /// </summary>
    protected virtual Task PreCheckAsync(CancellationToken ct) => Task.CompletedTask;

    public override async Task ReadAsync(CancellationToken ct)
    {
        targetXml = await Services.Gateway.GetHierarchyAsync(TargetId, ReadScope, ct).ConfigureAwait(false);
        target = XmlReads.Readable(() => HierarchyTarget.Read(targetXml), "GetHierarchy");
        if (Changeable(target))
        {
            await ReadMoreAsync(targetXml, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Whatever else step 3 reads once the node is known to be one this write may change.</summary>
    protected abstract Task ReadMoreAsync(string xml, CancellationToken ct);

    /// <summary>True when step 4's shared refusals won't refuse <paramref name="node"/> (so step 3's other reads are worth making).</summary>
    protected bool Changeable(HierarchyTarget node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return HierarchyRules.RecycleBinProblem(node.IsRecycleBin, node.InRecycleBin) is null && Accepts(node.Kind);
    }

    /// <summary>The kinds of node this write changes.</summary>
    protected abstract bool Accepts(HierarchyKind kind);

    public override void Validate()
    {
        var node = Target;
        if (HierarchyRules.RecycleBinProblem(node.IsRecycleBin, node.InRecycleBin) is { } binned)
        {
            throw NotFound(binned);
        }

        if (!Accepts(node.Kind))
        {
            throw WrongKind(node.Kind);
        }

        if (node.Kind == HierarchyKind.Section && node.Locked)
        {
            throw new OneNoteException(ErrorCode.SectionLocked, $"Section {TargetId} is locked, so nothing was {Done}.");
        }

        changesXml = ValidateChange();
        if (changesXml is null)
        {
            Unchanged = true;
            return;
        }

        // AD-16: the hard gate for UpdateHierarchy (story 5.4). The XML is ours, so a failure is our bug, never sent.
        var problems = SchemaValidation.Problems(changesXml);
        if (problems.Count > 0)
        {
            throw new OneNoteException(
                ErrorCode.Internal,
                string.Create(CultureInfo.InvariantCulture, $"The server built UpdateHierarchy XML that the 2013 schema refuses ({problems.Count} problem(s), the first at {problems[0].DisplayName ?? "the document"}), so nothing was sent."));
        }
    }

    /// <summary>
    /// Step 4's own checks, after the shared ones: throws to refuse, returns null when nothing would change, else the
    /// XML to send.
    /// </summary>
    protected abstract string? ValidateChange();

    /// <summary>The refusal for a node of a kind this write doesn't change.</summary>
    protected abstract OneNoteException WrongKind(HierarchyKind kind);

    public override Task CheckBasisAsync(CancellationToken ct)
    {
        // Nothing is written when nothing changes, so there is nothing to protect.
        if (Unchanged)
        {
            return Task.CompletedTask;
        }

        var noun = HierarchyRules.Noun(Target.Kind);
        if (basis is null)
        {
            warnings.Add(new ToolWarning(
                ToolWarnings.NoBasis,
                $"No basis was passed, so {noun} {TargetId} was checked against the hierarchy as the server just read it: a change made since your own read could be overwritten.",
                NoBasisUserAction));
            return Task.CompletedTask;
        }

        if (basis.LastModifiedTime is null)
        {
            warnings.Add(new ToolWarning(
                ToolWarnings.PartialBasis,
                $"The basis for {noun} {TargetId} is partial: it has no lastModifiedTime, so the {noun} as the server just read it was used.",
                NoBasisUserAction));
            return Task.CompletedTask;
        }

        if (OneNoteDates.ToUtc(Target.LastModifiedTime) is not { } current)
        {
            warnings.Add(new ToolWarning(
                ToolWarnings.PartialBasis,
                $"OneNote reported no lastModifiedTime for {noun} {TargetId}, so the basis couldn't be compared.",
                NoBasisUserAction));
            return Task.CompletedTask;
        }

        // Checked in step 1: the basis date parses.
        if (OneNoteDates.ToUtc(basis.LastModifiedTime) != current)
        {
            throw new OneNoteException(
                ErrorCode.StaleWrite,
                $"{Capitalized(noun)} {TargetId} changed since the basis was read (its lastModifiedTime differs), so nothing was {Done}.",
                userAction: StaleUserAction);
        }

        return Task.CompletedTask;
    }

    public override async Task SnapshotAsync(CancellationToken ct)
    {
        if (Unchanged)
        {
            return;
        }

        var written = await Services.Snapshot.TakeAsync(
            new HierarchySnapshotRequest(CorrelationId, Operation, OperationClass.HierarchyWrite, TargetId, targetXml ?? string.Empty),
            ct).ConfigureAwait(false);
        JournalEntryId = written.EntryId;
    }

    public override async Task<HierarchyWriteResult> CallAsync(CancellationToken ct)
    {
        if (changesXml is not { } xml)
        {
            // AD-7: nothing changes, so no COM call and no journal entry.
            return new HierarchyWriteResult([new ToolWarning(
                ToolWarnings.Unchanged,
                $"{Capitalized(HierarchyRules.Noun(Target.Kind))} {TargetId} already {UnchangedPhrase}, so nothing was sent to OneNote.")]);
        }

        try
        {
            await Services.Gateway.UpdateHierarchyAsync(xml, ct).ConfigureAwait(false);
        }
        catch (OneNoteException ex) when (FailureNote is { } note)
        {
            // The pipeline still decides outcomeUnknown (AD-9); the message names the journal entry (2.11 Q1).
            throw new OneNoteException(ex.Code, $"{ex.Message} {note}", ex.ComHResult, ex, ex.OutcomeUnknown, ex.UserAction, ex.Retryable);
        }

        return new HierarchyWriteResult([.. warnings]);
    }

    /// <summary>What the node already has when nothing changes: "has this name" or "has its children in this order".</summary>
    protected abstract string UnchangedPhrase { get; }

    /// <summary>Between 7 and 8: re-reads the node and adds <c>readBackDiffers</c> for each difference from what was sent.</summary>
    public override async Task<HierarchyWriteResult> AfterCallAsync(HierarchyWriteResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (Unchanged)
        {
            return result;
        }

        var xml = await Services.Gateway.GetHierarchyAsync(TargetId, ReadScope, ct).ConfigureAwait(false);
        var after = XmlReads.Readable(() => HierarchyTarget.Read(xml), "GetHierarchy");
        var differences = ReadBack(after);
        return differences.Count == 0
            ? result
            : new HierarchyWriteResult([.. result.Warnings, .. differences.Select(d => new ToolWarning(ToolWarnings.ReadBackDiffers, d, ReadBackUserAction))]);
    }

    /// <summary>The read-back's differences from what was sent, as messages carrying IDs and counts only.</summary>
    protected abstract IReadOnlyList<string> ReadBack(HierarchyTarget after);

    /// <summary>What to do after <c>readBackDiffers</c>.</summary>
    protected abstract string ReadBackUserAction { get; }

    /// <summary>The read-back threw (its deadline or a cancel included): the write stands, with the warning <c>readBackFailed</c>.</summary>
    public override HierarchyWriteResult AfterCallFailed(HierarchyWriteResult result, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(exception);
        var why = exception switch
        {
            OneNoteException one => one.Code.ToString(),
            OperationCanceledException => "the write's deadline or a cancel",
            _ => exception.GetType().Name,
        };
        return new HierarchyWriteResult([.. result.Warnings, new ToolWarning(
            ToolWarnings.ReadBackFailed,
            $"The write to {TargetId} succeeded, but it couldn't be re-read afterwards ({why}), so what OneNote kept wasn't checked.",
            "Re-read it with get_hierarchy to check the change.")]);
    }

    /// <summary><see cref="ErrorCode.ObjectNotFound"/> for the node, saying <paramref name="why"/>.</summary>
    protected OneNoteException NotFound(string why) =>
        new(ErrorCode.ObjectNotFound, $"Object {TargetId} {why}, so nothing was {Done}.", userAction: NotFoundUserAction);

    /// <summary><see cref="ErrorCode.InvalidArgument"/> with its own <c>userAction</c>.</summary>
    protected static OneNoteException Invalid(string message, string userAction) =>
        new(ErrorCode.InvalidArgument, message, userAction: userAction, retryable: false);

    /// <summary>The IDs for a message: up to <see cref="MaxIdsNamed"/>, then how many more.</summary>
    protected static string IdList(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var shown = string.Join(", ", ids.Take(MaxIdsNamed));
        return ids.Count > MaxIdsNamed
            ? string.Create(CultureInfo.InvariantCulture, $"{shown} and {ids.Count - MaxIdsNamed} more")
            : shown;
    }

    protected static string Capitalized(string noun) =>
        string.IsNullOrEmpty(noun) ? noun : char.ToUpperInvariant(noun[0]) + noun[1..];
}

/// <summary>
/// <c>rename_hierarchy_object</c> (story 5.4; Phill, 2026-11-13, Q1, Q3, Q4): a section's, section group's or notebook's
/// displayed name. A notebook sets <c>nickname</c>, never <c>name</c> (OneNote ignores a changed <c>name</c>, spike 5.1).
/// The name follows 5.3's rule (<see cref="SectionFileNames.NameProblem"/>); a section's loses one trailing <c>.one</c>
/// (<see cref="SectionFileNames.SectionName"/>) and its stem is checked and sent. A live sibling of either kind, or for a
/// notebook another open notebook's <c>nickname</c> or <c>name</c>, with the same name ignoring case is
/// <see cref="ErrorCode.InvalidArgument"/>; the node's own name doesn't count, so a case-only rename goes through. A page
/// is renamed by its title with <c>update_page_content</c>, and a section group in a notebook stored on OneDrive isn't
/// renamed at all (<see cref="ErrorCode.NotSupported"/>, AD-7).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RenameHierarchyWrite(HierarchyWriteServices services, string correlationId, string objectId, string name, PageWriteBasis? basis, DateTimeOffset callStart)
    : HierarchyWrite(services, correlationId, OperationName, objectId, basis, callStart)
{
    public const string OperationName = "rename_hierarchy_object";

    /// <summary>What to do about a name the server refuses.</summary>
    public const string NameUserAction = CreateHierarchyUseCase.NameUserAction;

    /// <summary>What to do about a name a sibling or another notebook already uses.</summary>
    public const string TakenUserAction =
        "Choose another name: two sections or section groups under one parent can't share a name, whatever their kind and case, and neither can two open notebooks.";

    /// <summary>What to do about a page as a rename target.</summary>
    public const string PageUserAction =
        "A page's name is its title: change it with update_page_content. rename_hierarchy_object renames a section, section group or notebook.";

    /// <summary>What to do about a section group in a notebook stored on OneDrive (AD-7; 5.2 plan Q5).</summary>
    public const string CloudGroupUserAction =
        "Ask the user to rename the section group in OneNote: renamed through this server, a section group in a notebook stored on OneDrive keeps its old name on OneDrive.";

    /// <summary>What to do when a rename would move a section onto a location that holds a read grant (AD-21).</summary>
    public const string GrantLocationUserAction =
        "Choose another name. A read grant belongs to a section's location, so this rename would let a section take over the read grant held at its new location. If that grant is no longer needed, ask the user to revoke it first (list_grants shows it; a once grant ends by itself when its window closes), then retry.";

    /// <summary>What to do when the server can't tell where a rename would move sections.</summary>
    public const string UnpredictableUserAction =
        "Ask the user to rename it in OneNote: the server can't check this rename against the user's read grants.";

    private readonly string name = name ?? string.Empty;
    private string sent = string.Empty;
    private IReadOnlyList<string> sectionsBelow = [];
    private string parentId = string.Empty;
    private HierarchyParent? siblings;
    private IReadOnlyList<HierarchyNode> notebooks = [];
    private string notebookPath = string.Empty;

    protected override string Done => "renamed";

    protected override HierarchyScope ReadScope => HierarchyScope.Self;

    protected override string UnchangedPhrase => "has this name";

    protected override string ReadBackUserAction => "Read it with get_hierarchy (scope self) to see the name OneNote kept.";

    protected override void CheckArguments()
    {
        if (TargetId.Length == 0)
        {
            throw Invalid("No objectId was given, so nothing was renamed.", NotFoundUserAction);
        }

        if (SectionFileNames.NameProblem(name) is { } problem)
        {
            throw Invalid($"The new name {problem}, so nothing was renamed.", NameUserAction);
        }
    }

    protected override bool Accepts(HierarchyKind kind) => kind is HierarchyKind.Notebook or HierarchyKind.SectionGroup or HierarchyKind.Section;

    /// <summary>
    /// Step 1's grant-location check (AD-21; story 5.4 security review): a rename that would move a section onto a
    /// location holding a read grant is refused before the lock. Anything else (the wrong kind, the recycle bin, a path
    /// the server can't follow) is left to step 4, which checks again against its fresh read.
    /// </summary>
    protected override async Task PreCheckAsync(CancellationToken ct)
    {
        var xml = await Services.Gateway.GetHierarchyAsync(TargetId, HierarchyScope.Self, ct).ConfigureAwait(false);
        var node = XmlReads.Readable(() => HierarchyTarget.Read(xml), "GetHierarchy");
        if (node.Kind is not (HierarchyKind.Section or HierarchyKind.SectionGroup) || node.IsRecycleBin || node.InRecycleBin)
        {
            return;
        }

        var below = node.Kind == HierarchyKind.SectionGroup ? await SectionsBelowAsync(ct).ConfigureAwait(false) : [];
        if (GrantLocationRefusal(node, below, NewName(node.Kind), strict: false) is { } refusal)
        {
            throw refusal;
        }
    }

    /// <summary>The name sent for a node of <paramref name="kind"/>: a section's without one trailing <c>.one</c>.</summary>
    private string NewName(HierarchyKind kind) => kind == HierarchyKind.Section ? SectionFileNames.SectionName(name) : name;

    private async Task<IReadOnlyList<string>> SectionsBelowAsync(CancellationToken ct)
    {
        var xml = await Services.Gateway.GetHierarchyAsync(TargetId, HierarchyScope.Sections, ct).ConfigureAwait(false);
        return XmlReads.Readable(() => HierarchyTarget.SectionPathsBelow(xml), "GetHierarchy");
    }

    /// <summary>
    /// The refusal for a rename of <paramref name="node"/> to <paramref name="newName"/> that would move a section (the
    /// node itself, or one of <paramref name="below"/> under a section group) onto a location whose key holds a stored
    /// read grant or a live <c>once</c> window, which the section would then inherit (AD-21: a grant follows its path,
    /// and our own write must not re-bind one). A section staying at its own key (a case-only rename) never counts.
    /// When the server can't tell where a section would go, <paramref name="strict"/> refuses with
    /// <see cref="ErrorCode.NotSupported"/>; otherwise that is left to the strict check. Messages carry IDs and counts
    /// only, never a name or path (AD-21).
    /// </summary>
    private OneNoteException? GrantLocationRefusal(HierarchyTarget node, IReadOnlyList<string> below, string newName, bool strict)
    {
        IReadOnlyList<(string Before, string After)>? moved = node.Kind switch
        {
            HierarchyKind.Section => RenamedLocations.Section(node.Path, newName) is { } after ? [(node.Path, after)] : null,
            HierarchyKind.SectionGroup => RenamedLocations.SectionsInGroup(node.Path, newName, below),
            _ => [],
        };
        if (moved is null)
        {
            return strict
                ? new OneNoteException(
                    ErrorCode.NotSupported,
                    $"The server can't tell where renaming {HierarchyRules.Noun(node.Kind)} {TargetId} would move its sections, so it can't check the rename against read grants, and nothing was renamed.",
                    userAction: UnpredictableUserAction,
                    retryable: false)
                : null;
        }

        var now = Services.ReadAccess.Now();
        var taken = moved.Count(m =>
            !GrantKeys.Comparer.Equals(GrantKeys.SectionKey(m.Before), GrantKeys.SectionKey(m.After))
            && Services.ReadAccess.HoldsReadGrantAt(GrantKeys.SectionKey(m.After), now));
        if (taken == 0)
        {
            return null;
        }

        var message = node.Kind == HierarchyKind.Section
            ? $"Renaming section {TargetId} would move it to a location that holds a read grant, so nothing was renamed."
            : string.Create(CultureInfo.InvariantCulture, $"Renaming section group {TargetId} would move {taken} section(s) inside it to locations that hold a read grant, so nothing was renamed.");
        return Invalid(message, GrantLocationUserAction);
    }

    protected override async Task ReadMoreAsync(string xml, CancellationToken ct)
    {
        if (Target.Kind == HierarchyKind.Notebook)
        {
            // Q4: no two open notebooks share a displayed name.
            var all = await Services.Gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, ct).ConfigureAwait(false);
            notebooks = XmlReads.Readable(() => HierarchyNode.Notebooks(all), "GetHierarchy");
            return;
        }

        // The siblings, under the node's own parent (5.3 Q4).
        parentId = await Services.Gateway.GetHierarchyParentAsync(TargetId, ct).ConfigureAwait(false);
        var children = await Services.Gateway.GetHierarchyAsync(parentId, HierarchyScope.Children, ct).ConfigureAwait(false);
        siblings = XmlReads.Readable(() => HierarchyChildren.Read(children), "GetHierarchy");

        if (Target.Kind == HierarchyKind.SectionGroup)
        {
            // AD-21: every section beneath moves with the group, for step 4's grant-location check.
            sectionsBelow = await SectionsBelowAsync(ct).ConfigureAwait(false);

            // AD-7: the notebook's path says whether it is stored on OneDrive.
            var notebook = await new HierarchyWalk(Services.Gateway).OwningNotebookAsync(TargetId, ct).ConfigureAwait(false);
            notebookPath = notebook?.Path ?? string.Empty;
        }
    }

    protected override OneNoteException WrongKind(HierarchyKind kind) => kind == HierarchyKind.Page
        ? Invalid($"Object {TargetId} is a page, so nothing was renamed.", PageUserAction)
        : Invalid($"Object {TargetId} isn't a section, section group or notebook, so nothing was renamed.", NotFoundUserAction);

    protected override string? ValidateChange()
    {
        var node = Target;
        if (node.Kind == HierarchyKind.SectionGroup && CloudNotebookCreate.IsUrl(notebookPath))
        {
            throw new OneNoteException(
                ErrorCode.NotSupported,
                $"Section group {TargetId} is in a notebook stored on OneDrive, where a rename through COM never reaches OneDrive, so nothing was renamed.",
                userAction: CloudGroupUserAction,
                retryable: false);
        }

        // A section's name is sent without .one (as 5.3 Q2 adds it); the stem is what must be a valid name.
        sent = NewName(node.Kind);
        if (node.Kind == HierarchyKind.Section && SectionFileNames.NameProblem(sent) is { } problem)
        {
            throw Invalid($"The new name, without .one, {problem}, so nothing was renamed.", NameUserAction);
        }

        if (string.Equals(sent, node.DisplayName, StringComparison.Ordinal))
        {
            return null;
        }

        if (node.Kind == HierarchyKind.Notebook)
        {
            var other = notebooks.FirstOrDefault(n =>
                !string.Equals(n.Id, TargetId, StringComparison.Ordinal)
                && (string.Equals(n.Name, sent, StringComparison.OrdinalIgnoreCase) || string.Equals(n.DisplayName, sent, StringComparison.OrdinalIgnoreCase)));
            if (other is not null)
            {
                throw Invalid($"Another open notebook ({other.Id}) already uses this name, so notebook {TargetId} wasn't renamed.", TakenUserAction);
            }
        }
        else if (siblings is { } parent && HierarchyRules.Named(parent, sent, exceptId: TargetId).FirstOrDefault() is { } clash)
        {
            // OneNote's own answer is 0x80042017 (spike 5.1); refused before COM (5.3 Q4).
            throw Invalid(
                $"A {HierarchyRules.Noun(clash.Kind)} with this name ({clash.Id}) already exists under {parentId}, so {HierarchyRules.Noun(node.Kind)} {TargetId} wasn't renamed.",
                TakenUserAction);
        }

        // AD-21 (story 5.4 security review): never hand a read grant to another section by moving it onto the grant's key.
        if (GrantLocationRefusal(node, sectionsBelow, sent, strict: true) is { } refusal)
        {
            throw refusal;
        }

        return HierarchyUpdate.Rename(node.Kind, node.Id, sent);
    }

    protected override IReadOnlyList<string> ReadBack(HierarchyTarget after)
    {
        ArgumentNullException.ThrowIfNull(after);
        var kept = Target.Kind == HierarchyKind.Notebook ? after.Nickname : after.Name;
        return string.Equals(kept, sent, StringComparison.Ordinal)
            ? []
            : [$"{Capitalized(HierarchyRules.Noun(Target.Kind))} {TargetId} reads back with a name other than the one sent: OneNote changed or didn't apply it."];
    }
}

/// <summary>
/// <c>reorder_hierarchy_children</c> (story 5.4; Phill, 2026-11-13, Q1, Q5, Q6, Q7): the caller lists every live child of
/// a notebook, section group or section in the new order: sections then section groups under a notebook or group, pages
/// under a section. The recycle bin is never listed and never sent. A missing, extra, duplicated or foreign ID is
/// <see cref="ErrorCode.InvalidArgument"/> (AD-7's "no moves"), and so is a section group before a section. Pages keep their
/// <c>pageLevel</c>, and each subpage block moves whole: every page must still follow its own parent page, with only that
/// parent's block between them. Notebooks themselves aren't reordered (Q7).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ReorderHierarchyWrite(HierarchyWriteServices services, string correlationId, string parentId, IReadOnlyList<string>? childIds, PageWriteBasis? basis, DateTimeOffset callStart)
    : HierarchyWrite(services, correlationId, OperationName, parentId, basis, callStart)
{
    public const string OperationName = "reorder_hierarchy_children";

    /// <summary>What to do about a child list the server refuses.</summary>
    public const string ListUserAction =
        "Read the parent with get_hierarchy (scope children) and pass the ID of every child it shows exactly once, in the new order: sections before section groups, and each page followed by its own subpages. Leave out the recycle bin. Moving something to another parent isn't supported here: ask the user to do it in OneNote.";

    private readonly IReadOnlyList<string> childIds = childIds ?? [];
    private IReadOnlyList<HierarchyMember> ordered = [];
    private bool namesIds = true;

    protected override string Done => "reordered";

    protected override HierarchyScope ReadScope => HierarchyScope.Children;

    protected override string UnchangedPhrase => "has its children in this order";

    protected override string ReadBackUserAction => "Read the parent with get_hierarchy (scope children) to see the order OneNote kept.";

    protected override void CheckArguments()
    {
        if (TargetId.Length == 0)
        {
            throw Invalid("No parentId was given, so nothing was reordered.", NotFoundUserAction);
        }

        var empty = childIds.Count(string.IsNullOrEmpty);
        if (empty > 0)
        {
            throw Invalid(string.Create(CultureInfo.InvariantCulture, $"childIds holds {empty} empty ID(s), so nothing was reordered."), ListUserAction);
        }

        // Counts only: the section's read grant isn't known yet (AD-7, AD-21).
        var duplicates = childIds.Count - childIds.Distinct(StringComparer.Ordinal).Count();
        if (duplicates > 0)
        {
            throw Invalid(string.Create(CultureInfo.InvariantCulture, $"childIds lists {duplicates} ID(s) more than once, so nothing was reordered."), ListUserAction);
        }
    }

    protected override bool Accepts(HierarchyKind kind) => kind is HierarchyKind.Notebook or HierarchyKind.SectionGroup or HierarchyKind.Section;

    protected override Task ReadMoreAsync(string xml, CancellationToken ct)
    {
        // A protected section's pages: with no read grant at the call's start, messages carry counts only (AD-7, AD-21).
        if (Target.Kind == HierarchyKind.Section)
        {
            var section = XmlReads.Readable(() => HierarchyNode.Root(xml), "GetHierarchy");
            namesIds = Services.ReadAccess.HoldsReadGrant(section, CallStart);
        }

        return Task.CompletedTask;
    }

    protected override OneNoteException WrongKind(HierarchyKind kind) =>
        Invalid($"Object {TargetId} isn't a notebook, section group or section, so it has no children to reorder.", ListUserAction);

    protected override string? ValidateChange()
    {
        var node = Target;
        var live = node.Children.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var foreign = childIds.Where(id => !live.ContainsKey(id)).ToList();
        var missing = node.Children.Select(c => c.Id).Where(id => !childIds.Contains(id, StringComparer.Ordinal)).ToList();
        if (foreign.Count > 0 || missing.Count > 0)
        {
            var parts = new List<string>();
            if (foreign.Count > 0)
            {
                parts.Add(namesIds
                    ? $"{IdList(foreign)} {(foreign.Count == 1 ? "isn't a live child" : "aren't live children")} of {TargetId}"
                    : string.Create(CultureInfo.InvariantCulture, $"{foreign.Count} ID(s) aren't live children of {TargetId}"));
            }

            if (missing.Count > 0)
            {
                parts.Add(namesIds
                    ? $"{IdList(missing)} {(missing.Count == 1 ? "is a child" : "are children")} of {TargetId} missing from childIds"
                    : string.Create(CultureInfo.InvariantCulture, $"{missing.Count} child(ren) of {TargetId} are missing from childIds"));
            }

            throw Invalid($"childIds must list every live child of {TargetId} exactly once: {string.Join("; ", parts)}. Nothing was reordered.", ListUserAction);
        }

        ordered = [.. childIds.Select(id => live[id])];
        if (node.Kind == HierarchyKind.Section)
        {
            CheckPageBlocks(node.Children, ordered);
        }
        else if (ordered.SkipWhile(c => c.Kind == HierarchyKind.Section).Any(c => c.Kind == HierarchyKind.Section))
        {
            // The 2013 schema's order; OneNote answered the other with 0x80042001 and applied nothing (spike 5.1 constraint 4).
            throw Invalid($"childIds lists a section group before a section, so {TargetId} wasn't reordered: sections come first.", ListUserAction);
        }

        if (ordered.Select(c => c.Id).SequenceEqual(node.Children.Select(c => c.Id), StringComparer.Ordinal))
        {
            return null;
        }

        return HierarchyUpdate.Reorder(node.Kind, node.Id, ordered);
    }

    /// <summary>
    /// Q6: <paramref name="sent"/> must keep every page's parent page, the nearest earlier page with a lower
    /// <c>pageLevel</c> (none for a top-level page), as <paramref name="current"/> has it. Levels never change, so this is
    /// the same as moving whole subpage blocks only. A page whose level jumps by more than one belongs to the nearest
    /// earlier page with a lower level (an assumption of the 5.4 build).
    /// </summary>
    private void CheckPageBlocks(IReadOnlyList<HierarchyMember> current, IReadOnlyList<HierarchyMember> sent)
    {
        var before = Parents(current);
        var after = Parents(sent);
        var moved = sent.Where(p => !string.Equals(before[p.Id], after[p.Id], StringComparison.Ordinal)).Select(p => p.Id).ToList();
        if (moved.Count == 0)
        {
            return;
        }

        throw Invalid(
            namesIds
                ? $"In section {TargetId}, {IdList(moved)} would no longer follow {(moved.Count == 1 ? "its" : "their")} own parent page, so nothing was reordered: a page moves with its subpages, and a subpage stays under its parent."
                : string.Create(CultureInfo.InvariantCulture, $"In section {TargetId}, {moved.Count} page(s) would no longer follow their own parent page, so nothing was reordered: a page moves with its subpages, and a subpage stays under its parent."),
            ListUserAction);
    }

    /// <summary>Each page's parent page in <paramref name="pages"/>: the nearest earlier page with a lower level; empty for none.</summary>
    private static Dictionary<string, string> Parents(IReadOnlyList<HierarchyMember> pages)
    {
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        var stack = new List<HierarchyMember>();
        foreach (var page in pages)
        {
            while (stack.Count > 0 && stack[^1].PageLevel >= page.PageLevel)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            parents[page.Id] = stack.Count > 0 ? stack[^1].Id : string.Empty;
            stack.Add(page);
        }

        return parents;
    }

    protected override IReadOnlyList<string> ReadBack(HierarchyTarget after)
    {
        ArgumentNullException.ThrowIfNull(after);
        var differences = new List<string>();
        var read = after.Children.Select(c => c.Id).ToList();
        var misplaced = ordered.Where((c, i) => i >= read.Count || !string.Equals(read[i], c.Id, StringComparison.Ordinal)).Count();
        if (misplaced > 0 || read.Count != ordered.Count)
        {
            differences.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"The children of {TargetId} read back in another order than sent: {misplaced} of the {ordered.Count} sent are elsewhere, and {read.Count} live child(ren) read back."));
        }

        if (Target.Kind == HierarchyKind.Section)
        {
            var levels = after.Children.ToDictionary(c => c.Id, c => c.PageLevel, StringComparer.Ordinal);
            var relevelled = ordered.Count(p => levels.TryGetValue(p.Id, out var level) && level != p.PageLevel);
            if (relevelled > 0)
            {
                differences.Add(string.Create(CultureInfo.InvariantCulture, $"In section {TargetId}, {relevelled} page(s) read back at another pageLevel than before."));
            }
        }

        return differences;
    }
}
