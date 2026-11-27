using System.Globalization;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>close_notebook</c> (story 4.4; AD-7 <c>AppState</c>, <c>close_notebook</c>): closes an open notebook in OneNote,
/// through the <see cref="AppStatePipeline"/> under the write lock (AD-18; Phill, 2026-11-22, OQ1 a). User-visible (AD-11):
/// a close of the notebook on screen leaves OneNote showing nothing (spike 4.1 C4), and a cloud close with an edit pending
/// shows OneNote's "syncing changes" box (C2 C). No grant is needed and none is dropped (AD-6).
/// <list type="bullet">
/// <item>Step 1: an empty ID is <see cref="ErrorCode.InvalidArgument"/>.</item>
/// <item>Step 3, under the lock: one read of the open notebooks. An ID none of them has is checked with one
/// <c>GetHierarchy(id, self)</c>: a section, section group or page is <see cref="ErrorCode.InvalidArgument"/>, anything else
/// (unknown, a closed notebook's old ID) <see cref="ErrorCode.ObjectNotFound"/>, both with a userAction to re-read the
/// notebooks, and nothing that changes state is sent.</item>
/// <item>Step 7: <c>SyncHierarchy</c> of the notebook, a settle of <see cref="SettleTime"/> (counted against the 120 s
/// deadline), then <c>CloseNotebook</c>, never forced (OQ2 b; spike 4.1 C2's lost local edit). A failure, timeout, deadline or
/// cancel in the sync or the settle stops before <c>CloseNotebook</c>: the notebook stays open, and it is never
/// <c>outcomeUnknown</c>. A <c>CloseNotebook</c> timeout, disconnect, cancel or <c>0x800706BE</c> is <c>outcomeUnknown</c>,
/// and the gateway tracks it as an abandoned write, so the pipeline keeps the lock until it settles (AD-3, AD-18). Any other
/// <c>CloseNotebook</c> failure keeps its AD-9 code, with <see cref="CloseUserAction"/>.</item>
/// </list>
/// The result's <c>xml</c> is the notebook's <c>one:Notebook</c> element as read under the lock, with <c>meta.objectId</c>,
/// <c>meta.path</c> and the warning <see cref="ToolWarnings.NotebookClosed"/> (4.4 plan Q1 a), whose userAction gives the
/// reopen route by <see cref="NotebookFolderPath"/>'s rule, the one <c>open_notebook</c> applies (round-3 retrospective F5):
/// an http path in OneNote, a path that rule takes through <c>open_notebook</c>, any other in OneNote. The search page-text
/// cache is left alone (OQ6 a). Nothing is retried.
/// </summary>
public sealed class CloseNotebookUseCase : IUseCase
{
    private readonly IOneNoteGateway gateway;
    private readonly AppStatePipeline pipeline;
    private readonly Func<TimeSpan, CancellationToken, Task>? settle;
    private readonly Func<OneDriveFolders> oneDrive;
    private readonly FilePathGate.ILinkReader? links;

    /// <param name="gateway">Reads the open notebooks, syncs and closes.</param>
    /// <param name="pipeline">The AD-7 <c>AppState</c> step runner, with the write lock.</param>
    /// <param name="settle">The wait between the sync and the close; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> when null.</param>
    /// <param name="oneDrive">The OneDrive folders on this machine, for the reopen route; none known when null.</param>
    public CloseNotebookUseCase(IOneNoteGateway gateway, AppStatePipeline pipeline, Func<TimeSpan, CancellationToken, Task>? settle = null, Func<OneDriveFolders>? oneDrive = null)
        : this(gateway, pipeline, settle, oneDrive, links: null)
    {
    }

    /// <summary>The seam unit tests use: links and drive types faked.</summary>
    internal CloseNotebookUseCase(
        IOneNoteGateway gateway,
        AppStatePipeline pipeline,
        Func<TimeSpan, CancellationToken, Task>? settle,
        Func<OneDriveFolders>? oneDrive,
        FilePathGate.ILinkReader? links)
    {
        this.gateway = gateway;
        this.pipeline = pipeline;
        this.settle = settle;
        this.oneDrive = oneDrive ?? (() => OneDriveFolders.None);
        this.links = links;
    }

    /// <summary>The settle between the sync and the close: about 10 s (AD-7 <c>AppState</c>; Phill, 2026-11-22, OQ2 b). Unmeasured; the sitting checks it.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(10);

    /// <summary>What to do when OneNote refused the close or it may not have happened (AD-7 <c>AppState</c>).</summary>
    public const string CloseUserAction =
        "Let the notebook finish syncing and retry, or ask the user to close it in OneNote. close_notebook never forces a close.";

    /// <summary>What to do with an ID that isn't an open notebook's: a reopen changes every ID.</summary>
    public const string ReReadUserAction =
        "Re-read the open notebooks (get_hierarchy with scope notebooks) and pass an open notebook's current ID: a notebook gets new IDs each time it is opened.";

    /// <summary>The warning's message.</summary>
    public static readonly string ClosedMessage = string.Create(
        CultureInfo.InvariantCulture,
        $"OneNote closed the notebook. The server asked OneNote to sync it and waited about {SettleTime.TotalSeconds:0} s before closing it. ")
        + "Every ID in the notebook changes when it is opened again: the old IDs then fail with ObjectNotFound. OneNote may also reset its colour. "
        + "If OneNote was showing the notebook, its view may now be blank. Its read and write grants are kept, matched by its path.";

    /// <summary>How to reopen a local notebook whose path <c>open_notebook</c> takes.</summary>
    public const string ReopenLocalUserAction =
        "To reopen it, use open_notebook with meta.path, or the user opens it in OneNote (File > Open > Browse, then the notebook's folder). Then re-read the hierarchy for its new IDs.";

    /// <summary>
    /// How to reopen a notebook whose path <c>open_notebook</c> refuses (<see cref="NotebookFolderPath"/>): a network, device
    /// or relative path, or a folder inside OneDrive (round-3 retrospective F5).
    /// </summary>
    public const string ReopenInOneNoteUserAction =
        "It is kept at a network path, inside a OneDrive-synced folder, or at another path open_notebook refuses, so the user reopens it in OneNote (File > Open > Browse, then the notebook's folder). Then re-read the hierarchy for its new IDs.";

    /// <summary>How to reopen a cloud notebook: the server refuses URLs (AD-7 <c>AppState</c>).</summary>
    public const string ReopenCloudUserAction =
        "It is stored online, so the user reopens it in OneNote (File > Open, under the account that stores it); this server doesn't open a notebook by URL. Then re-read the hierarchy for its new IDs.";

    public OperationClass Class => OperationClass.AppState;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string notebookId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pipeline);

        var closed = await pipeline.RunAsync(new Close(gateway, notebookId ?? string.Empty, settle ?? DelayAsync), ct, context.CorrelationId).ConfigureAwait(false);
        return new ToolSuccess(
            closed.Xml,
            new ToolMeta(context.CorrelationId, Path: closed.Path, ObjectId: closed.Id),
            [new ToolWarning(ToolWarnings.NotebookClosed, ClosedMessage, ReopenRoute(closed.Path))]);
    }

    /// <summary>
    /// The reopen route for a closed notebook's path: in OneNote for an http path; through <c>open_notebook</c> when
    /// <see cref="NotebookFolderPath"/>'s rule takes it; otherwise in OneNote. The rule only reads the file system; any failure
    /// there gives the OneNote route, never an error, as the close has already happened.
    /// </summary>
    private string ReopenRoute(string path)
    {
        if (UrlPath.IsHttp(path))
        {
            return ReopenCloudUserAction;
        }

        try
        {
            return NotebookFolderPath.Openable(path, oneDrive(), links) ? ReopenLocalUserAction : ReopenInOneNoteUserAction;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return ReopenInOneNoteUserAction;
        }
    }

    private static Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    /// <summary>The closed notebook as step 3 read it.</summary>
    private sealed record Closed(string Id, string Path, string Xml);

    private sealed class Close(IOneNoteGateway gateway, string notebookId, Func<TimeSpan, CancellationToken, Task> settle) : AppStateOperation<Closed>
    {
        private Closed? notebook;

        public override bool TakesWriteLock => true;

        public override bool MarksStateChange => true;

        public override void Validate()
        {
            if (notebookId.Length == 0)
            {
                throw new OneNoteException(ErrorCode.InvalidArgument, "notebookId is empty: pass the ID of an open notebook.", userAction: ReReadUserAction);
            }
        }

        public override async Task ReadAsync(CancellationToken ct)
        {
            // One read of the open notebooks, under the lock (AD-7 AppState).
            var notebooksXml = await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, ct).ConfigureAwait(false);
            var open = XmlReads.Readable(() => HierarchyNode.Notebooks(notebooksXml), "GetHierarchy").FirstOrDefault(n => string.Equals(n.Id, notebookId, StringComparison.Ordinal));
            if (open is not null)
            {
                notebook = new Closed(open.Id, open.Path, HierarchyNode.NotebookElement(notebooksXml, notebookId)!);
                return;
            }

            // Not an open notebook: one self read tells a section, section group or page from an ID that is gone.
            HierarchyKind kind;
            try
            {
                var selfXml = await gateway.GetHierarchyAsync(notebookId, HierarchyScope.Self, ct).ConfigureAwait(false);
                kind = XmlReads.Readable(() => HierarchyTarget.Read(selfXml), "GetHierarchy").Kind;
            }
            catch (OneNoteException ex) when (ex.Code == ErrorCode.ObjectNotFound)
            {
                throw NotOpen(ex);
            }

            if (kind is HierarchyKind.Section or HierarchyKind.SectionGroup or HierarchyKind.Page)
            {
                var what = kind switch
                {
                    HierarchyKind.Section => "a section",
                    HierarchyKind.SectionGroup => "a section group",
                    _ => "a page",
                };
                throw new OneNoteException(
                    ErrorCode.InvalidArgument,
                    $"{notebookId} is {what}, not a notebook: close_notebook closes a whole notebook only. Nothing was sent to OneNote.",
                    userAction: ReReadUserAction);
            }

            throw NotOpen(null);
        }

        public override async Task<Closed> CallAsync(CancellationToken ct)
        {
            var target = notebook ?? throw new InvalidOperationException("Step 3 didn't run.");

            // The sync and settle first (OQ2 b): a failure here closed nothing, so it is never outcomeUnknown.
            try
            {
                await gateway.SyncHierarchyAsync(notebookId, ct).ConfigureAwait(false);
            }
            catch (OneNoteException ex)
            {
                throw new OneNoteException(
                    ex.Code,
                    $"{ex.Message} The notebook wasn't closed: close_notebook stops when the sync before the close fails.",
                    ex.ComHResult,
                    ex,
                    userAction: ex.UserAction,
                    retryable: ex.Retryable ?? (ex.Code is ErrorCode.Timeout or ErrorCode.ComUnavailable or ErrorCode.Cancelled ? true : null));
            }

            await settle(SettleTime, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // The close, never forced, never retried (AD-3).
            MarkStateChangeSent();
            try
            {
                await gateway.CloseNotebookAsync(notebookId, ct).ConfigureAwait(false);
            }
            catch (OneNoteException ex) when (WritePipeline.IsAmbiguous(ex))
            {
                // The pipeline makes it outcomeUnknown; the advice goes in the message, as the envelope's userAction is then fixed (AD-9).
                throw new OneNoteException(
                    ex.Code,
                    $"{ex.Message} The close may still finish in OneNote: re-read the open notebooks before anything else; if it is still open, let it finish syncing, or ask the user to close it in OneNote.",
                    ex.ComHResult,
                    ex,
                    userAction: CloseUserAction);
            }
            catch (OneNoteException ex)
            {
                // Only a refusal made while OneNote held the notebook shows it is still open (round-3 retrospective F8): the
                // user may have closed it by hand during the sync or settle, and that HRESULT was never probed.
                var after = ex.Code is ErrorCode.ComBusy or ErrorCode.ModalDialogOpen
                    ? "The notebook is still open."
                    : "Re-read the open notebooks to see whether it is still open.";
                throw new OneNoteException(ex.Code, $"{ex.Message} {after}", ex.ComHResult, ex, ex.OutcomeUnknown, CloseUserAction, ex.Retryable);
            }

            return target;
        }

        private OneNoteException NotOpen(OneNoteException? inner) => new(
            ErrorCode.ObjectNotFound,
            $"{notebookId} isn't the ID of an open notebook. Nothing was sent to OneNote.",
            innerException: inner,
            userAction: ReReadUserAction);
    }
}
