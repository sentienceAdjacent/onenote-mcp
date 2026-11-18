using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Writes;

/// <summary>What a page move reports: the page's new ID when the read-back found it, and its warnings in step order.</summary>
public sealed record MoveResult(string? NewPageId, IReadOnlyList<ToolWarning> Warnings);

/// <summary>
/// <c>move_hierarchy_object</c> for a page (story 3.4; AD-5, AD-7 Moves, AD-14): one <c>HierarchyWrite</c> that sends one
/// <c>UpdateHierarchy</c> of the target section's XML with the page inserted (spike 3.1 M1 shape a). OneNote gives the
/// moved page a new ID and leaves its subpages in the source, promoted to level 1. Sections and section groups wait for
/// story 3.12.
/// <list type="number">
/// <item>Step 1, before the lock: an empty <c>objectId</c> or <c>parentId</c>, a <c>position</c> below 1 and a basis with a
/// hash are <see cref="ErrorCode.InvalidArgument"/>. Then the walk from the page to its section, never a <c>self</c> read
/// first, because OneNote hides a locked section's pages (3.3 sitting S1); with no section the object is read at
/// <c>self</c>: a section or section group is <see cref="ErrorCode.NotSupported"/>, anything else
/// <see cref="ErrorCode.InvalidArgument"/>. Then the write grant on the page's notebook and on the target's (AD-6; the
/// pipeline asks once per notebook, 3.4 Q3), then step 4's refusals against a read made now, so the lock is never taken
/// for one.</item>
/// <item value="3">Under the lock, both notebooks are re-resolved (AD-7): the page walked to its section and notebook, the
/// target to its notebook. A <c>NotebookKey</c> other than step 1's is <see cref="ErrorCode.WriteNotGranted"/>, retryable,
/// before COM. The source section and the target are read at <c>children</c> scope.</item>
/// <item>Refusals, in this order: a protected source (<see cref="ErrorCode.NotSupported"/>, AD-21), a page or target in the
/// recycle bin (<see cref="ErrorCode.ObjectNotFound"/>), a target that isn't a section
/// (<see cref="ErrorCode.InvalidArgument"/>), a locked target (<see cref="ErrorCode.SectionLocked"/>), the page's own
/// section as target, a position in a protected target without a read grant held at the call's start
/// (<see cref="ErrorCode.ReadNotGranted"/>), and a position outside the list or inside a subpage block
/// (<see cref="ErrorCode.InvalidArgument"/>, with no page count in the message).
/// The XML must pass the AD-16 gate.</item>
/// <item>The basis is the page's hierarchy <c>lastModifiedTime</c> (AD-17), compared as renames compare it
/// (<see cref="HierarchyBasis"/>).</item>
/// <item>One <c>hierarchy</c> journal entry per section, source then target (AD-8); a protected target's is encrypted.</item>
/// <item>One <c>UpdateHierarchy</c>. <c>0x80042004</c> keeps <see cref="ErrorCode.ObjectNotFound"/> and sets
/// <c>outcomeUnknown</c> (AD-9): both sections are re-read first, best effort, and the message says what was found.</item>
/// </list>
/// Between 7 and 8, both sections are re-read: the moved page is the one page in the target whose ID wasn't there before,
/// preferring the sent name. Messages carry IDs and counts only, never a name (AD-9).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MovePageWrite : WriteOperation<MoveResult>
{
    public const string OperationName = "move_hierarchy_object";

    /// <summary>hrSectionDoesNotExist (and friends): on a move it may have left a partial copy (AD-7 Moves, AD-9).</summary>
    public const int PartialCopyHResult = unchecked((int)0x80042004);

    /// <summary>What to do after <c>noBasis</c> or <c>partialBasis</c>.</summary>
    public const string NoBasisUserAction =
        "Pass the page's lastModifiedTime attribute from its section's get_hierarchy read (scope children or pages) as basis: {lastModifiedTime}, so a change made since your read is caught.";

    /// <summary>What to do after <see cref="ErrorCode.StaleWrite"/>.</summary>
    public const string StaleUserAction =
        "Re-read the page's section with get_hierarchy (scope children), check the move still makes sense, and retry with the page's current lastModifiedTime. Straight after another hierarchy write a date can move about a second late, so if nothing else changed, re-read and retry once.";

    /// <summary>What to do about a page ID that isn't a live page.</summary>
    public const string PageUserAction =
        "Pass as objectId the ID of a page that isn't in the recycle bin (restore it first). Read the hierarchy (get_hierarchy) for a current ID.";

    /// <summary>What to do about a target that can't take a page.</summary>
    public const string TargetUserAction =
        "Pass as parentId the ID of a section that isn't in the recycle bin: a page moves into a section. Read the hierarchy (get_hierarchy) for a current ID.";

    /// <summary>What to do about a page in a password-protected section (AD-7, AD-21; epic 3 Q9).</summary>
    public const string ProtectedUserAction =
        "Ask the user to move the page in OneNote: a page in a password-protected section isn't moved through this server, because the move could take it out of the section's encryption.";

    /// <summary>What to do about a position below 1, before the object's kind is known (story 3.12).</summary>
    public const string AnyPositionUserAction =
        "Pass a position from 1: among the target section's pages for a page, among the new parent's sections or section groups for a section or group; or leave position out to put the item last.";

    /// <summary>What to do about a basis the server refuses, before the object's kind is known (story 3.12).</summary>
    public const string AnyBasisUserAction =
        "Pass only the item's lastModifiedTime attribute, unchanged from a get_hierarchy read (a page's from its section's children), as basis: {lastModifiedTime}; or leave basis out.";

    /// <summary>What to do when the target is the page's own section.</summary>
    public const string SameSectionUserAction =
        "The page is already in this section. To change its place there, use reorder_hierarchy_children on the section.";

    /// <summary>What to do about a position the server refuses.</summary>
    public const string PositionUserAction =
        "Read the target section with get_hierarchy (scope children) and pass a position from 1 to one more than its page count, at a top-level page rather than a subpage; or leave position out to put the page last.";

    /// <summary>
    /// What to do about a <c>position</c> in a password-protected target without a read grant (3.4 security review, Phill
    /// 2026-11-17): a position's checks would show the section's page count and where its subpages sit.
    /// </summary>
    public const string ProtectedPositionUserAction =
        "Leave position out to put the page last in the password-protected section. Placing it at a position needs a read grant for that section, held when the call starts: ask the user whether to grant it (they can run the grant command with --read-section, or accept the dialog that appears when you read one of its pages with get_page_content in a client that supports it), then retry. Never run the grant command yourself.";

    /// <summary>What to do when a notebook changed under the lock (AD-7).</summary>
    public const string NotebookChangedUserAction =
        "The page or the target section moved to another notebook while this call ran. Re-read the hierarchy (get_hierarchy) and retry; the retry checks the write grant of the notebook each is in now.";

    private readonly HierarchyWriteServices services;
    private readonly HierarchyWalk walk;
    private readonly string correlationId;
    private readonly int? position;
    private readonly PageWriteBasis? basis;
    private readonly DateTimeOffset callStart;
    private readonly List<ToolWarning> warnings = [];

    private WriteTarget? pageNotebook;
    private WriteTarget? targetNotebook;
    private string sourceXml = string.Empty;
    private string targetXml = string.Empty;
    private Plan? plan;

    public MovePageWrite(HierarchyWriteServices services, string correlationId, string objectId, string parentId, int? position, PageWriteBasis? basis)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;
        walk = new HierarchyWalk(services.Gateway);
        this.correlationId = correlationId ?? string.Empty;
        PageId = objectId ?? string.Empty;
        TargetId = parentId ?? string.Empty;
        this.position = position;
        this.basis = basis;
        callStart = services.ReadAccess.Now();
    }

    public override OperationClass Class => OperationClass.HierarchyWrite;

    /// <summary>Step 3 re-resolves both notebooks itself, the page walked from the page (AD-7).</summary>
    public override bool ReResolvesNotebooks => true;

    /// <summary>The page moved (its ID before the move).</summary>
    public string PageId { get; }

    /// <summary>The section the page moves to (<c>meta.parentId</c>).</summary>
    public string TargetId { get; }

    /// <summary>The source section's journal entry (<c>meta.journalEntryId</c>); null until step 6 stores it.</summary>
    public Guid? SourceEntryId { get; private set; }

    /// <summary>The target section's journal entry; null until step 6 stores it.</summary>
    public Guid? TargetEntryId { get; private set; }

    /// <summary>The journal entries step 6 stored, source then target (<c>meta.journalEntryIds</c>, 3.4 Q1).</summary>
    public IReadOnlyList<Guid> JournalEntryIds => [.. new[] { SourceEntryId, TargetEntryId }.OfType<Guid>()];

    /// <summary>The old page ID is dropped from the search page-text cache (AD-7, AD-12).</summary>
    public override IReadOnlyCollection<string> WrittenPageIds => PageId.Length == 0 ? [] : [PageId];

    public override string? FailureNote => (SourceEntryId, TargetEntryId) switch
    {
        ({ } source, { } target) => string.Create(CultureInfo.InvariantCulture, $"Both sections' hierarchy as it was before this move is in journal entries {source:D} (source) and {target:D} (target)."),
        ({ } source, null) => string.Create(CultureInfo.InvariantCulture, $"The source section's hierarchy as it was before this move is in journal entry {source:D}."),
        _ => null,
    };

    public override async Task AuthorizeAsync(CancellationToken ct)
    {
        // Step 1, before the lock and before any grant: the arguments (AD-7, AD-17).
        if (PageId.Length == 0)
        {
            throw Invalid("No objectId was given, so nothing was moved.", PageUserAction);
        }

        if (TargetId.Length == 0)
        {
            throw Invalid("No parentId was given, so nothing was moved.", TargetUserAction);
        }

        if (position is < 1)
        {
            throw Invalid(string.Create(CultureInfo.InvariantCulture, $"position {position} is below 1, so nothing was moved."), AnyPositionUserAction);
        }

        // Before the walk tells a page from a section or group, so these two say what fits either (3.12 review).
        HierarchyBasis.CheckArgument(basis, AnyBasisUserAction);

        // The walk first, never a self read: OneNote hides a locked section's pages, while the parent chain answers (3.3 S1).
        var section = await walk.OwningSectionAsync(PageId, ct).ConfigureAwait(false)
            ?? throw await NotAPageAsync(ct).ConfigureAwait(false);

        // Phill, 2026-11-17 (3.14 Fable review): with a position, a locked target, then a protected target's read grant,
        // before any write grant, so the user is never shown the write-grant dialog for a move that will be refused.
        if (position is not null)
        {
            var targetSelf = await services.Gateway.GetHierarchyAsync(TargetId, HierarchyScope.Self, ct).ConfigureAwait(false);
            var target = XmlReads.Readable(() => HierarchyTarget.Read(targetSelf), "GetHierarchy");
            if (target.Kind == HierarchyKind.Section)
            {
                if (target.Locked)
                {
                    throw new OneNoteException(ErrorCode.SectionLocked, $"Section {TargetId} is locked, so page {PageId} wasn't moved.");
                }

                CheckPositionReadGrant(target);
            }
        }

        // AD-6: both notebooks' write grants, the page's first; the pipeline asks once per notebook (3.4 Q3).
        pageNotebook = await services.WriteAccess.CheckAsync(section.Id, () => PageNotFound("isn't inside a notebook"), ct).ConfigureAwait(false);
        targetNotebook = await services.WriteAccess.CheckContainerAsync(TargetId, () => BadTarget("isn't inside a notebook"), ct).ConfigureAwait(false);

        // Step 4's refusals against a read made now, so the lock is never taken for one; step 4 checks again under it.
        var sourceNow = await services.Gateway.GetHierarchyAsync(section.Id, HierarchyScope.Children, ct).ConfigureAwait(false);
        var targetNow = await services.Gateway.GetHierarchyAsync(TargetId, HierarchyScope.Children, ct).ConfigureAwait(false);
        Check(sourceNow, targetNow);
    }

    public override async Task ReadAsync(CancellationToken ct)
    {
        // AD-7: both notebooks re-resolved under the lock, before COM (story 3.4 builds it for the move).
        var location = await walk.PageLocationAsync(PageId, ct).ConfigureAwait(false);
        var section = location.Section
            ?? throw PageNotFound("isn't inside a section any more");
        targetXml = await services.Gateway.GetHierarchyAsync(TargetId, HierarchyScope.Children, ct).ConfigureAwait(false);
        var targetNode = await walk.OwningNotebookAsync(TargetId, ct).ConfigureAwait(false);
        var targetPath = targetNode?.Path;
        if (targetNode is null)
        {
            var self = XmlReads.Readable(() => HierarchyTarget.Read(targetXml), "GetHierarchy");
            targetPath = self.Kind == HierarchyKind.Notebook ? self.Path : null;
        }

        CheckNotebook("Page", PageId, location.Notebook?.Path, pageNotebook);
        CheckNotebook("Target", TargetId, targetPath, targetNotebook);

        sourceXml = await services.Gateway.GetHierarchyAsync(section.Id, HierarchyScope.Children, ct).ConfigureAwait(false);
    }

    public override void Validate() => plan = Check(sourceXml, targetXml);

    public override Task CheckBasisAsync(CancellationToken ct)
    {
        var now = Planned;
        if (HierarchyBasis.Compare(basis, now.Page.LastModifiedTime, "page", PageId, "moved", NoBasisUserAction, StaleUserAction) is { } warning)
        {
            warnings.Add(warning);
        }

        return Task.CompletedTask;
    }

    public override async Task SnapshotAsync(CancellationToken ct)
    {
        // AD-8: a hierarchy entry per section, source then target. No page snapshot: no page move lost a page (AD-7).
        var now = Planned;
        SourceEntryId = (await services.Snapshot.TakeAsync(
            new HierarchySnapshotRequest(correlationId, OperationName, OperationClass.HierarchyWrite, now.Source.Id, sourceXml, services.ReadAccess.Protection()),
            ct).ConfigureAwait(false)).EntryId;
        TargetEntryId = (await services.Snapshot.TakeAsync(
            new HierarchySnapshotRequest(correlationId, OperationName, OperationClass.HierarchyWrite, now.Target.Id, targetXml, services.ReadAccess.Protection()),
            ct).ConfigureAwait(false)).EntryId;
    }

    public override async Task<MoveResult> CallAsync(CancellationToken ct)
    {
        var now = Planned;
        try
        {
            await services.Gateway.UpdateHierarchyAsync(now.Xml, ct).ConfigureAwait(false);
        }
        catch (OneNoteException ex) when (ex.ComHResult == PartialCopyHResult)
        {
            // AD-7 Moves: a partial copy is possible, so the outcome is unknown; the code stays (AD-9). The re-read runs here,
            // under the lock and before the throw, so the pipeline needs no branch of its own.
            var found = await PartialCopyFindingsAsync(now, ct).ConfigureAwait(false);
            throw new OneNoteException(ex.Code, WithNote($"{ex.Message} {found}"), ex.ComHResult, ex, outcomeUnknown: true, ex.UserAction, retryable: false);
        }
        catch (OneNoteException ex) when (FailureNote is not null)
        {
            // The pipeline still decides outcomeUnknown (AD-9); the message names the journal entries (2.11 Q1).
            throw new OneNoteException(ex.Code, WithNote(ex.Message), ex.ComHResult, ex, ex.OutcomeUnknown, ex.UserAction, ex.Retryable);
        }

        var result = new List<ToolWarning>(warnings);
        if (now.Page.Subpages > 0)
        {
            result.Add(new ToolWarning(
                ToolWarnings.SubpagesLeftBehind,
                string.Create(CultureInfo.InvariantCulture, $"Page {PageId} had {now.Page.Subpages} subpage(s); OneNote moves a page without them, so they stay in section {now.Source.Id}, promoted to level 1."),
                "Tell the user. To move them as well, move each subpage with move_hierarchy_object: it lands as a top-level page."));
        }

        return new MoveResult(null, result);
    }

    /// <summary>Between 7 and 8: re-reads both sections and finds the moved page's new ID (AD-7 Moves read-back).</summary>
    public override async Task<MoveResult> AfterCallAsync(MoveResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        var now = Planned;
        var sourceAfter = await ReadChildrenAsync(now.Source.Id, ct).ConfigureAwait(false);
        var targetAfter = await ReadChildrenAsync(now.Target.Id, ct).ConfigureAwait(false);
        var newId = MovedPage(now, targetAfter);
        var stillInSource = sourceAfter.Children.Any(c => string.Equals(c.Id, PageId, StringComparison.Ordinal));

        var differences = new List<ToolWarning>();
        if (newId is null)
        {
            differences.Add(new ToolWarning(
                ToolWarnings.ReadBackDiffers,
                $"Section {now.Target.Id} read back with no new page in it, so the move of page {PageId} wasn't confirmed.",
                ReadBackUserAction(now.Target.Id)));
        }

        if (stillInSource)
        {
            differences.Add(new ToolWarning(
                ToolWarnings.ReadBackDiffers,
                $"Page {PageId} still reads back in section {now.Source.Id}.",
                ReadBackUserAction(now.Target.Id)));
        }

        return new MoveResult(newId ?? (stillInSource ? PageId : null), [.. result.Warnings, .. differences]);
    }

    /// <summary>The read-back threw (its deadline or a cancel included): the move stands, with the warning <c>readBackFailed</c>.</summary>
    public override MoveResult AfterCallFailed(MoveResult result, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(exception);
        return result with
        {
            Warnings = [.. result.Warnings, new ToolWarning(
                ToolWarnings.ReadBackFailed,
                $"The move of page {PageId} to section {TargetId} succeeded, but the sections couldn't be re-read afterwards ({Why(exception)}), so the page's new ID wasn't found.",
                ReadBackUserAction(TargetId))],
        };
    }

    private Plan Planned => plan ?? throw new InvalidOperationException("Step 4 hasn't run.");

    /// <summary>
    /// Step 4's refusals, in the plan's order, against <paramref name="sourceSectionXml"/> (the page's section at
    /// <c>children</c>) and <paramref name="targetChildrenXml"/>; returns what the later steps use. Step 1 runs it too.
    /// </summary>
    private Plan Check(string sourceSectionXml, string targetChildrenXml)
    {
        var source = XmlReads.Readable(() => HierarchyTarget.Read(sourceSectionXml), "GetHierarchy");
        var target = XmlReads.Readable(() => HierarchyTarget.Read(targetChildrenXml), "GetHierarchy");

        // AD-7, AD-21 (epic 3 Q9): a protected source first, locked or not: the move could carry the page out of its encryption.
        if (services.ReadAccess.Protection().IsProtected(source))
        {
            throw new OneNoteException(
                ErrorCode.NotSupported,
                $"Page {PageId} is in section {source.Id}, which {Protection.Phrase(source.EncryptedMarker)}, and a move out of a protected section isn't supported, so nothing was moved.",
                userAction: ProtectedUserAction,
                retryable: false);
        }

        // AD-7: the recycle bin is restore's only.
        if (HierarchyRules.RecycleBinProblem(source.IsRecycleBin, source.InRecycleBin) is not null)
        {
            throw PageNotFound("is in the recycle bin");
        }

        var listed = source.Kind == HierarchyKind.Section
            && source.Children.Any(c => string.Equals(c.Id, PageId, StringComparison.Ordinal));
        var page = listed ? XmlReads.Readable(() => HierarchyTarget.Page(sourceSectionXml, PageId), "GetHierarchy") : null;
        if (page is null)
        {
            throw PageNotFound(string.Create(CultureInfo.InvariantCulture, $"isn't a live page of section {source.Id}"));
        }

        if (HierarchyRules.RecycleBinProblem(target.IsRecycleBin, target.InRecycleBin) is { } binned)
        {
            throw BadTargetNotFound(binned);
        }

        if (target.Kind != HierarchyKind.Section)
        {
            throw Invalid(
                $"Object {TargetId} is a {HierarchyRules.Noun(target.Kind)}, not a section, and a page moves into a section, so nothing was moved.",
                TargetUserAction);
        }

        if (target.Locked)
        {
            throw new OneNoteException(ErrorCode.SectionLocked, $"Section {TargetId} is locked, so page {PageId} wasn't moved.");
        }

        if (string.Equals(target.Id, source.Id, StringComparison.Ordinal))
        {
            throw Invalid($"Page {PageId} is already in section {TargetId}, so nothing was moved.", SameSectionUserAction);
        }

        // 3.4 security review (Phill, 2026-11-17): a position's refusals would show a protected section's page count and where
        // its subpages sit, and a move into one needs only the write grant, so without a read grant held at the call's start
        // only a missing position (last) is accepted (AD-21).
        CheckPositionReadGrant(target);

        // 3.4 Q2: 1-based among the target's live pages, before the page now there, which must be top-level; omitted, last.
        // No message carries the page count (3.4 security review).
        var count = target.Children.Count;
        var at = position ?? count + 1;
        if (at > count + 1)
        {
            throw Invalid(
                string.Create(CultureInfo.InvariantCulture, $"position {at} is past the end of section {TargetId}, so nothing was moved."),
                PositionUserAction);
        }

        if (at <= count && target.Children[at - 1].PageLevel > 1)
        {
            throw Invalid(
                string.Create(CultureInfo.InvariantCulture, $"position {at} in section {TargetId} is inside a subpage block (the page there is a subpage), so nothing was moved."),
                PositionUserAction);
        }

        var xml = HierarchyUpdate.MovePage(target.Id, target.Children, PageId, at - 1);

        // AD-16: the hard gate for UpdateHierarchy. The XML is ours, so a failure is our bug, never sent.
        var problems = SchemaValidation.Problems(xml);
        if (problems.Count > 0)
        {
            throw new OneNoteException(
                ErrorCode.Internal,
                string.Create(CultureInfo.InvariantCulture, $"The server built UpdateHierarchy XML that the 2013 schema refuses ({problems.Count} problem(s), the first at {problems[0].DisplayName ?? "the document"}), so nothing was sent."));
        }

        return new Plan(source, target, page, at - 1, xml);
    }

    /// <summary>
    /// A <c>position</c> in a password-protected target needs its read grant held at the call's start, never prompted for
    /// (3.4 security review). Step 1 checks it before the write grants (Phill, 2026-11-17), step 4 again under the lock.
    /// </summary>
    private void CheckPositionReadGrant(HierarchyTarget target)
    {
        if (position is not null && services.ReadAccess.Protection().IsProtected(target) && !services.ReadAccess.HoldsReadGrantGivenBy(GrantKeys.SectionKey(target.Path), callStart))
        {
            throw new OneNoteException(
                ErrorCode.ReadNotGranted,
                $"Section {TargetId} {Protection.Phrase(target.EncryptedMarker)}, and placing a page at a position in it needs a read grant for it held when the call starts, so nothing was moved.",
                userAction: ProtectedPositionUserAction);
        }
    }

    /// <summary>Step 3's compare (AD-7): <paramref name="path"/>'s key must be the one step 1 granted.</summary>
    private static void CheckNotebook(string what, string id, string? path, WriteTarget? granted)
    {
        var key = string.IsNullOrEmpty(path) ? null : GrantKeys.NotebookKey(path);
        if (granted is null || key is null || !string.Equals(key, granted.NotebookKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new OneNoteException(
                ErrorCode.WriteNotGranted,
                $"{what} {id} is no longer in notebook {granted?.NotebookId ?? "(unknown)"}, whose write grant this call checked, so nothing was moved.",
                userAction: NotebookChangedUserAction,
                retryable: true);
        }
    }

    /// <summary>
    /// The moved page in <paramref name="after"/>: the one page whose ID the target didn't hold before, preferring the
    /// sent name, then the sent position; null when there is none.
    /// </summary>
    private static string? MovedPage(Plan now, HierarchyTarget after)
    {
        var before = now.Target.Children.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var added = after.Children.Where(c => !before.Contains(c.Id)).ToList();
        var named = added.Where(c => string.Equals(c.Name, now.Page.Name, StringComparison.Ordinal)).ToList();
        var candidates = named.Count > 0 ? named : added;
        if (candidates.Count <= 1)
        {
            return candidates.SingleOrDefault()?.Id;
        }

        var atSent = now.Index < after.Children.Count ? after.Children[now.Index] : null;
        return atSent is not null && candidates.Contains(atSent) ? atSent.Id : candidates[0].Id;
    }

    /// <summary>After <c>0x80042004</c>: where the page is, as far as a best-effort re-read of both sections can tell (AD-7 Moves).</summary>
    private async Task<string> PartialCopyFindingsAsync(Plan now, CancellationToken ct)
    {
        string inSource;
        try
        {
            var source = await ReadChildrenAsync(now.Source.Id, ct).ConfigureAwait(false);
            inSource = source.Children.Any(c => string.Equals(c.Id, PageId, StringComparison.Ordinal))
                ? $"page {PageId} is still in section {now.Source.Id}"
                : $"page {PageId} is no longer in section {now.Source.Id}";
        }
#pragma warning disable CA1031 // Best effort: the original failure is what the caller gets; the re-read only adds to its message.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            inSource = $"section {now.Source.Id} couldn't be re-read ({Why(ex)})";
        }

        string inTarget;
        try
        {
            var target = await ReadChildrenAsync(now.Target.Id, ct).ConfigureAwait(false);
            var before = now.Target.Children.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            var copies = target.Children
                .Where(c => !before.Contains(c.Id) && string.Equals(c.Name, now.Page.Name, StringComparison.Ordinal))
                .Select(c => c.Id)
                .ToList();
            // In a password-protected target, a count only: nothing inside it is named (AD-21 Messages).
            inTarget = copies.Count == 0
                ? $"section {now.Target.Id} has no new page with its name"
                : services.ReadAccess.Protection().IsProtected(now.Target)
                    ? string.Create(CultureInfo.InvariantCulture, $"section {now.Target.Id} has {copies.Count} new page(s) with its name")
                    : string.Create(CultureInfo.InvariantCulture, $"section {now.Target.Id} has {copies.Count} new page(s) with its name ({string.Join(", ", copies)})");
        }
#pragma warning disable CA1031 // As above.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            inTarget = $"section {now.Target.Id} couldn't be re-read ({Why(ex)})";
        }

        return $"The move may have left a partial copy. Re-read straight after the failure: {inSource}, and {inTarget}; a copy in a cloud notebook can land later.";
    }

    private async Task<HierarchyTarget> ReadChildrenAsync(string sectionId, CancellationToken ct)
    {
        var xml = await services.Gateway.GetHierarchyAsync(sectionId, HierarchyScope.Children, ct).ConfigureAwait(false);
        return XmlReads.Readable(() => HierarchyTarget.Read(xml), "GetHierarchy");
    }

    /// <summary>
    /// With no section above it, the object isn't a page: a section or group is <see cref="ObjectIsContainer"/>, which the
    /// use case answers with <see cref="MoveContainerWrite"/> (story 3.12); anything else is refused.
    /// </summary>
    private async Task<OneNoteException> NotAPageAsync(CancellationToken ct)
    {
        var xml = await services.Gateway.GetHierarchyAsync(PageId, HierarchyScope.Self, ct).ConfigureAwait(false);
        var self = XmlReads.Readable(() => HierarchyNode.Root(xml), "GetHierarchy");
        return self.Kind is HierarchyKind.Section or HierarchyKind.SectionGroup
            ? new ObjectIsContainer(PageId, self.Kind)
            : Invalid($"Object {PageId} is a {HierarchyRules.Noun(self.Kind)}, not a page, section or section group, so nothing was moved.", PageUserAction);
    }

    private string WithNote(string message) => FailureNote is { } note ? $"{message} {note}" : message;

    private OneNoteException PageNotFound(string why) =>
        new(ErrorCode.ObjectNotFound, $"Page {PageId} {why}, so nothing was moved.", userAction: PageUserAction);

    private OneNoteException BadTargetNotFound(string why) =>
        new(ErrorCode.ObjectNotFound, $"Object {TargetId} {why}, so nothing was moved.", userAction: TargetUserAction);

    private OneNoteException BadTarget(string why) =>
        Invalid($"Object {TargetId} {why}, so nothing was moved.", TargetUserAction);

    private static OneNoteException Invalid(string message, string userAction) =>
        new(ErrorCode.InvalidArgument, message, userAction: userAction, retryable: false);

    private static string ReadBackUserAction(string targetId) =>
        $"Read section {targetId} with get_hierarchy (scope children) to find the moved page and its current ID.";

    private static string Why(Exception exception) => exception switch
    {
        OneNoteException one => one.Code.ToString(),
        OperationCanceledException => "the write's deadline or a cancel",
        _ => exception.GetType().Name,
    };

    /// <summary>What step 4 decided: both sections as read, the page, the 0-based index it goes in at, and the XML to send.</summary>
    private sealed record Plan(HierarchyTarget Source, HierarchyTarget Target, SectionPage Page, int Index, string Xml);
}

/// <summary>
/// Step 1 of a page move found a section or section group (story 3.12): nothing was granted, locked or read beyond the
/// walk, and <see cref="UseCases.MoveHierarchyObjectUseCase"/> runs <see cref="MoveContainerWrite"/> instead. Uncaught,
/// it reaches the tool runner as an ordinary <see cref="ErrorCode.InvalidArgument"/>. IDs only (AD-9).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ObjectIsContainer(string objectId, HierarchyKind kind)
    : OneNoteException(ErrorCode.InvalidArgument, $"Object {objectId} is a {UseCases.HierarchyRules.Noun(kind)}, not a page, so nothing was moved.", userAction: MovePageWrite.PageUserAction)
{
    public HierarchyKind Kind { get; } = kind;
}
