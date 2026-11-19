using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// What a merge reports: the copies' new IDs in destination order (null for a password-protected destination, 3.6 Q6, or
/// when the read-back failed) and its warnings in step order.
/// </summary>
public sealed record MergeResult(IReadOnlyList<string>? PageIds, IReadOnlyList<ToolWarning> Warnings);

/// <summary>
/// <c>merge_sections</c> (story 3.6; AD-5, AD-7 Merges, AD-14, AD-21): one <c>HierarchyWrite</c> whose COM call is one
/// <c>MergeSections(source, destination)</c>. OneNote leaves the source section and its pages untouched and puts copies
/// under new IDs at the end of the destination, subpages kept (spike 3.1 Implication 4).
/// <list type="number">
/// <item>Step 1, before the lock: an empty <c>sourceId</c> or <c>destinationId</c>, a basis with a hash and the same
/// section twice are <see cref="ErrorCode.InvalidArgument"/> (AD-17). Each ID is then found by its walk to its notebook,
/// never by a <c>self</c> read first, because OneNote hides a locked section's pages (3.3 S1): an unknown ID is
/// <see cref="ErrorCode.ObjectNotFound"/>, and so is the recycle bin itself or anything in it, checked before the kind; then
/// each must be a section (<see cref="ErrorCode.InvalidArgument"/>). Then, in this order: a protected source (AD-21's test:
/// the <c>encrypted</c> marker or a path under a protected root), locked or not, is <see cref="ErrorCode.NotSupported"/>; a
/// locked destination is <see cref="ErrorCode.SectionLocked"/>; a source with no live pages is
/// <see cref="ErrorCode.InvalidArgument"/> (Q7 a). Only then the write grants, the source's notebook first (AD-6; the
/// pipeline asks once per notebook and re-resolves both under the lock, story 3.12's ledger).</item>
/// <item value="3">Under the lock, both sections are walked to their notebooks again and read at <c>children</c>.</item>
/// <item>Step 4 repeats every step-1 refusal against those reads.</item>
/// <item>The basis is the source section's hierarchy <c>lastModifiedTime</c> (AD-17), through <see cref="HierarchyBasis"/>:
/// optional (<c>noBasis</c>) and weak.</item>
/// <item>One <c>hierarchy</c> journal entry of the destination (AD-8); a protected destination's is DPAPI-encrypted.</item>
/// <item>One <c>MergeSections</c>, never retried. <c>0x8004201F</c> (hrMergeFailed) keeps <see cref="ErrorCode.Internal"/>
/// and sets <c>outcomeUnknown</c>; it and every other failure AD-9 calls ambiguous (<see cref="ErrorCode.Timeout"/>,
/// <see cref="ErrorCode.ComUnavailable"/>, <see cref="ErrorCode.Cancelled"/>, <c>0x800706BE</c>, a cancel or the deadline)
/// get a best-effort re-read of the destination by its ID, still under the lock, after the cloud settle, and the message
/// gives the new pages' count and IDs (the count alone for a protected destination) and says to delete them before merging
/// again. The advice goes in the message: <c>ToolErrors</c> replaces an <c>outcomeUnknown</c> error's <c>userAction</c>.</item>
/// </list>
/// Between 7 and 8: a merge touching a cloud notebook settles 10 s under the lock (Q2 a; the warning <c>settled</c>), then
/// both sections are re-read. The copies are the destination's pages whose IDs weren't there before; a count other than the
/// source's live pages, or a source that no longer reads back with its ID and every page ID, is <c>readBackDiffers</c>; a
/// failed re-read is <c>readBackFailed</c>. The search cache drops nothing: no existing page changes. Messages carry IDs and
/// counts only, never a name (AD-9).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MergeSectionsWrite : WriteOperation<MergeResult>
{
    public const string OperationName = "merge_sections";

    /// <summary>hrMergeFailed: it came three times in 18 merges, twice after a partial copy (spike 3.1 Implication 4).</summary>
    public const int MergeFailedHResult = unchecked((int)0x8004201F);

    /// <summary>What to do about a source that isn't a live section.</summary>
    public const string SourceUserAction =
        "Pass as sourceId the ID of a section that isn't in the recycle bin: merge_sections copies one section's pages into another section. Read the hierarchy (get_hierarchy) for a current ID.";

    /// <summary>What to do about a destination that isn't a live section.</summary>
    public const string DestinationUserAction =
        "Pass as destinationId the ID of a section that isn't in the recycle bin: merge_sections copies one section's pages into another section. Read the hierarchy (get_hierarchy) for a current ID.";

    /// <summary>What to do when both IDs are the same section.</summary>
    public const string SameSectionUserAction =
        "Pass two different sections: sourceId, whose pages are copied, and destinationId, the section they are copied into.";

    /// <summary>What to do about a password-protected source (AD-7 Merges, AD-21; E8, H2).</summary>
    public const string ProtectedUserAction =
        "Ask the user to merge it in OneNote: a password-protected section isn't merged through this server, because the merge could copy its pages out of the section's encryption.";

    /// <summary>What to do about a source with no live pages (Q7 a).</summary>
    public const string EmptySourceUserAction =
        "The source section has no pages, so there is nothing to merge. Read it with get_hierarchy (scope children) to check, and pass a section that has pages.";

    /// <summary>What to do after <c>noBasis</c> or <c>partialBasis</c>.</summary>
    public const string NoBasisUserAction =
        "Pass the source section's lastModifiedTime attribute from a get_hierarchy read as basis: {lastModifiedTime}, so a change made since your read is caught.";

    /// <summary>What to do after <see cref="ErrorCode.StaleWrite"/>.</summary>
    public const string StaleUserAction =
        "Re-read the source section with get_hierarchy, check the merge still makes sense, and retry with its current lastModifiedTime. Straight after another hierarchy write a date can move about a second late, so if nothing else changed, re-read and retry once.";

    /// <summary>What to do after the warning <c>settled</c>.</summary>
    public const string SettledUserAction =
        "The merge touched a cloud notebook (OneDrive or SharePoint). Before relying on it, ask the user to check in OneNote that it synced (no sync error on either notebook).";

    private readonly HierarchyWriteServices services;
    private readonly Func<TimeSpan, CancellationToken, Task> settle;
    private readonly string correlationId;
    private readonly PageWriteBasis? basis;
    private readonly List<ToolWarning> warnings = [];

    private State? state;
    private Plan? plan;
    private bool settled;
    private string? findings;

    /// <param name="settle">Waits the settle out (<see cref="CloudSettle.Time"/>); a test passes its own.</param>
    public MergeSectionsWrite(
        HierarchyWriteServices services,
        string correlationId,
        string sourceId,
        string destinationId,
        PageWriteBasis? basis,
        Func<TimeSpan, CancellationToken, Task> settle)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settle);
        this.services = services;
        this.settle = settle;
        this.correlationId = correlationId ?? string.Empty;
        SourceId = sourceId ?? string.Empty;
        DestinationId = destinationId ?? string.Empty;
        this.basis = basis;
    }

    public override OperationClass Class => OperationClass.HierarchyWrite;

    /// <summary>The source section (<c>meta.objectId</c>): OneNote leaves it and its pages untouched.</summary>
    public string SourceId { get; }

    /// <summary>The destination section (<c>meta.parentId</c>).</summary>
    public string DestinationId { get; }

    /// <summary>The destination's journal entry (<c>meta.journalEntryId</c>); null until step 6 stores it.</summary>
    public Guid? JournalEntryId { get; private set; }

    /// <summary>The journal entry, and after an ambiguous step-7 failure what the destination's re-read found (IDs and counts only).</summary>
    public override string? FailureNote
    {
        get
        {
            var note = JournalEntryId is { } entry
                ? string.Create(CultureInfo.InvariantCulture, $"The destination's hierarchy as it was before this merge is in journal entry {entry:D}.")
                : null;
            return (findings, note) switch
            {
                (null, _) => note,
                (_, null) => findings,
                _ => $"{findings} {note}",
            };
        }
    }

    public override async Task AuthorizeAsync(CancellationToken ct)
    {
        // Step 1, before the lock and before any read: the arguments (AD-7, AD-17).
        if (SourceId.Length == 0)
        {
            throw HierarchyWriteSteps.Invalid("No sourceId was given, so nothing was merged.", SourceUserAction);
        }

        if (DestinationId.Length == 0)
        {
            throw HierarchyWriteSteps.Invalid("No destinationId was given, so nothing was merged.", DestinationUserAction);
        }

        HierarchyBasis.CheckArgument(basis, NoBasisUserAction);

        if (string.Equals(SourceId, DestinationId, StringComparison.Ordinal))
        {
            throw HierarchyWriteSteps.Invalid($"sourceId and destinationId are both {SourceId}, so nothing was merged.", SameSectionUserAction);
        }

        // Every refusal against a read made now, before any grant dialog (AD-7 step 1).
        Check(await ReadStateAsync(ct).ConfigureAwait(false));

        // AD-6: both notebooks' write grants, the source's first; the pipeline asks once per notebook (3.4 Q3) and
        // re-resolves both under the lock (story 3.12).
        await services.WriteAccess.CheckAsync(SourceId, () => SourceNotFound("isn't inside a notebook"), ct).ConfigureAwait(false);
        await services.WriteAccess.CheckAsync(DestinationId, () => DestinationNotFound("isn't inside a notebook"), ct).ConfigureAwait(false);
    }

    /// <summary>Step 3: both sections walked to their notebooks again and read at <c>children</c>, under the lock.</summary>
    public override async Task ReadAsync(CancellationToken ct) => state = await ReadStateAsync(ct).ConfigureAwait(false);

    /// <summary>Step 4: every step-1 refusal again, against step 3's reads.</summary>
    public override void Validate() => plan = Check(state ?? throw new InvalidOperationException("Step 3 hasn't run."));

    public override Task CheckBasisAsync(CancellationToken ct)
    {
        var now = Planned;
        if (HierarchyBasis.Compare(basis, now.Source.LastModifiedTime, "section", SourceId, "merged", NoBasisUserAction, StaleUserAction) is { } warning)
        {
            warnings.Add(warning);
        }

        return Task.CompletedTask;
    }

    public override async Task SnapshotAsync(CancellationToken ct)
    {
        // AD-8: a hierarchy entry of the destination only; no page snapshot of the source (Q12's condition isn't met).
        var now = Planned;
        JournalEntryId = (await services.Snapshot.TakeAsync(
            new HierarchySnapshotRequest(correlationId, OperationName, OperationClass.HierarchyWrite, DestinationId, now.DestinationXml, services.ReadAccess.Protection()),
            ct).ConfigureAwait(false)).EntryId;
    }

    public override async Task<MergeResult> CallAsync(CancellationToken ct)
    {
        var now = Planned;
        try
        {
            await services.Gateway.MergeSectionsAsync(SourceId, DestinationId, ct).ConfigureAwait(false);
        }
        catch (OneNoteException ex) when (ex.ComHResult == MergeFailedHResult)
        {
            // AD-7 Merges (approved spike 3.1 rule): a partial copy is possible, so the outcome is unknown; the code stays
            // Internal (AD-9). The cloud settle first, then the re-read, here under the lock and before the throw.
            await CloudSettle.QuietlyAsync(settle, now.TouchesCloud, ct).ConfigureAwait(false);
            findings = await FindingsAfterFailureAsync(now, ct).ConfigureAwait(false);
            throw new OneNoteException(ex.Code, $"{ex.Message} {FailureNote}", ex.ComHResult, ex, outcomeUnknown: true, ex.UserAction, retryable: false);
        }
        catch (OneNoteException ex) when (WritePipeline.IsAmbiguous(ex))
        {
            // Every other outcomeUnknown failure gets the same re-read; the pipeline still sets outcomeUnknown (AD-9).
            await CloudSettle.QuietlyAsync(settle, now.TouchesCloud, ct).ConfigureAwait(false);
            findings = await FindingsAfterFailureAsync(now, ct).ConfigureAwait(false);
            throw new OneNoteException(ex.Code, $"{ex.Message} {FailureNote}", ex.ComHResult, ex, ex.OutcomeUnknown, ex.UserAction, ex.Retryable);
        }
        catch (OperationCanceledException)
        {
            // A cancel or the deadline: the pipeline reports it, with FailureNote, which now carries the findings.
            findings = await FindingsAfterFailureAsync(now, ct).ConfigureAwait(false);
            throw;
        }
        catch (OneNoteException ex) when (FailureNote is not null)
        {
            // Not ambiguous (0x8004201B SectionLocked, 0x80042030 ModalDialogOpen, ...): the message names the journal
            // entry (2.11 Q1). A cloud merge settles first, so the next write waits (as a move, 3.12 Q4).
            await CloudSettle.QuietlyAsync(settle, now.TouchesCloud, ct).ConfigureAwait(false);
            throw new OneNoteException(ex.Code, $"{ex.Message} {FailureNote}", ex.ComHResult, ex, ex.OutcomeUnknown, ex.UserAction, ex.Retryable);
        }

        return new MergeResult(null, [.. warnings]);
    }

    /// <summary>Between 7 and 8: the cloud settle (Q2 a), then both sections re-read and the copies found.</summary>
    public override async Task<MergeResult> AfterCallAsync(MergeResult result, CancellationToken ct)
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

        var sourceAfter = await HierarchyWriteSteps.ReadChildrenAsync(services.Gateway, SourceId, ct).ConfigureAwait(false);
        var destinationAfter = await HierarchyWriteSteps.ReadChildrenAsync(services.Gateway, DestinationId, ct).ConfigureAwait(false);
        var copies = NewPages(now.Destination, destinationAfter);
        var expected = now.Source.Children.Count;
        if (copies.Count != expected)
        {
            done.Add(new ToolWarning(
                ToolWarnings.ReadBackDiffers,
                string.Create(CultureInfo.InvariantCulture, $"Section {DestinationId} read back with {copies.Count} new page(s), but source section {SourceId} had {expected}, so the merge wasn't confirmed in full."),
                ReadBackUserAction()));
        }

        var kept = sourceAfter.Children.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var missing = now.Source.Children.Count(c => !kept.Contains(c.Id));
        if (missing > 0)
        {
            done.Add(new ToolWarning(
                ToolWarnings.ReadBackDiffers,
                string.Create(CultureInfo.InvariantCulture, $"Source section {SourceId} didn't read back as it was: {missing} of its {expected} page(s) are no longer listed in it."),
                $"Read section {SourceId} with get_hierarchy (scope children) to see what it holds now."));
        }

        return new MergeResult(now.DestinationProtected ? null : copies, done);
    }

    /// <summary>The settle or the read-back threw (its deadline or a cancel included): the merge stands, with <c>readBackFailed</c>.</summary>
    public override MergeResult AfterCallFailed(MergeResult result, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(exception);
        var added = new List<ToolWarning>();
        if (settled && !result.Warnings.Any(w => w.Code == ToolWarnings.Settled))
        {
            added.Add(SettledWarning());
        }

        added.Add(new ToolWarning(
            ToolWarnings.ReadBackFailed,
            $"The merge of section {SourceId} into section {DestinationId} succeeded, but they couldn't be re-read afterwards ({MoveReadBack.Why(exception)}), so the copies' new IDs weren't found.",
            ReadBackUserAction()));
        return result with { PageIds = null, Warnings = [.. result.Warnings, .. added] };
    }

    /// <summary>How long the findings re-read after a cancel or the deadline may take, on its own token.</summary>
    internal static readonly TimeSpan FindingsReReadLimit = TimeSpan.FromSeconds(5);

    private Plan Planned => plan ?? throw new InvalidOperationException("Step 4 hasn't run.");

    /// <summary>Both sections, each found by its walk, and read at <c>children</c> when that read is needed.</summary>
    private async Task<State> ReadStateAsync(CancellationToken ct)
    {
        var source = await LocateAsync(SourceId, ct).ConfigureAwait(false);
        var destination = await LocateAsync(DestinationId, ct).ConfigureAwait(false);
        var protection = services.ReadAccess.Protection();

        // A protected source and a locked destination are refused anyway, so they are never read at children.
        string? sourceXml = null;
        if (source.Live is { Kind: HierarchyKind.Section } s && !protection.IsProtected(s) && destination.Live is { Kind: HierarchyKind.Section })
        {
            sourceXml = await services.Gateway.GetHierarchyAsync(SourceId, HierarchyScope.Children, ct).ConfigureAwait(false);
        }

        string? destinationXml = null;
        if (sourceXml is not null && destination.Live is { Locked: false })
        {
            destinationXml = await services.Gateway.GetHierarchyAsync(DestinationId, HierarchyScope.Children, ct).ConfigureAwait(false);
        }

        return new State(source, destination, sourceXml, destinationXml);
    }

    /// <summary>
    /// Finds <paramref name="id"/> by its walk to its notebook first, never a <c>self</c> read (3.3 S1): an unknown ID throws
    /// the gateway's <see cref="ErrorCode.ObjectNotFound"/>. A page (its first ancestor a section or page) is never read
    /// itself. Anything else is read at <c>self</c> after the walk.
    /// </summary>
    private async Task<Located> LocateAsync(string id, CancellationToken ct)
    {
        var chain = new List<HierarchyTarget>();
        var current = id;
        while (chain.Count < MaxChainDepth)
        {
            var parent = await services.Gateway.GetHierarchyParentAsync(current, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(parent))
            {
                break;
            }

            var xml = await services.Gateway.GetHierarchyAsync(parent, HierarchyScope.Self, ct).ConfigureAwait(false);
            var node = XmlReads.Readable(() => HierarchyTarget.Read(xml), "GetHierarchy");
            chain.Add(node);
            if (node.Kind == HierarchyKind.Notebook)
            {
                break;
            }

            current = parent;
        }

        var binned = chain.Any(n => n.IsRecycleBin || n.InRecycleBin);
        var notebookPath = chain.LastOrDefault(n => n.Kind == HierarchyKind.Notebook)?.Path;
        if (chain.Count > 0 && chain[0].Kind is HierarchyKind.Section or HierarchyKind.Page)
        {
            // A page (or something inside one): its kind is known from the walk.
            return new Located(null, HierarchyKind.Page, binned, notebookPath);
        }

        var selfXml = await services.Gateway.GetHierarchyAsync(id, HierarchyScope.Self, ct).ConfigureAwait(false);
        var self = XmlReads.Readable(() => HierarchyTarget.Read(selfXml), "GetHierarchy");
        return new Located(self, self.Kind, binned || self.IsRecycleBin || self.InRecycleBin, notebookPath ?? (self.Kind == HierarchyKind.Notebook ? self.Path : null));
    }

    /// <summary>The refusals of step 1 and step 4, in the plan's order; returns what the later steps use.</summary>
    private Plan Check(State now)
    {
        // AD-7: nothing in or into the recycle bin outside restore, before the kind.
        if (now.Source.Binned)
        {
            throw SourceNotFound("is the recycle bin or in it");
        }

        if (now.Destination.Binned)
        {
            throw DestinationNotFound("is the recycle bin or in it");
        }

        if (now.Source.Kind != HierarchyKind.Section)
        {
            throw HierarchyWriteSteps.Invalid($"Object {SourceId} is a {HierarchyRules.Noun(now.Source.Kind)}, not a section, so nothing was merged.", SourceUserAction);
        }

        if (now.Destination.Kind != HierarchyKind.Section)
        {
            throw HierarchyWriteSteps.Invalid($"Object {DestinationId} is a {HierarchyRules.Noun(now.Destination.Kind)}, not a section, so nothing was merged.", DestinationUserAction);
        }

        var source = now.Source.Live!;
        var destination = now.Destination.Live!;

        // AD-7 Merges, AD-21 (E8, H2): a protected source first, locked or not.
        var protection = services.ReadAccess.Protection();
        if (protection.IsProtected(source))
        {
            throw new OneNoteException(
                ErrorCode.NotSupported,
                $"Section {SourceId} {Protection.Phrase(source.EncryptedMarker)}, and merging a protected section isn't supported, so nothing was merged.",
                userAction: ProtectedUserAction,
                retryable: false);
        }

        // MergeSections answers 0x8004201B for a locked section and changes nothing (spike 3.1).
        if (destination.Locked)
        {
            throw new OneNoteException(ErrorCode.SectionLocked, $"Section {DestinationId} is locked, so section {SourceId} wasn't merged into it.");
        }

        var sourceChildren = XmlReads.Readable(() => HierarchyTarget.Read(now.SourceXml ?? throw new InvalidOperationException("The source wasn't read.")), "GetHierarchy");
        if (sourceChildren.Children.Count == 0)
        {
            // Q7 a: an unprobed call is never sent.
            throw HierarchyWriteSteps.Invalid($"Section {SourceId} has no pages, so there is nothing to merge, and nothing was merged.", EmptySourceUserAction);
        }

        var destinationXml = now.DestinationXml ?? throw new InvalidOperationException("The destination wasn't read.");
        var destinationChildren = XmlReads.Readable(() => HierarchyTarget.Read(destinationXml), "GetHierarchy");
        var cloud = CloudNotebookCreate.IsUrl(now.Source.NotebookPath) || CloudNotebookCreate.IsUrl(now.Destination.NotebookPath);
        return new Plan(sourceChildren, destinationChildren, destinationXml, protection.IsProtected(destination), cloud);
    }

    /// <summary>
    /// After an ambiguous failure: what a best-effort re-read of the destination, by its ID, finds (AD-7 Merges). IDs and
    /// counts only, and in a protected destination the count alone (AD-21 Messages).
    /// </summary>
    private async Task<string> FindingsAsync(Plan now, CancellationToken ct)
    {
        string found;
        try
        {
            var after = await HierarchyWriteSteps.ReadChildrenAsync(services.Gateway, DestinationId, ct).ConfigureAwait(false);
            var copies = NewPages(now.Destination, after);
            found = copies.Count == 0
                ? $"section {DestinationId} has no new pages"
                : now.DestinationProtected
                    ? string.Create(CultureInfo.InvariantCulture, $"section {DestinationId} has {copies.Count} new page(s)")
                    : string.Create(CultureInfo.InvariantCulture, $"section {DestinationId} has {copies.Count} new page(s) ({string.Join(", ", copies)})");
        }
#pragma warning disable CA1031 // Best effort: the original failure is what the caller gets; the re-read only adds to its message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            found = $"section {DestinationId} couldn't be re-read ({MoveReadBack.Why(ex)})";
        }

        return $"The merge may have copied some pages before it failed. Re-read straight after the failure: {found}; a copy in a cloud notebook can land later. Delete any copies it made (delete_hierarchy_object) before merging again, or the retry duplicates them.";
    }

    /// <summary>
    /// <see cref="FindingsAsync"/> on <paramref name="ct"/>, or, once a cancel or the deadline has spent it, on a token of
    /// its own limited to <see cref="FindingsReReadLimit"/>, so the findings still come.
    /// </summary>
    private async Task<string> FindingsAfterFailureAsync(Plan now, CancellationToken ct)
    {
        if (!ct.IsCancellationRequested)
        {
            return await FindingsAsync(now, ct).ConfigureAwait(false);
        }

        using var reread = new CancellationTokenSource(FindingsReReadLimit);
        return await FindingsAsync(now, reread.Token).ConfigureAwait(false);
    }

    /// <summary>The destination's pages in <paramref name="after"/> whose IDs <paramref name="before"/> didn't hold, in destination order.</summary>
    private static List<string> NewPages(HierarchyTarget before, HierarchyTarget after)
    {
        var known = before.Children.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        return [.. after.Children.Where(c => c.Kind == HierarchyKind.Page && !known.Contains(c.Id)).Select(c => c.Id)];
    }

    private ToolWarning SettledWarning() => new(
        ToolWarnings.Settled,
        string.Create(CultureInfo.InvariantCulture, $"The merge of section {SourceId} into section {DestinationId} touched a cloud notebook, so the server waited {CloudSettle.Time.TotalSeconds:0} s before re-reading them, as OneNote can fail or block on a write straight after one."),
        SettledUserAction);

    private string ReadBackUserAction() =>
        $"Read section {DestinationId} with get_hierarchy (scope children) to see the copies and their current IDs.";

    private OneNoteException SourceNotFound(string why) =>
        new(ErrorCode.ObjectNotFound, $"Object {SourceId} {why}, so nothing was merged.", userAction: SourceUserAction);

    private OneNoteException DestinationNotFound(string why) =>
        new(ErrorCode.ObjectNotFound, $"Object {DestinationId} {why}, so nothing was merged.", userAction: DestinationUserAction);

    /// <summary>A guard against a parent chain that never ends, as the access walk's.</summary>
    private const int MaxChainDepth = 64;

    /// <summary>One ID as its walk found it: the node itself when it isn't a page, its kind, whether it is the recycle bin or in it, and its notebook's path.</summary>
    private sealed record Located(HierarchyTarget? Live, HierarchyKind Kind, bool Binned, string? NotebookPath);

    /// <summary>What a read found: both IDs, and the source and destination at <c>children</c> when they were read.</summary>
    private sealed record State(Located Source, Located Destination, string? SourceXml, string? DestinationXml);

    /// <summary>What step 4 decided: both sections at <c>children</c>, the destination's XML for the snapshot, whether it is protected, and whether a cloud notebook is involved.</summary>
    private sealed record Plan(HierarchyTarget Source, HierarchyTarget Destination, string DestinationXml, bool DestinationProtected, bool TouchesCloud);
}
