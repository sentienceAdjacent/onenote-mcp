using System.Globalization;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>What <see cref="NavigateToUrlUseCase"/> logs for one call (AD-13): never the link, which carries notebook and section names.</summary>
/// <param name="CorrelationId">The tool call's correlation ID.</param>
/// <param name="LinkKind"><c>page</c>, <c>section</c> or <c>notebook</c> once the link is read; before that <c>empty</c>, <c>web</c>, <c>local</c>, <c>cloud</c> or <c>other</c>.</param>
/// <param name="Rule">The rule that refused the link (<see cref="CloudLinks.Rules"/>), or <c>none</c>.</param>
/// <param name="Outcome"><c>landed</c>, <c>landedElsewhere</c>, <c>landingUnconfirmed</c>, <c>refused</c>, or the error code of a failure.</param>
public sealed record NavigateToUrlLogEntry(string CorrelationId, string LinkKind, string Rule, string Outcome);

/// <summary>
/// <c>navigate_to_url</c> (story 4.7; spine AD-7 <c>AppState</c>, <c>navigate_to_url</c>): follows a <c>onenote:</c> link into an
/// open notebook stored online, through the <see cref="AppStatePipeline"/> (no lock; <c>ComBusy</c> at the start while an
/// abandoned write is outstanding; the 120 s deadline). User-visible (AD-11): it changes what OneNote shows and may bring
/// it to the front, so the skill calls it only when the user asks. No grant is needed (AD-21).
/// <list type="bullet">
/// <item>Step 1: <see cref="CloudLinks.Parse"/>; every refusal is <see cref="ErrorCode.InvalidArgument"/> with a userAction,
/// before anything is read or sent.</item>
/// <item>Step 3: one <c>GetHierarchy</c> of the open notebooks at <c>Sections</c>, and <see cref="CloudLinks.Match"/>: a link
/// outside every open notebook, a <c>base-path</c> into another notebook, a <c>.one</c> that isn't an open section, or a
/// folder that isn't the notebook itself is refused the same way, and nothing is shown.</item>
/// <item>Step 7: <c>NavigateToUrl</c> with <c>fNewWindow</c> false, or with a <c>window</c> handle that window's
/// <c>Window.NavigateToUrl</c> (found and called in one STA call; an unknown handle is <see cref="ErrorCode.InvalidArgument"/>
/// and nothing is sent, as story 4.6's), never retried; a timeout, disconnect, cancel or
/// <c>0x800706BE</c> is <c>outcomeUnknown</c>. <c>0x80042014</c> stays <see cref="ErrorCode.ObjectNotFound"/> with
/// <see cref="ObjectNotFoundUserAction"/> (AD-9).</item>
/// <item>Then, outside the pipeline, the landing check (Design Notes): the navigation's outcome is known, so its reads
/// (<c>CallKind.Read</c>, the call timeout) never turn the call into <c>outcomeUnknown</c>. One windows read; the
/// <c>window</c> argument's window, else OneNote's current window; then by kind, a page link's <c>page-id</c> against <c>GetHyperlinkToObject</c> of the window's page, a
/// section link's section ID against <c>CurrentSectionId</c>, a notebook link's against <c>CurrentNotebookId</c>. A match
/// gives <see cref="ToolWarnings.Landed"/>; a mismatch, or no page where the link names one,
/// <see cref="ToolWarnings.LandedElsewhere"/>; reads that fail, no window, or the <c>window</c> argument's window gone,
/// <see cref="ToolWarnings.LandingUnconfirmed"/>.
/// The call stays ok in every case.</item>
/// </list>
/// The result's <c>meta.windows</c> holds the window the check read, its page's ID withheld under AD-21 as
/// <c>get_windows</c> withholds it (with <see cref="ToolWarnings.ReadNotGranted"/>). Messages say "OneNote's current page is …",
/// never "OneNote shows …": a cloud landing in the background didn't repaint until the user pointed at OneNote (spike 4.12
/// N1 C). No content is read or returned. Logs carry the link's kind, the refusing rule and the outcome, never the link.
/// </summary>
public sealed class NavigateToUrlUseCase(IOneNoteGateway gateway, AppStatePipeline pipeline, ReadAccess access, Action<NavigateToUrlLogEntry>? log = null) : IUseCase
{
    /// <summary>The tool's name (AD-14).</summary>
    public const string OperationName = "navigate_to_url";

    /// <summary>
    /// The userAction for <c>0x80042014</c> from <c>NavigateToUrl</c> (AD-9): ambiguous, since it came from a declined
    /// security notice, a web link, and a cloud repeat inside about a second (spike 4.12 H, R1 C).
    /// </summary>
    public const string ObjectNotFoundUserAction =
        "The link may name something that no longer exists. If navigate_to_url was just called, wait a second and try once more; otherwise ask the user whether OneNote showed a message.";

    /// <summary>The <see cref="ToolWarnings.LandedElsewhere"/> userAction.</summary>
    public const string LandedElsewhereUserAction =
        "Tell the user OneNote didn't reach what the link names: it may have been deleted or moved. Re-read the hierarchy and get a fresh link with get_hyperlink_to_object, or call navigate_to with the object's ID.";

    /// <summary>The <see cref="ToolWarnings.LandedElsewhere"/> userAction when the link's section is locked (spike 4.12 N6).</summary>
    public const string LockedUserAction =
        "The section is locked with a password, so OneNote shows it without its pages; following a link unlocks nothing. Ask the user to unlock it in OneNote.";

    /// <summary>The <see cref="ToolWarnings.LandingUnconfirmed"/> userAction.</summary>
    public const string UnconfirmedUserAction =
        "Ask the user what OneNote shows before relying on it, or call get_windows if they ask which page they're on.";

    /// <summary>Appended to every landing message: why it never says what OneNote shows.</summary>
    public const string RepaintNote =
        " With OneNote in the background, its window may not repaint until the user points at it.";

    public OperationClass Class => OperationClass.AppState;

    /// <summary>Follows <paramref name="url"/> without a window argument: app-level <c>NavigateToUrl</c>.</summary>
    public Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string url, CancellationToken ct) =>
        ExecuteAsync(context, url, window: null, ct);

    /// <summary>Follows <paramref name="url"/>.</summary>
    /// <param name="window">
    /// Story 4.7: a window's handle as <c>get_windows</c> returns it, so only that window moves (<c>Window.NavigateToUrl</c>); or
    /// null for app-level <c>NavigateToUrl</c>, whose landing is read from OneNote's current window.
    /// </param>
    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string url, int? window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(access);

        // AD-21: the grant must be held when the call starts.
        var callStart = access.Now();
        var follow = new Follow(gateway, url ?? string.Empty, window);
        CloudLinkTarget target;
        try
        {
            target = await pipeline.RunAsync(follow, ct, context.CorrelationId).ConfigureAwait(false);
        }
        catch (OneNoteException ex)
        {
            Log(context, follow, follow.Rule == CloudLinks.Rules.None ? ex.Code.ToString() : "refused");
            if (ex.Code == ErrorCode.ObjectNotFound && ex.ComHResult == OneNoteHResults.ObjectDoesNotExist)
            {
                throw new OneNoteException(ex.Code, ex.Message, ex.ComHResult, ex, ex.OutcomeUnknown, ObjectNotFoundUserAction);
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            Log(context, follow, nameof(ErrorCode.Cancelled));
            throw;
        }

        var landing = await LandingAsync(target, window, callStart, ct).ConfigureAwait(false);
        Log(context, follow, landing.Code);
        return new ToolSuccess(null, new ToolMeta(context.CorrelationId, Windows: landing.Windows), landing.Warnings);
    }

    private void Log(ToolCallContext context, Follow follow, string outcome) =>
        log?.Invoke(new NavigateToUrlLogEntry(context.CorrelationId, follow.LinkKind, follow.Rule, outcome));

    /// <summary>The landing check's outcome: the warning code (for the log), the window read, and the warnings.</summary>
    private sealed record Landing(string Code, IReadOnlyList<WindowListing>? Windows, IReadOnlyList<ToolWarning> Warnings);

    /// <summary>The landing check (Decision OQ1; spine AD-7 <c>AppState</c>, The landing check): never throws, never a settle.</summary>
    private async Task<Landing> LandingAsync(CloudLinkTarget target, int? handle, DateTimeOffset callStart, CancellationToken ct)
    {
        IReadOnlyList<OneNoteWindow> windows;
        try
        {
            windows = await gateway.GetWindowsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OneNoteException or OperationCanceledException)
        {
            return Unconfirmed(null, "OneNote's windows couldn't be read after it took the link.");
        }

        var window = handle is { } h
            ? windows.FirstOrDefault(w => OneNoteWindow.HandleOf(w.Handle) == h)
            : windows.FirstOrDefault(w => w.Current);
        if (window is null)
        {
            return Unconfirmed(
                [],
                handle is { } gone
                    ? string.Create(CultureInfo.InvariantCulture, $"the window it was sent to (handle {gone}) was no longer open after it took the link.")
                    : "no OneNote window was open after it took the link.");
        }

        WindowListing listing;
        bool withheld;
        try
        {
            (listing, withheld) = await new WindowPages(gateway, access, callStart).ListAsync(window, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OneNoteException or OperationCanceledException)
        {
            // Fail closed (AD-21): a page whose section couldn't be checked isn't named.
            (listing, withheld) = (WindowPages.Listing(window, null), WindowPages.Present(window.PageId) is not null);
        }

        IReadOnlyList<WindowListing> listed = [listing];
        var current = Describe(listing, withheld);
        bool? same = target.Kind switch
        {
            CloudLinkKind.Notebook => string.Equals(window.NotebookId, target.NotebookId, StringComparison.Ordinal),
            CloudLinkKind.Section => string.Equals(window.SectionId, target.SectionId, StringComparison.Ordinal),
            // Under AD-21 a withheld page's link is never read: comparing it would tell the caller whether the protected
            // page is the one the link names (2026-11-23 security review), so the landing stays unconfirmed.
            _ when withheld => null,
            _ => await SamePageAsync(window, target, ct).ConfigureAwait(false),
        };

        var warnings = new List<ToolWarning>();
        string code;
        if (same is null)
        {
            code = ToolWarnings.LandingUnconfirmed;
            var why = withheld
                ? "the window's page is in a protected section with no read grant, so its link isn't read."
                : "the page's link couldn't be read.";
            warnings.Add(new ToolWarning(code, "OneNote took the link, but whether it reached the page couldn't be checked: " + why + " " + current + RepaintNote, UnconfirmedUserAction));
        }
        else if (same.Value)
        {
            code = ToolWarnings.Landed;
            warnings.Add(new ToolWarning(code, "OneNote took the link and reached what it names. " + current + RepaintNote));
        }
        else
        {
            code = ToolWarnings.LandedElsewhere;
            var locked = target.SectionLocked && window.PageId.Length == 0;
            warnings.Add(new ToolWarning(
                code,
                "OneNote took the link, but its current " + CloudLinks.KindName(target.Kind) + " isn't the one the link names. " + current
                    + (locked ? " The link's section is locked: a locked section shows no page, and following a link unlocks nothing." : string.Empty)
                    + RepaintNote,
                locked ? LockedUserAction : LandedElsewhereUserAction));
        }

        if (withheld)
        {
            warnings.Add(new ToolWarning(ToolWarnings.ReadNotGranted, GetWindowsUseCase.WithheldMessage(1), ReadAccess.WithheldUserAction));
        }

        return new Landing(code, listed, warnings);
    }

    /// <summary>
    /// A page link: the window's page's <c>page-id</c>, from <c>GetHyperlinkToObject</c>, against the link's; false when the
    /// window has no page (a locked section, N6), null when the link can't be read or carries no <c>page-id</c>.
    /// </summary>
    private async Task<bool?> SamePageAsync(OneNoteWindow window, CloudLinkTarget target, CancellationToken ct)
    {
        if (window.PageId.Length == 0)
        {
            return false;
        }

        try
        {
            var link = await gateway.GetHyperlinkToObjectAsync(window.PageId, string.Empty, ct).ConfigureAwait(false);
            return CloudLinks.PageGuidOf(link) is { } guid ? guid == target.PageGuid : null;
        }
        catch (Exception ex) when (ex is OneNoteException or OperationCanceledException)
        {
            return null;
        }
    }

    private static Landing Unconfirmed(IReadOnlyList<WindowListing>? windows, string why) =>
        new(
            ToolWarnings.LandingUnconfirmed,
            windows,
            [new ToolWarning(ToolWarnings.LandingUnconfirmed, "OneNote took the link, but whether it reached what the link names couldn't be checked: " + why, UnconfirmedUserAction)]);

    /// <summary>"OneNote's current page is …", with IDs only (AD-9), and the page left out under AD-21.</summary>
    internal static string Describe(WindowListing window, bool withheld)
    {
        ArgumentNullException.ThrowIfNull(window);
        var where = string.Create(
            CultureInfo.InvariantCulture,
            $"section {window.SectionId ?? "(none)"} of notebook {window.NotebookId ?? "(none)"}, in window {window.Handle}");
        if (window.PageId is { } page)
        {
            return $"OneNote's current page is {page}, in {where}.";
        }

        return withheld
            ? $"OneNote's current page is in {where}; its ID is left out, because its section is protected (see readNotGranted)."
            : $"OneNote's current page is none: the window is on {where}, with no page.";
    }

    private sealed class Follow(IOneNoteGateway gateway, string url, int? window) : AppStateOperation<CloudLinkTarget>
    {
        private CloudLink? link;
        private CloudLinkTarget? target;

        /// <summary>The link's kind for the log (never the link).</summary>
        public string LinkKind { get; private set; } = CloudLinks.KindOf(url);

        /// <summary>The rule that refused the link, or <see cref="CloudLinks.Rules.None"/>.</summary>
        public string Rule { get; private set; } = CloudLinks.Rules.None;

        public override void Validate()
        {
            // Step 1, before anything is read or sent.
            Refuse(CloudLinks.Parse(url, out link));
            LinkKind = CloudLinks.KindName(link!.Kind);
        }

        public override async Task ReadAsync(CancellationToken ct)
        {
            // Step 3: one read of the open notebooks with their sections and section groups.
            var xml = await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Sections, ct).ConfigureAwait(false);
            Refuse(CloudLinks.Match(link ?? throw new InvalidOperationException("Step 1 didn't run."), xml, out target));
        }

        public override async Task<CloudLinkTarget> CallAsync(CancellationToken ct)
        {
            var matched = target ?? throw new InvalidOperationException("Step 3 didn't run.");

            // Step 7: the link as given (trimmed), never retried; with a window, only that window moves.
            if (window is { } handle)
            {
                await gateway.NavigateToUrlAsync(url.Trim(), handle, ct).ConfigureAwait(false);
            }
            else
            {
                await gateway.NavigateToUrlAsync(url.Trim(), ct).ConfigureAwait(false);
            }
            return matched;
        }

        private void Refuse(CloudLinkRefusal? refusal)
        {
            if (refusal is null)
            {
                return;
            }

            Rule = refusal.Rule;
            throw new OneNoteException(ErrorCode.InvalidArgument, refusal.Message, userAction: refusal.UserAction, retryable: false);
        }
    }
}
