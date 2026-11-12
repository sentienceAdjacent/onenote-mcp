using System.Globalization;
using System.Runtime.Versioning;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// AD-17's basis as a write takes it: <c>{lastModifiedTime?, pageHash?, elementHashes?}</c>, passed back unchanged from a
/// read's <c>meta.basis</c>. Every field is optional; a missing one is filled from step 3's read (<c>partialBasis</c>).
/// </summary>
public sealed record PageWriteBasis(string? LastModifiedTime = null, string? PageHash = null, IReadOnlyDictionary<string, string>? ElementHashes = null);

/// <summary>
/// What a page write reports: <c>meta.basis</c> from the read-back's piBasic read (Phill, 2026-11-12, 2.9 plan Q2), null
/// when the read-back failed or the section is protected with no read grant; and the warnings, in step order.
/// </summary>
public sealed record PageWriteResult(PageBasisResult? Basis, IReadOnlyList<ToolWarning> Warnings);

/// <summary>What every <see cref="PageWrite"/> uses: the gateway, both access checks and step 6's snapshot.</summary>
[SupportedOSPlatform("windows")]
public sealed record PageWriteServices(IOneNoteGateway Gateway, WriteAccess WriteAccess, ReadAccess ReadAccess, PageSnapshot Snapshot);

/// <summary>
/// The <c>PageWrite</c> class's shared steps (AD-7), for <c>UpdatePageContent</c> here and <c>DeletePageContent</c> (story
/// 2.10) beside it.
/// <list type="number">
/// <item>The notebook's write grant (AD-6), also in a password-protected section; never the read dialog (AD-21).</item>
/// <item value="3">The page's section, then the page's own hierarchy node, both read fresh: a section in the recycle bin,
/// not a page, or a page in the recycle bin is <see cref="ErrorCode.ObjectNotFound"/> (as 2.4 Q8); a locked section is
/// <see cref="ErrorCode.SectionLocked"/> before the page is looked at; then the piBasic page XML.</item>
/// <item value="6">The page snapshot (AD-8) of step 3's piBasic read.</item>
/// <item value="8">Between 7 and 8, the read-back: a piBasic re-read compared with what was sent. It only adds warnings.</item>
/// </list>
/// Whether a message may name what is inside the page is decided once: with no read grant for a protected section, at
/// the call's start, a <c>once</c> grant included and never asking (AD-7, AD-21), refusals and warnings carry counts
/// only. Messages carry IDs, counts, attribute and element names only, never content (AD-9, AD-13). The page is dropped
/// from the search cache whatever the outcome.
/// </summary>
[SupportedOSPlatform("windows")]
public abstract class PageWrite : WriteOperation<PageWriteResult>
{
    /// <summary>At most this many warnings per code; the rest become one summary warning (AD-7 read-back).</summary>
    public const int MaxWarningsPerCode = 20;

    /// <summary>What to do when <c>pageId</c> isn't a live page's ID.</summary>
    public const string NotAPageUserAction =
        "Pass the ID of a page that isn't in the recycle bin. Read the hierarchy (get_hierarchy with scope pages) and use a one:Page ID.";

    private readonly DateTimeOffset callStart;
    private string? basicXml;
    private HierarchyNode? page;
    private HierarchyNode? section;

    protected PageWrite(PageWriteServices services, string correlationId, string operation, string pageId, DateTimeOffset callStart)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
        CorrelationId = correlationId ?? string.Empty;
        Operation = operation;
        PageId = pageId ?? string.Empty;
        this.callStart = callStart;
    }

    public override OperationClass Class => OperationClass.PageWrite;

    public override IReadOnlyCollection<string> WrittenPageIds => [PageId];

    protected PageWriteServices Services { get; }

    protected string CorrelationId { get; }

    protected string Operation { get; }

    protected string PageId { get; }

    /// <summary>Step 3's piBasic read of the page.</summary>
    protected string BasicXml => basicXml ?? throw new InvalidOperationException("Step 3 hasn't read the page.");

    /// <summary>False in a protected section with no read grant at the call's start: messages then name nothing inside the page.</summary>
    protected bool NamesIds { get; private set; }

    public override Task AuthorizeAsync(CancellationToken ct) =>
        Services.WriteAccess.CheckAsync(PageId, () => NotAPage("isn't a page"), ct);

    public override async Task ReadAsync(CancellationToken ct)
    {
        // The section first: OneNote may not list a locked section's pages, which must read as SectionLocked.
        section = await new HierarchyWalk(Services.Gateway).OwningSectionAsync(PageId, ct).ConfigureAwait(false)
            ?? throw NotAPage("isn't in a section");
        if (section.InRecycleBin)
        {
            throw NotAPage("is in the recycle bin");
        }

        if (section.Locked)
        {
            throw new OneNoteException(ErrorCode.SectionLocked, $"Section {section.Id} is locked, so nothing was written to page {PageId}.");
        }

        NamesIds = Services.ReadAccess.HoldsReadGrant(section, callStart);
        if (!NamesIds)
        {
            // Before the page is read (AD-17 as amended by 5.2): checked again here, under the lock.
            CheckWithoutReadGrant(section.Id);
        }

        var self = await Services.Gateway.GetHierarchyAsync(PageId, HierarchyScope.Self, ct).ConfigureAwait(false);
        page = XmlReads.Readable(() => HierarchyNode.Root(self), "GetHierarchy");
        if (page.Kind != HierarchyKind.Page)
        {
            throw NotAPage("isn't a page");
        }

        if (page.InRecycleBin)
        {
            throw NotAPage("is in the recycle bin");
        }

        basicXml = await Services.Gateway.GetPageContentAsync(PageId, PageInfo.Basic, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The no-prompt read-grant check before the page is read (Phill, 2026-11-12, story 2.9 security review; AD-17 as
    /// amended by 5.2): in a protected section with no read grant at the call's start, it throws
    /// <see cref="ErrorCode.ReadNotGranted"/> for anything a write-grant-only caller could use as an oracle. Nothing to
    /// check by default.
    /// </summary>
    protected virtual void CheckWithoutReadGrant(string sectionId)
    {
    }

    /// <summary>
    /// Step 1's view of the read grant, before the page is read: true when the page's section isn't protected or a grant
    /// covered it at the call's start; also true when the walk finds no section (step 3 refuses that). Never asks.
    /// </summary>
    protected async Task<(bool Granted, string? SectionId)> ReadGrantAtStartAsync(CancellationToken ct)
    {
        var owner = await new HierarchyWalk(Services.Gateway).OwningSectionAsync(PageId, ct).ConfigureAwait(false);
        return (owner is null || Services.ReadAccess.HoldsReadGrant(owner, callStart), owner?.Id);
    }

    public override Task SnapshotAsync(CancellationToken ct) =>
        Services.Snapshot.TakeAsync(new PageSnapshotRequest(CorrelationId, Operation, OperationClass.PageWrite, PageId, BasicXml), ct);

    /// <summary><see cref="ErrorCode.InvalidArgument"/> with its own <c>userAction</c>, as AD-7's refusals need (deferred-work, 2.5 review).</summary>
    protected static OneNoteException Invalid(string message, string userAction) =>
        new(ErrorCode.InvalidArgument, message, userAction: userAction, retryable: false);

    /// <summary>The IDs for a message: up to <see cref="MaxWarningsPerCode"/>, then how many more.</summary>
    protected static string IdList(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var shown = string.Join(", ", ids.Take(MaxWarningsPerCode));
        return ids.Count > MaxWarningsPerCode
            ? string.Create(CultureInfo.InvariantCulture, $"{shown} and {ids.Count - MaxWarningsPerCode} more")
            : shown;
    }

    /// <summary>
    /// Caps <paramref name="warnings"/> at <see cref="MaxWarningsPerCode"/> per code, adding one summary warning per code
    /// that had more, in first-seen order.
    /// </summary>
    protected static IReadOnlyList<ToolWarning> Capped(IEnumerable<ToolWarning> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        var kept = new List<ToolWarning>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var summaries = new List<string>();
        foreach (var warning in warnings)
        {
            counts[warning.Code] = counts.GetValueOrDefault(warning.Code) + 1;
            if (counts[warning.Code] <= MaxWarningsPerCode)
            {
                kept.Add(warning);
            }
            else if (counts[warning.Code] == MaxWarningsPerCode + 1)
            {
                summaries.Add(warning.Code);
            }
        }

        foreach (var code in summaries)
        {
            kept.Add(new ToolWarning(
                code,
                string.Create(CultureInfo.InvariantCulture, $"{counts[code] - MaxWarningsPerCode} more {code} warnings from this write aren't listed."),
                "Re-read the page (get_page_content) to see what OneNote kept."));
        }

        return kept;
    }

    private OneNoteException NotAPage(string why) =>
        new(ErrorCode.ObjectNotFound, $"Object {PageId} {why}, so nothing was written.", userAction: NotAPageUserAction);
}

/// <summary>
/// <c>UpdatePageContent</c> as a <see cref="PageWrite"/> (AD-7, AD-17; story 2.9).
/// <list type="bullet">
/// <item>Step 4 (<see cref="PageWriteCheck.Validate"/>): the 2013 schema gate (<see cref="ErrorCode.InvalidXml"/>, line,
/// position and element name only; Q3); the root <c>one:Page@ID</c> is <c>pageId</c> (Q1); every sent <c>objectID</c> is
/// on the page (<see cref="ErrorCode.UnknownObjectId"/>); removals name OEs under sent Outlines and aren't also sent;
/// no OE is moved between Outlines (Q4); sent Outlines are whole.</item>
/// <item>Step 5 (AD-17, Q5): a basis date must be step 3's instant; each sent Outline's hash, and each sent OE's outside a
/// sent Outline (the title's), must equal step 3's, else <see cref="ErrorCode.StaleWrite"/>. A missing date or hash is
/// <c>partialBasis</c>; no basis is <c>noBasis</c>. The caller's date, never the live one, goes to COM.</item>
/// <item>Step 7: <c>UpdatePageContent</c> with the caller's XML unchanged (AD-4) and <c>force: false</c>.</item>
/// <item>Read-back: <see cref="PageWriteCheck.CompareReadBack"/>; <c>readBackDiffers</c>, <c>imageLost</c>,
/// <c>readBackFailed</c>, and <c>meta.basis</c> from the re-read (Q2).</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdatePageContentWrite : PageWrite
{
    /// <summary>The tool's name, recorded in the journal entry.</summary>
    public const string OperationName = "update_page_content";

    public const string OmittedUserAction =
        "update_page_content replaces the whole OE list of every Outline it sends: an OE left out is deleted. Send every OE that "
        + "get_page_content shows under each Outline you send (nested and table-cell OEs too, each with its objectID), or name the "
        + "OEs to delete in removeObjectIds. Re-read the page if you don't have them all.";

    public const string MovedUserAction =
        "OneNote can't move an OE from one Outline to another. Send the OE without its objectID in the Outline it should go to (it "
        + "is added as a new OE), and name its objectID in removeObjectIds while sending the Outline it was in, which removes it there.";

    public const string RemovalUserAction =
        "removeObjectIds may name only OEs inside an Outline this call sends, and never an OE the call also sends. Send the Outline "
        + "that holds each OE you remove, without that OE.";

    public const string PageIdUserAction =
        "The XML's root must be one:Page with ID equal to the pageId argument. Read the page with get_page_content and keep its ID unchanged.";

    public const string WrongElementUserAction =
        "Send each objectID on the same kind of element it has in get_page_content's XML.";

    public const string NoBasisUserAction =
        "Pass meta.basis from get_page_content (or from the previous update_page_content) unchanged as basis, so a change made since your read is caught.";

    public const string ReadFirstUserAction =
        "Read the page with get_page_content first: that asks the user for the section's read grant. Then write with its meta.basis. "
        + "Without that grant, a write here may carry only basis.lastModifiedTime and new content: no hashes, no objectIDs and no removeObjectIds.";

    public const string PathUserAction =
        "Name a file by its full local path (such as C:\\folder\\file.png), never a network, device or relative path; or send the image as inline one:Data (base64).";

    public const string ReadBackWithheldUserAction =
        "To see what OneNote kept, read the page with get_page_content, which asks the user for the section's read grant.";

    public const string DoctypeUserAction =
        "Send the page XML without a <!DOCTYPE ...> declaration, starting at the one:Page root as get_page_content returns it.";

    public const string ReadBackUserAction =
        "Re-read the page (get_page_content) to see what OneNote kept. OneNote ignores some edits, such as a changed quickStyleIndex on an existing OE.";

    private readonly string xml;
    private readonly PageWriteBasis? basis;
    private readonly IReadOnlyList<string> removeObjectIds;
    private readonly List<ToolWarning> warnings = [];
    private readonly HashSet<string> confirmedPaths = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> hashTargets = [];
    private string? expectedLastModified;

    public UpdatePageContentWrite(
        PageWriteServices services,
        string correlationId,
        string pageId,
        string xml,
        PageWriteBasis? basis,
        IReadOnlyList<string>? removeObjectIds,
        DateTimeOffset callStart)
        : base(services, correlationId, OperationName, pageId, callStart)
    {
        this.xml = xml ?? string.Empty;
        this.basis = basis;
        // A null name is refused like any name that isn't an OE under a sent Outline.
        this.removeObjectIds = [.. (removeObjectIds ?? []).Select(id => id ?? string.Empty)];
    }

    /// <summary>
    /// Step 1, in the order the 5.2 re-check sets (Phill, 2026-11-12), so the user is never asked to confirm anything for
    /// a call that will fail anyway: the sent XML parsed with DTDs prohibited (a DOCTYPE or broken XML is
    /// <see cref="ErrorCode.InvalidXml"/>); the no-prompt read-grant check (<see cref="CheckWithoutReadGrant"/>); the
    /// notebook's write grant (which may raise the grant dialog); then the file paths: each new one canonical and local
    /// (else <see cref="ErrorCode.InvalidArgument"/>), and any the user hasn't confirmed in this call is
    /// <see cref="FilePathsUnconfirmed"/>, which raises the file-path dialog. The page is read here only when the XML
    /// names a path and a read grant holds; without one every path is new.
    /// </summary>
    public override async Task AuthorizeAsync(CancellationToken ct)
    {
        if (PageWriteScan.ParseProblem(xml) is { } parse)
        {
            throw Refusal(new PageWriteProblem(PageWriteProblemKind.InvalidXml, [], [parse]));
        }

        var (granted, sectionId) = await ReadGrantAtStartAsync(ct).ConfigureAwait(false);
        if (!granted)
        {
            CheckWithoutReadGrant(sectionId ?? string.Empty);
        }

        await base.AuthorizeAsync(ct).ConfigureAwait(false);

        if (PageWriteScan.FilePaths(xml).Count == 0)
        {
            return;
        }

        var read = granted ? await Services.Gateway.GetPageContentAsync(PageId, PageInfo.Basic, ct).ConfigureAwait(false) : null;
        var unconfirmed = Unconfirmed(read);
        if (unconfirmed.Count > 0)
        {
            var refusal = new FilePathsUnconfirmed(PageId, unconfirmed, () => confirmedPaths.UnionWith(unconfirmed));

            // After a confirmation, a path that is new now means the page changed meanwhile: retryable, not "no dialog".
            throw confirmedPaths.Count > 0
                ? refusal.Because("the page changed while the user was deciding", unsupported: false, FilePathsUnconfirmed.ChangedUserAction)
                : refusal;
        }
    }

    public override void Validate()
    {
        var validation = XmlReads.Readable(() => PageWriteCheck.Validate(xml, PageId, BasicXml, removeObjectIds), $"GetPageContent({PageId})");
        if (validation.Problem is { } problem)
        {
            throw Refusal(problem);
        }

        // AD-7: a path that is new against step 3's read and wasn't confirmed in step 1 (the page changed in between).
        var unconfirmed = Unconfirmed(NamesIds ? BasicXml : null);
        if (unconfirmed.Count > 0)
        {
            throw new OneNoteException(
                ErrorCode.WriteNotGranted,
                string.Create(CultureInfo.InvariantCulture, $"The XML for page {PageId} names {unconfirmed.Count} new file path(s) the user didn't confirm in this call, so nothing was written."),
                userAction: "Re-read the page and retry: the user is asked to confirm each new file path. Or send the image as inline one:Data (base64) instead.",
                retryable: true);
        }

        hashTargets = validation.HashTargets;
    }

    /// <summary>
    /// AD-17 as amended by 5.2 (Phill, 2026-11-12, story 2.9 security review): without a read grant, a write may carry only
    /// a basis date and new content. Any element hash (<c>pageHash</c> included), any <c>objectID</c> in the XML, or any
    /// <c>removeObjectIds</c> entry is <see cref="ErrorCode.ReadNotGranted"/>, before the page is read: "existing" can't be
    /// tested then, and each would be an oracle. The message names the section's ID only (AD-21).
    /// </summary>
    protected override void CheckWithoutReadGrant(string sectionId)
    {
        var hashes = basis is { PageHash: not null } || basis?.ElementHashes is { Count: > 0 };
        if (hashes || removeObjectIds.Count > 0 || PageWriteScan.SendsObjectId(xml))
        {
            throw new OneNoteException(
                ErrorCode.ReadNotGranted,
                $"Section {sectionId} is password-protected and has no read grant, so a write to it may carry only a basis lastModifiedTime and new content: no hashes, no objectIDs and no removeObjectIds. Nothing was written.",
                userAction: ReadFirstUserAction);
        }
    }

    /// <summary>The canonical forms of the new paths not yet confirmed in this call; a network, device or relative path is refused.</summary>
    private List<string> Unconfirmed(string? readXml)
    {
        var unconfirmed = new List<string>();
        foreach (var path in PageWriteScan.NewFilePaths(xml, readXml))
        {
            var canonical = FilePathGate.Canonical(path.Value, out var refusal) ?? throw Invalid(
                $"The XML for page {PageId} names a file path that is {Describe(refusal)}, which page writes never read, so nothing was written.",
                PathUserAction);
            if (!confirmedPaths.Contains(canonical) && !unconfirmed.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                unconfirmed.Add(canonical);
            }
        }

        return unconfirmed;
    }

    private static string Describe(FilePathRefusal refusal) => refusal switch
    {
        FilePathRefusal.Network => "on the network (a UNC path, a mapped network drive, or a link to one)",
        FilePathRefusal.Device => "a device path",
        FilePathRefusal.InvalidCharacters => "written with control, invisible or wildcard characters",
        _ => "not a full local path such as C:\\folder\\file.png",
    };

    public override Task CheckBasisAsync(CancellationToken ct)
    {
        var current = XmlReads.Readable(() => PageBasis.From(BasicXml), $"GetPageContent({PageId})");
        if (basis is null)
        {
            expectedLastModified = current.LastModifiedTime;
            warnings.Add(new ToolWarning(
                ToolWarnings.NoBasis,
                $"No basis was passed, so the write to page {PageId} was checked against the page as the server just read it: a change made since your own read could be overwritten.",
                NoBasisUserAction));
            return Task.CompletedTask;
        }

        var filled = new List<string>();
        if (basis.LastModifiedTime is null)
        {
            expectedLastModified = current.LastModifiedTime;
            filled.Add("it has no lastModifiedTime, so the page's current one was used");
        }
        else
        {
            var sent = OneNoteDates.ToUtc(basis.LastModifiedTime)
                ?? throw Invalid(
                    $"The basis lastModifiedTime for page {PageId} isn't a date OneNote writes, so nothing was written.",
                    NoBasisUserAction);
            if (OneNoteDates.ToUtc(current.LastModifiedTime) != sent)
            {
                throw new OneNoteException(
                    ErrorCode.StaleWrite,
                    $"Page {PageId} changed since the basis was read (its lastModifiedTime differs), so nothing was written.");
            }

            expectedLastModified = basis.LastModifiedTime;
        }

        var stale = new List<string>();
        var missing = new List<string>();
        foreach (var target in hashTargets)
        {
            if (basis.ElementHashes is { } hashes && hashes.TryGetValue(target, out var hash) && hash is not null)
            {
                if (!current.ElementHashes.TryGetValue(target, out var now) || !string.Equals(hash, now, StringComparison.Ordinal))
                {
                    stale.Add(target);
                }
            }
            else
            {
                missing.Add(target);
            }
        }

        if (stale.Count > 0)
        {
            throw new OneNoteException(
                ErrorCode.StaleWrite,
                NamesIds
                    ? $"On page {PageId}, {IdList(stale)} changed since the basis was read, so nothing was written."
                    : string.Create(CultureInfo.InvariantCulture, $"On page {PageId}, {stale.Count} sent element(s) changed since the basis was read, so nothing was written."));
        }

        if (missing.Count > 0)
        {
            filled.Add(NamesIds
                ? $"it has no hash for {IdList(missing)}, so their check was skipped"
                : string.Create(CultureInfo.InvariantCulture, $"it has no hash for {missing.Count} sent element(s), so their check was skipped"));
        }

        if (filled.Count > 0)
        {
            warnings.Add(new ToolWarning(
                ToolWarnings.PartialBasis,
                $"The basis for page {PageId} is partial: {string.Join("; ", filled)}.",
                NoBasisUserAction));
        }

        return Task.CompletedTask;
    }

    public override async Task<PageWriteResult> CallAsync(CancellationToken ct)
    {
        await Services.Gateway.UpdatePageContentAsync(xml, expectedLastModified ?? string.Empty, ct).ConfigureAwait(false);
        return new PageWriteResult(null, [.. warnings]);
    }

    public override async Task<PageWriteResult> AfterCallAsync(PageWriteResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        var after = await Services.Gateway.GetPageContentAsync(PageId, PageInfo.Basic, ct).ConfigureAwait(false);
        var diffs = XmlReads.Readable(() => PageWriteCheck.CompareReadBack(xml, after, BasicXml), $"GetPageContent({PageId})");
        if (!NamesIds)
        {
            // No read grant: no basis, and the read-back's details stay inside the server; one count-free warning says so.
            return new PageWriteResult(null, [.. result.Warnings, .. ReadBackWarnings(diffs), new ToolWarning(
                ToolWarnings.ReadBackWithheld,
                $"The write to page {PageId} was checked after it was made, but the section has no read grant, so what OneNote kept and a basis for the next write are withheld.",
                ReadBackWithheldUserAction)]);
        }

        var afterBasis = PageBasisResult.From(XmlReads.Readable(() => PageBasis.From(after), $"GetPageContent({PageId})"));
        return new PageWriteResult(afterBasis, [.. result.Warnings, .. ReadBackWarnings(diffs)]);
    }

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
        return new PageWriteResult(null, [.. result.Warnings, new ToolWarning(
            ToolWarnings.ReadBackFailed,
            $"The write to page {PageId} succeeded, but the page couldn't be re-read afterwards ({why}), so what OneNote kept wasn't checked.",
            "Re-read the page (get_page_content) to check the change and to get a basis for the next write.")]);
    }

    private IReadOnlyList<ToolWarning> ReadBackWarnings(IReadOnlyList<ReadBackDiff> diffs)
    {
        if (diffs.Count == 0)
        {
            return [];
        }

        if (!NamesIds)
        {
            return CountsOnly(diffs);
        }

        return Capped(diffs.Select(diff => diff.Kind switch
        {
            ReadBackDiffKind.AttributeDiffers => new ToolWarning(
                ToolWarnings.ReadBackDiffers,
                $"On page {PageId}, OneNote didn't apply the attribute {diff.Attribute} sent on {diff.ObjectId}.",
                ReadBackUserAction),
            ReadBackDiffKind.ObjectIdChanged => new ToolWarning(
                ToolWarnings.ReadBackDiffers,
                diff.NewObjectId is { } newId
                    ? $"On page {PageId}, OE {diff.ObjectId} came back as {newId}: OneNote deleted it and re-created it under a new objectID."
                    : $"On page {PageId}, OE {diff.ObjectId} came back without an objectID.",
                ReadBackUserAction),
            ReadBackDiffKind.Missing => new ToolWarning(
                ToolWarnings.ReadBackDiffers,
                $"On page {PageId}, {diff.ObjectId} was sent but isn't on the page after the write.",
                ReadBackUserAction),
            ReadBackDiffKind.OeCountDiffers => new ToolWarning(
                ToolWarnings.ReadBackDiffers,
                $"On page {PageId}, Outline {diff.ObjectId} came back with a different number of OEs than were sent, so its remaining OEs weren't compared.",
                ReadBackUserAction),
            _ => new ToolWarning(
                ToolWarnings.ImageLost,
                string.Create(CultureInfo.InvariantCulture, $"On page {PageId}, Outline {diff.ObjectId} was sent with {diff.SentImages} image(s) and has {diff.ImagesAfter} after the write."),
                "Re-read the page (get_page_content). The recovery journal entry for this write holds the page as it was before."),
        }));
    }

    /// <summary>The read-back's findings as counts only, for a protected section with no read grant (AD-7, AD-21).</summary>
    private List<ToolWarning> CountsOnly(IReadOnlyList<ReadBackDiff> diffs)
    {
        var result = new List<ToolWarning>();
        int Count(ReadBackDiffKind kind) => diffs.Count(d => d.Kind == kind);
        var parts = new List<string>();
        void Part(int n, string what)
        {
            if (n > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{n} {what}"));
            }
        }

        Part(Count(ReadBackDiffKind.AttributeDiffers), "attribute(s) OneNote didn't apply");
        Part(Count(ReadBackDiffKind.ObjectIdChanged), "OE(s) re-created under a new objectID");
        Part(Count(ReadBackDiffKind.Missing), "sent element(s) missing after the write");
        Part(Count(ReadBackDiffKind.OeCountDiffers), "Outline(s) whose OEs came back in another number");
        if (parts.Count > 0)
        {
            result.Add(new ToolWarning(
                ToolWarnings.ReadBackDiffers,
                $"After the write, page {PageId} differs from what was sent: {string.Join(", ", parts)}.",
                ReadBackUserAction));
        }

        var lost = diffs.Where(d => d.Kind == ReadBackDiffKind.ImageLost).ToList();
        if (lost.Count > 0)
        {
            result.Add(new ToolWarning(
                ToolWarnings.ImageLost,
                string.Create(CultureInfo.InvariantCulture, $"After the write, {lost.Count} sent Outline(s) on page {PageId} have {lost.Sum(d => d.SentImages - d.ImagesAfter)} fewer image(s) than were sent."),
                "Re-read the page if you can. The recovery journal entry for this write holds the page as it was before."));
        }

        return result;
    }

    private OneNoteException Refusal(PageWriteProblem problem)
    {
        var ids = problem.ObjectIds;
        string Named(string named, string counted) => NamesIds ? named : counted;
        string N(int n) => n.ToString(CultureInfo.InvariantCulture);
        return problem.Kind switch
        {
            PageWriteProblemKind.InvalidXml when problem.Schema is [{ IsDoctype: true }, ..] => new OneNoteException(
                ErrorCode.InvalidXml,
                $"The XML for page {PageId} has a DOCTYPE, which page writes never accept, so nothing was written.",
                userAction: DoctypeUserAction),
            PageWriteProblemKind.InvalidXml => new OneNoteException(
                ErrorCode.InvalidXml,
                $"The XML for page {PageId} doesn't match the OneNote 2013 schema at {SchemaPlaces(problem.Schema ?? [])}, so nothing was written."),
            PageWriteProblemKind.PageIdMismatch => Invalid(
                $"The XML's root isn't a one:Page whose ID is the pageId argument ({PageId}), so nothing was written.",
                PageIdUserAction),
            PageWriteProblemKind.UnknownObjectId => new OneNoteException(
                ErrorCode.UnknownObjectId,
                Named(
                    $"Page {PageId} has no {IdList(ids)}, which the XML sends, so nothing was written.",
                    $"{N(ids.Count)} objectID(s) the XML sends aren't on page {PageId}, so nothing was written.")),
            PageWriteProblemKind.WrongElement => Invalid(
                Named(
                    $"On page {PageId}, {IdList(ids)} is sent on a different kind of element than the page has, so nothing was written.",
                    $"On page {PageId}, {N(ids.Count)} objectID(s) are sent on a different kind of element than the page has, so nothing was written."),
                WrongElementUserAction),
            PageWriteProblemKind.RemovalNotAnOe => Invalid(
                Named(
                    $"removeObjectIds names {IdList(ids)}, which isn't an OE inside an Outline this call sends to page {PageId}, so nothing was written.",
                    $"removeObjectIds names {N(ids.Count)} objectID(s) that aren't OEs inside an Outline this call sends to page {PageId}, so nothing was written."),
                RemovalUserAction),
            PageWriteProblemKind.RemovalAlsoSent => Invalid(
                Named(
                    $"removeObjectIds names {IdList(ids)}, which the XML also sends to page {PageId}, so nothing was written.",
                    $"removeObjectIds names {N(ids.Count)} objectID(s) that the XML also sends to page {PageId}, so nothing was written."),
                RemovalUserAction),
            PageWriteProblemKind.OeMoved => Invalid(
                Named(
                    $"On page {PageId}, OE or other element {IdList(ids)} is sent under a different Outline than the one it is in, so nothing was written.",
                    $"On page {PageId}, {N(ids.Count)} OE(s) or other element(s) are sent under a different Outline than the one they are in, so nothing was written."),
                MovedUserAction),
            _ => Invalid(
                Named(
                    $"On page {PageId}, the sent Outlines leave out OE {IdList(ids)}, which removeObjectIds doesn't name, so nothing was written: an OE left out would be deleted.",
                    $"On page {PageId}, the sent Outlines leave out {N(ids.Count)} OE(s) that removeObjectIds doesn't name, so nothing was written: an OE left out would be deleted."),
                OmittedUserAction),
        };
    }

    /// <summary>Line, position and element name of up to five schema problems, then how many more (Q3).</summary>
    private static string SchemaPlaces(IReadOnlyList<SchemaProblem> problems)
    {
        var places = problems.Take(5).Select(p =>
            string.Create(CultureInfo.InvariantCulture, $"line {p.Line}, position {p.Position}") + (p.DisplayName is { } name ? $" ({name})" : string.Empty));
        var text = string.Join("; ", places);
        return problems.Count > 5 ? string.Create(CultureInfo.InvariantCulture, $"{text}; and {problems.Count - 5} more places") : text;
    }
}
