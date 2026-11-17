using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>CreateNewPage</c>: adds an empty page to a section, through the write pipeline (AD-7) as a <c>Create</c>. Step 1
/// needs the write grant of the section's notebook (AD-6); in a password-protected section that grant alone suffices,
/// and no read-grant dialog is ever raised (AD-21). Step 3 reads the section at <c>self</c>; step 4 refuses an ID that
/// isn't a section, or a section in the recycle bin, with <see cref="ErrorCode.ObjectNotFound"/> (Phill, 2026-11-10, 2.4
/// plan Q8), and a locked section with <see cref="ErrorCode.SectionLocked"/>, before COM. The new page's ID is
/// <c>meta.pageId</c> (Q2). It is returned in a protected section too: the model made the page, and the ID reveals
/// nothing inside the section. Messages carry IDs only (AD-9).
/// <para>
/// With <c>parentPageId</c> (story 3.14; E10, Q5): the new page becomes a subpage of that page, which must be a live page of
/// the section below level 3, else <see cref="ErrorCode.InvalidArgument"/> before COM. In a protected section the call also
/// needs the section's read grant, held at the call's start and never prompted for (Q1 a), else
/// <see cref="ErrorCode.ReadNotGranted"/>. Step 6 takes a <c>hierarchy</c> journal entry of the section
/// (<c>meta.journalEntryId</c>), and step 7, after <c>CreateNewPage</c> and still under the lock, sends one
/// <c>UpdateHierarchy</c> that puts the new page just after the parent's subpages at the parent's level plus one. If that
/// placement fails for any reason the page stands, top-level and last, with the warning <c>subpageNotPlaced</c> (Q4). An
/// empty <c>parentPageId</c> counts as none.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CreatePageUseCase(IOneNoteGateway gateway, WriteAccess access, WritePipeline pipeline, ReadAccess? readAccess, HierarchySnapshot? snapshot) : IUseCase
{
    public const string OperationName = "create_page";

    /// <summary>What to do when <c>sectionId</c> isn't a live section's ID.</summary>
    public const string NotASectionUserAction =
        "create_page needs the ID of a section that isn't in the recycle bin. Read the hierarchy (get_hierarchy with scope sections) and pass a one:Section ID.";

    /// <summary>What to do about a <c>parentPageId</c> the server refuses.</summary>
    public const string ParentUserAction =
        "Pass as parentPageId the ID of a page in the section given as sectionId, at level 1 or 2 (a page at level 3 can't take a subpage). Read the section with get_hierarchy (scope children) for its pages and their levels, or leave parentPageId out to add a top-level page.";

    /// <summary>What to do about a subpage in a password-protected section without a read grant (3.14 Q1 a).</summary>
    public const string ProtectedParentUserAction =
        "Leave parentPageId out to add a top-level page to the password-protected section. Adding it as a subpage needs a read grant for that section, held when the call starts: ask the user whether to grant it (they can run the grant command with --read-section, or accept the dialog that appears when you read one of its pages with get_page_content in a client that supports it), then retry. Never run the grant command yourself.";

    /// <summary>What to do after <c>subpageNotPlaced</c> (3.14 Q4).</summary>
    public const string NotPlacedUserAction =
        "Tell the user. To make it a subpage now, read the section with get_hierarchy (scope children), then move the page after the parent's subpages with reorder_hierarchy_children and set its level with set_page_level.";

    /// <summary>The steps 1 to 7 of <c>create_page</c> without a parent, as before story 3.14.</summary>
    public CreatePageUseCase(IOneNoteGateway gateway, WriteAccess access, WritePipeline pipeline)
        : this(gateway, access, pipeline, null, null)
    {
    }

    public OperationClass Class => OperationClass.Create;

    public Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string sectionId, NewPageStyle style, CancellationToken ct) =>
        ExecuteAsync(context, sectionId, style, null, ct);

    /// <summary>A new page in <paramref name="sectionId"/>, as a subpage of <paramref name="parentPageId"/> when one is given.</summary>
    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string sectionId, NewPageStyle style, string? parentPageId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var parent = string.IsNullOrEmpty(parentPageId) ? null : parentPageId;
        if (parent is not null && (readAccess is null || snapshot is null))
        {
            throw new InvalidOperationException("create_page with parentPageId needs the read access check and the hierarchy snapshot.");
        }

        var operation = new CreatePage(gateway, access, readAccess, snapshot, context.CorrelationId, sectionId ?? string.Empty, style, parent);
        var result = await pipeline.RunAsync(context, operation, ct).ConfigureAwait(false);
        return new ToolSuccess(
            null,
            new ToolMeta(context.CorrelationId, PageId: result.PageId, JournalEntryId: operation.JournalEntryId?.ToString("D", CultureInfo.InvariantCulture)),
            result.Warnings);
    }

    private static OneNoteException NotASection(string sectionId, string why) =>
        new(ErrorCode.ObjectNotFound, $"Object {sectionId} {why}, so no page was created.", userAction: NotASectionUserAction);

    /// <summary>What a create reports: the new page's ID and, with a parent, the placement's warnings.</summary>
    private sealed record Created(string PageId, IReadOnlyList<ToolWarning> Warnings);

    /// <summary>The <c>Create</c> class's steps for one <c>CreateNewPage</c>, and with a parent the subpage placement.</summary>
    [SupportedOSPlatform("windows")]
    private sealed class CreatePage(
        IOneNoteGateway gateway,
        WriteAccess access,
        ReadAccess? readAccess,
        HierarchySnapshot? snapshot,
        string correlationId,
        string sectionId,
        NewPageStyle style,
        string? parentPageId) : WriteOperation<Created>
    {
        private readonly DateTimeOffset callStart = readAccess?.Now() ?? DateTimeOffset.MinValue;
        private HierarchyNode? section;
        private string childrenXml = string.Empty;
        private string? createdPageId;

        /// <summary>The pages sent to place the new page (null until placed): the read-back compares against them.</summary>
        private PageLevelResult? placed;

        public override OperationClass Class => OperationClass.Create;

        public override IReadOnlyCollection<string> WrittenPageIds => createdPageId is null ? [] : [createdPageId];

        /// <summary>With a parent, the section's <c>hierarchy</c> entry step 6 stored; null until then, and without a parent.</summary>
        public Guid? JournalEntryId { get; private set; }

        public override string? FailureNote => JournalEntryId is { } entryId
            ? string.Create(CultureInfo.InvariantCulture, $"The section's hierarchy as it was before this create is in journal entry {entryId:D}.")
            : null;

        public override async Task AuthorizeAsync(CancellationToken ct)
        {
            await access.CheckAsync(sectionId, () => NotASection(sectionId, "isn't a section"), ct).ConfigureAwait(false);
            if (parentPageId is not null)
            {
                // 3.14: the refusals against a read made now, so the lock is never taken for one; step 4 checks again.
                var xml = await gateway.GetHierarchyAsync(sectionId, HierarchyScope.Children, ct).ConfigureAwait(false);
                CheckParent(xml);
            }
        }

        public override async Task ReadAsync(CancellationToken ct)
        {
            var xml = await gateway.GetHierarchyAsync(sectionId, HierarchyScope.Self, ct).ConfigureAwait(false);
            section = XmlReads.Readable(() => HierarchyNode.Root(xml), "GetHierarchy");
            if (parentPageId is not null)
            {
                childrenXml = await gateway.GetHierarchyAsync(sectionId, HierarchyScope.Children, ct).ConfigureAwait(false);
            }
        }

        public override void Validate()
        {
            var node = section ?? throw new InvalidOperationException("Validate runs after ReadAsync.");
            if (node.Kind != HierarchyKind.Section)
            {
                throw NotASection(sectionId, "isn't a section");
            }

            if (node.InRecycleBin)
            {
                throw NotASection(sectionId, "is in the recycle bin");
            }

            if (node.Locked)
            {
                throw new OneNoteException(ErrorCode.SectionLocked, $"Section {sectionId} is locked, so no page was created.");
            }

            if (parentPageId is not null)
            {
                CheckParent(childrenXml);
            }
        }

        public override async Task SnapshotAsync(CancellationToken ct)
        {
            if (parentPageId is null)
            {
                return;
            }

            // Step 6 (3.14): the section's hierarchy before the create and the placement (AD-8); a protected one is encrypted.
            JournalEntryId = (await snapshot!.TakeAsync(
                new HierarchySnapshotRequest(correlationId, OperationName, OperationClass.Create, sectionId, childrenXml),
                ct).ConfigureAwait(false)).EntryId;
        }

        public override async Task<Created> CallAsync(CancellationToken ct)
        {
            await CallNamingFailureNoteAsync(async () => createdPageId = await gateway.CreateNewPageAsync(sectionId, style, ct).ConfigureAwait(false)).ConfigureAwait(false);
            var created = createdPageId!;
            if (parentPageId is null)
            {
                return new Created(created, []);
            }

            // Still under the lock (3.14 design note): the placement is part of step 7, so a failure of it is visible as one.
            try
            {
                await PlaceAsync(created, ct).ConfigureAwait(false);
                return new Created(created, []);
            }
#pragma warning disable CA1031 // Q4: the page exists; any placement failure (the deadline and a cancel included) is a warning, never an error.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // Sent but abandoned (a timeout, a cancel, the deadline, a failed call): OneNote may still apply it (AD-9), so
                // the warning says so; the pipeline keeps the lock until the abandoned call settles (AD-3, AD-18).
                var sent = placed is not null && (ex is OperationCanceledException || (ex is OneNoteException one && WritePipeline.IsAmbiguous(one)));
                placed = null;
                var where = sent
                    ? $"so it may or may not have been placed: re-read section {sectionId} to see whether it is a subpage or still at the end at level 1"
                    : $"so it is at the end of section {sectionId} at level 1";
                return new Created(created, [new ToolWarning(
                    ToolWarnings.SubpageNotPlaced,
                    $"Page {created} was created, but it couldn't be made a subpage of page {parentPageId} ({Why(ex)}), {where}.",
                    NotPlacedUserAction)]);
            }
        }

        /// <summary>Between 7 and 8: the placed page must read back where it was sent, at the level sent.</summary>
        public override async Task<Created> AfterCallAsync(Created result, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(result);
            if (placed is not { } sent)
            {
                return result;
            }

            var xml = await gateway.GetHierarchyAsync(sectionId, HierarchyScope.Children, ct).ConfigureAwait(false);
            var after = XmlReads.Readable(() => HierarchyTarget.Read(xml), "GetHierarchy").Children;
            var index = after.ToList().FindIndex(p => string.Equals(p.Id, result.PageId, StringComparison.Ordinal));
            var expected = sent.Pages[sent.Index];
            if (index == sent.Index && after[index].PageLevel == expected.PageLevel)
            {
                return result;
            }

            var found = index < 0
                ? "isn't in the section"
                : string.Create(CultureInfo.InvariantCulture, $"is at position {index + 1} at level {after[index].PageLevel}");
            return result with
            {
                Warnings = [.. result.Warnings, new ToolWarning(
                    ToolWarnings.ReadBackDiffers,
                    string.Create(CultureInfo.InvariantCulture, $"Page {result.PageId} was sent to position {sent.Index + 1} of section {sectionId} at level {expected.PageLevel}, but it reads back {found}."),
                    "Read the section with get_hierarchy (scope children) to see where OneNote kept the page.")],
            };
        }

        /// <summary>The read-back threw (its deadline or a cancel included): the page and its placement stand, with <c>readBackFailed</c>.</summary>
        public override Created AfterCallFailed(Created result, Exception exception)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(exception);
            if (placed is null)
            {
                return result;
            }

            return result with
            {
                Warnings = [.. result.Warnings, new ToolWarning(
                    ToolWarnings.ReadBackFailed,
                    $"Page {result.PageId} was created and placed under page {parentPageId}, but section {sectionId} couldn't be re-read afterwards ({Why(exception)}), so where OneNote kept it wasn't checked.",
                    "Read the section with get_hierarchy (scope children) to check the page's place and level.")],
            };
        }

        /// <summary>Step 7's placement: the section re-read, the new page moved after the parent's block one level deeper, one call.</summary>
        private async Task PlaceAsync(string created, CancellationToken ct)
        {
            var xml = await gateway.GetHierarchyAsync(sectionId, HierarchyScope.Children, ct).ConfigureAwait(false);
            var pages = XmlReads.Readable(() => HierarchyTarget.Read(xml), "GetHierarchy").Children;
            var plan = PageLevels.PlaceSubpage(pages, parentPageId!, created);
            if (plan.Problem != PageLevelProblem.None)
            {
                throw new OneNoteException(ErrorCode.ObjectNotFound, $"The section's re-read found {plan.Problem}.");
            }

            var changes = HierarchyUpdate.Reorder(HierarchyKind.Section, sectionId, plan.Pages);

            // AD-16: the hard gate for UpdateHierarchy. The XML is ours, so a failure is our bug, never sent.
            if (SchemaValidation.Problems(changes).Count > 0)
            {
                throw new OneNoteException(ErrorCode.Internal, "The server built UpdateHierarchy XML that the 2013 schema refuses.");
            }

            placed = plan;
            await gateway.UpdateHierarchyAsync(changes, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 3.14's refusals for <paramref name="sectionChildrenXml"/>, after the section's own (kind, recycle bin, locked): a
        /// protected section without the read grant held at the call's start (Q1 a), then a parent that isn't a live page
        /// of the section or is at level 3. IDs and levels only.
        /// </summary>
        private void CheckParent(string sectionChildrenXml)
        {
            var read = XmlReads.Readable(() => HierarchyTarget.Read(sectionChildrenXml), "GetHierarchy");
            if (read.Kind != HierarchyKind.Section)
            {
                throw NotASection(sectionId, "isn't a section");
            }

            if (read.IsRecycleBin || read.InRecycleBin)
            {
                throw NotASection(sectionId, "is in the recycle bin");
            }

            if (read.Locked)
            {
                throw new OneNoteException(ErrorCode.SectionLocked, $"Section {sectionId} is locked, so no page was created.");
            }

            if (read.Encrypted && !readAccess!.HoldsReadGrantAt(GrantKeys.SectionKey(read.Path), callStart))
            {
                throw new OneNoteException(
                    ErrorCode.ReadNotGranted,
                    $"Section {sectionId} is password-protected, and adding a subpage to it needs a read grant for it held when the call starts, so no page was created.",
                    userAction: ProtectedParentUserAction);
            }

            switch (PageLevels.ParentProblem(read.Children, parentPageId!))
            {
                case PageLevelProblem.ParentNotListed:
                    throw Invalid($"Object {parentPageId} isn't a live page of section {sectionId}, so no page was created.");
                case PageLevelProblem.ParentAtDeepest:
                    throw Invalid(string.Create(CultureInfo.InvariantCulture, $"Page {parentPageId} is at level {PageLevels.Deepest}, the deepest a page can be, so it can't take a subpage, and no page was created."));
            }
        }

        private static OneNoteException Invalid(string message) =>
            new(ErrorCode.InvalidArgument, message, userAction: ParentUserAction, retryable: false);

        private static string Why(Exception exception) => exception switch
        {
            OneNoteException one => one.Code.ToString(),
            OperationCanceledException => "the write's deadline or a cancel",
            _ => exception.GetType().Name,
        };
    }
}
