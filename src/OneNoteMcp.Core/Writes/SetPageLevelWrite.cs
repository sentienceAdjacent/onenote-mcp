using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// <c>set_page_level</c> (story 3.14; E10): a page made a subpage, or a subpage made a page, as one <c>HierarchyWrite</c> that
/// sends one <c>UpdateHierarchy</c> of the page's section: every live page in its current order, each with its level, the
/// page and its subpages re-levelled by the same amount (Q2). The level is absolute, 1 to 3 (Q3).
/// <list type="number">
/// <item>Step 1, before the lock: an empty <c>pageId</c>, a level outside 1 to 3 and a basis carrying a hash (AD-17) are
/// <see cref="ErrorCode.InvalidArgument"/>. Then the walk from the page to its section, never a <c>self</c> read first,
/// because OneNote hides a locked section's pages (3.3 sitting S1); with no section above it the ID isn't a page
/// (<see cref="ErrorCode.InvalidArgument"/>). Then the write grant on the section's notebook (AD-6), then step 4's refusals
/// against a read made now, so the lock is never taken for one.</item>
/// <item value="3">Under the lock, the page is walked to its section and notebook again; a notebook other than the one step 1
/// granted is <see cref="ErrorCode.WriteNotGranted"/>, retryable (AD-7). The section is read at <c>children</c>.</item>
/// <item>Refusals, in this order: a page in the recycle bin or Deleted Pages (<see cref="ErrorCode.ObjectNotFound"/>), a
/// locked section (<see cref="ErrorCode.SectionLocked"/>, from the section's own attributes, before any page is read), a
/// password-protected section without its read grant held at the call's start (<see cref="ErrorCode.ReadNotGranted"/>, Q1
/// a; never prompted for), a page that isn't a live page of the section (<see cref="ErrorCode.ObjectNotFound"/>), and
/// <see cref="PageLevels"/>' rules (<see cref="ErrorCode.InvalidArgument"/>). The same level is <c>unchanged</c>: no basis
/// check, journal entry or COM call. The XML must pass the AD-16 gate.</item>
/// <item>The basis is the page's hierarchy <c>lastModifiedTime</c>, compared by <see cref="HierarchyBasis"/>.</item>
/// <item>One <c>hierarchy</c> journal entry of the section (AD-8); a protected section's is encrypted.</item>
/// <item>One <c>UpdateHierarchy</c>, never retried.</item>
/// </list>
/// Between 7 and 8 the section is re-read: a page gone, a level or an order other than sent is <c>readBackDiffers</c>; a
/// failed re-read is <c>readBackFailed</c>. Messages carry IDs, counts and levels only, never a name (AD-9).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SetPageLevelWrite : WriteOperation<HierarchyWriteResult>
{
    public const string OperationName = "set_page_level";

    /// <summary>What to do after <c>noBasis</c> or <c>partialBasis</c>.</summary>
    public const string NoBasisUserAction =
        "Pass the page's lastModifiedTime attribute from its section's get_hierarchy read (scope children or pages) as basis: {lastModifiedTime}, so a change made since your read is caught.";

    /// <summary>What to do after <see cref="ErrorCode.StaleWrite"/>.</summary>
    public const string StaleUserAction =
        "Re-read the page's section with get_hierarchy (scope children), check the change still makes sense, and retry with the page's current lastModifiedTime. Straight after another hierarchy write a date can move about a second late, so if nothing else changed, re-read and retry once.";

    /// <summary>What to do about an ID that isn't a live page.</summary>
    public const string PageUserAction =
        "Pass as pageId the ID of a page that isn't in the recycle bin (restore it first). Read the hierarchy (get_hierarchy) for a current ID.";

    /// <summary>What to do about a level the server refuses.</summary>
    public const string LevelUserAction =
        "Read the page's section with get_hierarchy (scope children) and pick a level from 1 to 3: the first page stays at level 1, a page can be at most one level deeper than the page before it, and a page's subpages move with it by the same amount, so none may leave 1 to 3 and the page after them may not end up more than one level deeper than the last of them. Change a neighbouring page's level first, or reorder the pages with reorder_hierarchy_children.";

    /// <summary>What to do about a password-protected section without a read grant (3.14 Q1 a).</summary>
    public const string ProtectedUserAction =
        "Changing a page's level in a password-protected section needs a read grant for that section, held when the call starts: ask the user whether to grant it (they can run the grant command with --read-section, or accept the dialog that appears when you read one of its pages with get_page_content in a client that supports it), then retry. Never run the grant command yourself.";

    /// <summary>What to do when the page's notebook changed under the lock (AD-7).</summary>
    public const string NotebookChangedUserAction =
        "The page moved to another notebook while this call ran. Re-read the hierarchy (get_hierarchy) and retry; the retry checks the write grant of the notebook it is in now.";

    private readonly HierarchyWriteServices services;
    private readonly HierarchyWalk walk;
    private readonly string correlationId;
    private readonly int level;
    private readonly PageWriteBasis? basis;
    private readonly DateTimeOffset callStart;
    private readonly List<ToolWarning> warnings = [];

    private WriteTarget? notebook;
    private string sectionXml = string.Empty;
    private Plan? plan;

    public SetPageLevelWrite(HierarchyWriteServices services, string correlationId, string pageId, int level, PageWriteBasis? basis)
    {
        ArgumentNullException.ThrowIfNull(services);
        this.services = services;
        walk = new HierarchyWalk(services.Gateway);
        this.correlationId = correlationId ?? string.Empty;
        PageId = pageId ?? string.Empty;
        this.level = level;
        this.basis = basis;
        callStart = services.ReadAccess.Now();
    }

    public override OperationClass Class => OperationClass.HierarchyWrite;

    /// <summary>The page whose level changes (<c>meta.objectId</c>).</summary>
    public string PageId { get; }

    /// <summary>The page's section (<c>meta.parentId</c>); empty until step 1 finds it.</summary>
    public string SectionId { get; private set; } = string.Empty;

    /// <summary>The journal entry step 6 stored (<c>meta.journalEntryId</c>); null until then, and when nothing changed.</summary>
    public Guid? JournalEntryId { get; private set; }

    /// <summary>The page and every page of its block leave the search page-text cache (AD-7, AD-12).</summary>
    public override IReadOnlyCollection<string> WrittenPageIds =>
        plan is { } p && p.Levels.Moved.Count > 0 ? p.Levels.Moved : PageId.Length == 0 ? [] : [PageId];

    public override string? FailureNote => JournalEntryId is { } entryId
        ? string.Create(CultureInfo.InvariantCulture, $"The section's hierarchy as it was before this change is in journal entry {entryId:D}.")
        : null;

    public override async Task AuthorizeAsync(CancellationToken ct)
    {
        // Step 1, before the lock and before any grant: the arguments (AD-7, AD-17).
        if (PageId.Length == 0)
        {
            throw HierarchyWriteSteps.Invalid("No pageId was given, so no level was changed.", PageUserAction);
        }

        if (level is < 1 or > PageLevels.Deepest)
        {
            throw HierarchyWriteSteps.Invalid(string.Create(CultureInfo.InvariantCulture, $"level {level} is outside 1 to {PageLevels.Deepest}, so no level was changed."), LevelUserAction);
        }

        HierarchyBasis.CheckArgument(basis, NoBasisUserAction);

        // The walk first, never a self read: OneNote hides a locked section's pages, while the parent chain answers (3.3 S1).
        var section = await walk.OwningSectionAsync(PageId, ct).ConfigureAwait(false)
            ?? throw HierarchyWriteSteps.Invalid($"Object {PageId} isn't a page in a section, so no level was changed.", PageUserAction);
        SectionId = section.Id;

        // D182: a loose section is refused as such first, never as locked or protected (backlog 12's answer).
        await services.WriteAccess.RefuseLooseAsync(PageId, ct).ConfigureAwait(false);

        // Phill, 2026-11-17 (3.14 Fable review): locked, then the read grant, before the write grant, so the user is never
        // shown the write-grant dialog for a call that will be refused. Only the section's own attributes are read here.
        var self = await services.Gateway.GetHierarchyAsync(section.Id, HierarchyScope.Self, ct).ConfigureAwait(false);
        CheckLockedAndReadGrant(XmlReads.Readable(() => HierarchyTarget.Read(self), "GetHierarchy"));

        notebook = await services.WriteAccess.CheckAsync(section.Id, () => PageNotFound("isn't inside a notebook"), ct, PageId).ConfigureAwait(false);

        // Step 4's refusals against a read made now, so the lock is never taken for one; step 4 checks again under it.
        var now = await services.Gateway.GetHierarchyAsync(section.Id, HierarchyScope.Children, ct).ConfigureAwait(false);
        Check(now);
    }

    public override async Task ReadAsync(CancellationToken ct)
    {
        // AD-7: the notebook re-resolved under the lock, before COM.
        var location = await walk.PageLocationAsync(PageId, ct).ConfigureAwait(false);
        var section = location.Section ?? throw PageNotFound("isn't inside a section any more");
        var key = string.IsNullOrEmpty(location.Notebook?.Path) ? null : GrantKeys.NotebookKey(location.Notebook.Path);
        if (notebook is null || key is null || !string.Equals(key, notebook.NotebookKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new OneNoteException(
                ErrorCode.WriteNotGranted,
                $"Page {PageId} is no longer in notebook {notebook?.NotebookId ?? "(unknown)"}, whose write grant this call checked, so no level was changed.",
                userAction: NotebookChangedUserAction,
                retryable: true);
        }

        SectionId = section.Id;
        sectionXml = await services.Gateway.GetHierarchyAsync(section.Id, HierarchyScope.Children, ct).ConfigureAwait(false);
    }

    public override void Validate() => plan = Check(sectionXml);

    public override Task CheckBasisAsync(CancellationToken ct)
    {
        var now = Planned;
        if (now.Levels.Unchanged)
        {
            return Task.CompletedTask;
        }

        if (HierarchyBasis.Compare(basis, now.Page.LastModifiedTime, "page", PageId, "changed", NoBasisUserAction, StaleUserAction) is { } warning)
        {
            warnings.Add(warning);
        }

        return Task.CompletedTask;
    }

    public override async Task SnapshotAsync(CancellationToken ct)
    {
        if (Planned.Levels.Unchanged)
        {
            return;
        }

        JournalEntryId = (await services.Snapshot.TakeAsync(
            new HierarchySnapshotRequest(correlationId, OperationName, OperationClass.HierarchyWrite, SectionId, sectionXml, services.ReadAccess.Protection()),
            ct).ConfigureAwait(false)).EntryId;
    }

    public override async Task<HierarchyWriteResult> CallAsync(CancellationToken ct)
    {
        var now = Planned;
        if (now.Xml is not { } xml)
        {
            // Nothing changes, so no COM call and no journal entry (as a rename to the same name, story 5.4).
            return new HierarchyWriteResult([new ToolWarning(
                ToolWarnings.Unchanged,
                string.Create(CultureInfo.InvariantCulture, $"Page {PageId} is already at level {level}, so nothing was sent to OneNote."))]);
        }

        try
        {
            await services.Gateway.UpdateHierarchyAsync(xml, ct).ConfigureAwait(false);
        }
        catch (OneNoteException ex) when (FailureNote is { } note)
        {
            // The pipeline still decides outcomeUnknown (AD-9); the message names the journal entry (2.11 Q1).
            throw new OneNoteException(ex.Code, $"{ex.Message} {note}", ex.ComHResult, ex, ex.OutcomeUnknown, ex.UserAction, ex.Retryable);
        }

        return new HierarchyWriteResult([.. warnings]);
    }

    /// <summary>Between 7 and 8: re-reads the section and compares its pages, their order and levels with what was sent.</summary>
    public override async Task<HierarchyWriteResult> AfterCallAsync(HierarchyWriteResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        var now = Planned;
        if (now.Levels.Unchanged)
        {
            return result;
        }

        var xml = await services.Gateway.GetHierarchyAsync(SectionId, HierarchyScope.Children, ct).ConfigureAwait(false);
        var after = XmlReads.Readable(() => HierarchyTarget.Read(xml), "GetHierarchy");
        var differences = ReadBackDifferences(SectionId, now.Levels.Pages, after.Children);
        return differences.Count == 0
            ? result
            : new HierarchyWriteResult([.. result.Warnings, .. differences.Select(d => new ToolWarning(ToolWarnings.ReadBackDiffers, d, ReadBackUserAction))]);
    }

    /// <summary>The read-back threw (its deadline or a cancel included): the change stands, with the warning <c>readBackFailed</c>.</summary>
    public override HierarchyWriteResult AfterCallFailed(HierarchyWriteResult result, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(exception);
        return new HierarchyWriteResult([.. result.Warnings, new ToolWarning(
            ToolWarnings.ReadBackFailed,
            $"The level change of page {PageId} succeeded, but section {SectionId} couldn't be re-read afterwards ({MoveReadBack.Why(exception)}), so what OneNote kept wasn't checked.",
            ReadBackUserAction)]);
    }

    /// <summary>
    /// The read-back's differences between the pages <paramref name="sent"/> and those <paramref name="after"/> lists: pages
    /// gone, pages at another level, and another order. IDs and counts only.
    /// </summary>
    internal static IReadOnlyList<string> ReadBackDifferences(string sectionId, IReadOnlyList<HierarchyMember> sent, IReadOnlyList<HierarchyMember> after)
    {
        var differences = new List<string>();
        var read = after.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var gone = sent.Where(p => !read.ContainsKey(p.Id)).Select(p => p.Id).ToList();
        if (gone.Count > 0)
        {
            differences.Add($"Section {sectionId} read back without {Ids(gone)}, which {(gone.Count == 1 ? "was" : "were")} sent.");
        }

        var relevelled = sent.Where(p => read.TryGetValue(p.Id, out var r) && r.PageLevel != p.PageLevel).ToList();
        if (relevelled.Count > 0)
        {
            differences.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"In section {sectionId}, {relevelled.Count} page(s) read back at another level than sent: {string.Join(", ", relevelled.Take(HierarchyWrite.MaxIdsNamed).Select(p => $"{p.Id} sent at {p.PageLevel}, read at {read[p.Id].PageLevel}"))}."));
        }

        var sentIds = sent.Select(p => p.Id).Where(read.ContainsKey).ToList();
        var readIds = after.Select(p => p.Id).Where(id => sent.Any(p => string.Equals(p.Id, id, StringComparison.Ordinal))).ToList();
        if (!sentIds.SequenceEqual(readIds, StringComparer.Ordinal))
        {
            differences.Add($"The pages of section {sectionId} read back in another order than sent.");
        }

        return differences;
    }

    private Plan Planned => plan ?? throw new InvalidOperationException("Step 4 hasn't run.");

    private static string ReadBackUserAction => "Read the page's section with get_hierarchy (scope children) to see the levels OneNote kept.";

    /// <summary>
    /// Step 4's refusals, in the plan's order, against <paramref name="childrenXml"/> (the page's section at
    /// <c>children</c>); returns what the later steps use. Step 1 runs it too.
    /// </summary>
    private Plan Check(string childrenXml)
    {
        var section = XmlReads.Readable(() => HierarchyTarget.Read(childrenXml), "GetHierarchy");
        if (section.Kind != HierarchyKind.Section)
        {
            throw PageNotFound("isn't inside a section any more");
        }

        // AD-7: the recycle bin is restore's only.
        if (HierarchyRules.RecycleBinProblem(section.IsRecycleBin, section.InRecycleBin) is not null)
        {
            throw PageNotFound("is in the recycle bin");
        }

        CheckLockedAndReadGrant(section);

        var page = section.Children.Any(c => string.Equals(c.Id, PageId, StringComparison.Ordinal))
            ? XmlReads.Readable(() => HierarchyTarget.Page(childrenXml, PageId), "GetHierarchy")
            : null;
        if (page is null)
        {
            throw PageNotFound($"isn't a live page of section {section.Id}");
        }

        var levels = PageLevels.Change(section.Children, PageId, level);
        if (levels.Problem != PageLevelProblem.None)
        {
            throw HierarchyWriteSteps.Invalid(Refusal(levels, section.Children), LevelUserAction);
        }

        if (levels.Unchanged)
        {
            return new Plan(page, levels, null);
        }

        var xml = HierarchyUpdate.Reorder(HierarchyKind.Section, section.Id, levels.Pages);

        // AD-16: the hard gate for UpdateHierarchy. The XML is ours, so a failure is our bug, never sent.
        HierarchyWriteSteps.EnsureSchemaValid(xml);

        return new Plan(page, levels, xml);
    }

    /// <summary>The message for a <see cref="PageLevels"/> refusal: IDs and levels only.</summary>
    /// <summary>
    /// From the section's own attributes, before any page is read: locked first (3.14 design note), then in a protected
    /// section the read grant held at the call's start, never prompted for (Q1 a). Step 1 runs it before the write grant
    /// (Phill, 2026-11-17), step 4 again under the lock.
    /// </summary>
    private void CheckLockedAndReadGrant(HierarchyTarget section)
    {
        if (section.Locked)
        {
            throw new OneNoteException(ErrorCode.SectionLocked, $"Section {section.Id} is locked, so the level of page {PageId} wasn't changed.");
        }

        if (services.ReadAccess.Protection().IsProtected(section) && !services.ReadAccess.HoldsReadGrantGivenBy(GrantKeys.SectionKey(section.Path), callStart))
        {
            throw new OneNoteException(
                ErrorCode.ReadNotGranted,
                $"Section {section.Id} {Protection.Phrase(section.EncryptedMarker)}, and changing a page's level in it needs a read grant for it held when the call starts, so no level was changed.",
                userAction: ProtectedUserAction);
        }
    }

    private string Refusal(PageLevelResult levels, IReadOnlyList<HierarchyMember> pages)
    {
        int LevelOf(string? id) => pages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal))?.PageLevel ?? 0;
        var index = pages.ToList().FindIndex(p => string.Equals(p.Id, PageId, StringComparison.Ordinal));
        var delta = level - LevelOf(PageId);
        return levels.Problem switch
        {
            PageLevelProblem.FirstPage =>
                $"Page {PageId} is the first page of its section, so its level can only be 1, and no level was changed.",
            PageLevelProblem.GapBefore => string.Create(
                CultureInfo.InvariantCulture,
                $"Level {level} would put page {PageId} more than one level deeper than the page before it (at level {pages[index - 1].PageLevel}), so no level was changed."),
            PageLevelProblem.SubpageOutOfRange => string.Create(
                CultureInfo.InvariantCulture,
                $"Page {PageId}'s subpages move with it, and subpage {levels.Offender} would go to level {LevelOf(levels.Offender) + delta}, outside 1 to {PageLevels.Deepest}, so no level was changed."),
            PageLevelProblem.GapAfter => string.Create(
                CultureInfo.InvariantCulture,
                $"Page {levels.Offender} (at level {LevelOf(levels.Offender)}), after page {PageId} and its subpages, would end up more than one level deeper than the page before it, so no level was changed."),
            _ => string.Create(CultureInfo.InvariantCulture, $"Page {PageId} can't go to level {level} ({levels.Problem}), so no level was changed."),
        };
    }

    private static string Ids(IReadOnlyList<string> ids) =>
        ids.Count > HierarchyWrite.MaxIdsNamed
            ? string.Create(CultureInfo.InvariantCulture, $"{string.Join(", ", ids.Take(HierarchyWrite.MaxIdsNamed))} and {ids.Count - HierarchyWrite.MaxIdsNamed} more")
            : string.Join(", ", ids);

    private OneNoteException PageNotFound(string why) =>
        new(ErrorCode.ObjectNotFound, $"Page {PageId} {why}, so no level was changed.", userAction: PageUserAction);

    /// <summary>What step 4 decided: the page as read, the levels to send, and the XML (null when nothing changes).</summary>
    private sealed record Plan(SectionPage Page, PageLevelResult Levels, string? Xml);
}
