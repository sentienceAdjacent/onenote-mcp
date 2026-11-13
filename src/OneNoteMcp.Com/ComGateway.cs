using System.Globalization;
using System.Runtime.InteropServices;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;
using CoreNewPageStyle = OneNoteMcp.Core.NewPageStyle;
using CorePageInfo = OneNoteMcp.Core.PageInfo;
using CoreScope = OneNoteMcp.Core.HierarchyScope;
using CoreSpecialLocation = OneNoteMcp.Core.SpecialLocation;
using InteropNewPageStyle = OneNoteMcp.Com.Interop.NewPageStyle;
using InteropPageInfo = OneNoteMcp.Com.Interop.PageInfo;
using InteropScope = OneNoteMcp.Com.Interop.HierarchyScope;
using InteropSpecialLocation = OneNoteMcp.Com.Interop.SpecialLocation;

namespace OneNoteMcp.Com;

/// <summary>
/// The only real <see cref="IOneNoteGateway"/> adapter (AD-2). A supervisor over replaceable STA workers
/// (<see cref="StaThread"/>, one thread and one COM reference each), applying every AD-3 rule:
/// <list type="bullet">
/// <item>One live worker at a time. Each call's deadline (<c>timeouts.call</c>) covers its queue wait plus the call.</item>
/// <item>A call that outlives its deadline returns <see cref="ErrorCode.Timeout"/> and poisons its worker: queued calls
/// fail <see cref="ErrorCode.ComBusy"/>, and the next call gets a fresh worker. A poisoned worker releases its COM
/// reference and exits if its call ever returns. While <see cref="MaxAbandonedWorkers"/> are outstanding, every call
/// returns <see cref="ErrorCode.ComUnavailable"/> without touching COM.</item>
/// <item>A write (<c>CreateNewPage</c>, <c>UpdatePageContent</c>, <c>DeletePageContent</c>, or the raw seam without the
/// disconnect retry) that started in OneNote and was then left, by its deadline or its caller's cancel, is outstanding
/// until it returns. Meanwhile every call but a read (writes, <c>NavigateTo</c>) returns <see cref="ErrorCode.ComBusy"/>
/// without touching COM, and <see cref="WaitForAbandonedWritesAsync"/> stays pending. A write abandoned while still
/// queued never ran and isn't tracked.</item>
/// <item>A disconnect drops the reference, re-runs every activation rule and retries a read once; never a write or
/// <c>NavigateTo</c>.</item>
/// <item>Busy is retried with backoff until just before the deadline, then returns <see cref="ErrorCode.ComBusy"/>, so
/// busy never poisons a worker. <c>0x8004201D</c> returns <see cref="ErrorCode.ComBusy"/> at once.</item>
/// <item>Activation: process check (unless <c>allowSilentActivation</c>; an <c>ONENOTE.EXE</c> Windows Error Reporting is
/// handling doesn't count as running), integrity check, then <c>CoCreateInstance</c>
/// on the worker under <c>timeouts.activation</c>. Settings are re-read at every activation.</item>
/// <item>The COM reference is released after <c>timeouts.idleRelease</c> with no call.</item>
/// </list>
/// Failures map through <see cref="HResultMap"/> (AD-9). It never kills or closes OneNote.
/// </summary>
public sealed class ComGateway : IOneNoteGateway, IDisposable
{
    /// <summary>Outstanding abandoned workers at which the gateway stops calling COM (AD-3).</summary>
    internal const int MaxAbandonedWorkers = 2;

    internal static readonly TimeSpan FirstBackoff = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(1);

    private const string ActivationWhat = "Connecting to OneNote";

    // Attempts that never reached COM (the reference was released under them) retry without spending the read's one retry.
    private const int MaxAttempts = 4;

    private readonly IProcessProbe probe;
    private readonly Func<GatewaySettings> readSettings;
    private readonly Func<object> createApplication;
    private readonly Lock gate = new();
    private readonly SemaphoreSlim activationLock = new(1, 1);
    private readonly Timer idleTimer;

    // Guarded by gate.
    private StaThread? worker;
    private GatewaySettings settings = GatewaySettings.Default;
    private int abandoned;
    private int inFlight;
    private int workerSerial;
    private bool disposed;
    private int outstandingWrites;
    private int abandonedWritesTracked;
    private TaskCompletionSource? writesSettled;

    public ComGateway(IProcessProbe probe, SettingsStore settings)
        : this(probe, () => GatewaySettings.From(settings.Read()), CreateApplication)
    {
        ArgumentNullException.ThrowIfNull(settings);
    }

    /// <summary>Test seam: <paramref name="createApplication"/> replaces COM activation; settings are the defaults.</summary>
    internal ComGateway(IProcessProbe probe, Func<object> createApplication)
        : this(probe, () => GatewaySettings.Default, createApplication)
    {
    }

    /// <summary>Test seam: <paramref name="readSettings"/> is read at every activation, like <see cref="SettingsStore"/>.</summary>
    internal ComGateway(IProcessProbe probe, Func<GatewaySettings> readSettings, Func<object> createApplication)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(readSettings);
        ArgumentNullException.ThrowIfNull(createApplication);
        this.probe = probe;
        this.readSettings = readSettings;
        this.createApplication = createApplication;
        idleTimer = new Timer(_ => OnIdle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Abandoned workers whose hung call hasn't returned yet.</summary>
    internal int AbandonedWorkers
    {
        get
        {
            lock (gate)
            {
                return abandoned;
            }
        }
    }

    /// <summary>Whether the live worker holds a COM reference.</summary>
    internal bool HasApplication
    {
        get
        {
            lock (gate)
            {
                return worker?.HasApplication ?? false;
            }
        }
    }

    public Task<string> GetHierarchyAsync(string startNodeId, CoreScope scope, CancellationToken ct)
    {
        var interopScope = MapScope(scope);
        return RunAsync(
            nameof(IApplication.GetHierarchy),
            app =>
            {
                app.GetHierarchy(startNodeId ?? string.Empty, interopScope, out var xml, XMLSchema.xs2013);
                return xml;
            },
            CallKind.Read,
            ct);
    }

    public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.GetHierarchyParent),
            app =>
            {
                app.GetHierarchyParent(objectId ?? string.Empty, out var parent);
                return parent;
            },
            CallKind.Read,
            ct);

    public Task<string> GetPageContentAsync(string pageId, CorePageInfo pageInfo, CancellationToken ct)
    {
        var interopInfo = MapPageInfo(pageInfo);
        return RunAsync(
            nameof(IApplication.GetPageContent),
            app =>
            {
                app.GetPageContent(pageId ?? string.Empty, out var xml, interopInfo, XMLSchema.xs2013);
                return xml;
            },
            CallKind.Read,
            ct);
    }

    public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.GetBinaryPageContent),
            app =>
            {
                app.GetBinaryPageContent(pageId ?? string.Empty, callbackId ?? string.Empty, out var base64);
                return base64;
            },
            CallKind.Read,
            ct);

    public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.FindPages),
            app =>
            {
                // fDisplay is always false: search never shows results in OneNote's UI (AD-11).
                app.FindPages(startNodeId ?? string.Empty, query ?? string.Empty, out var xml, includeUnindexed, fDisplay: false, XMLSchema.xs2013);
                return xml;
            },
            CallKind.Read,
            ct);

    public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.FindMeta),
            app =>
            {
                app.FindMeta(startNodeId ?? string.Empty, name ?? string.Empty, out var xml, includeUnindexed, XMLSchema.xs2013);
                return xml;
            },
            CallKind.Read,
            ct);

    public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.GetHyperlinkToObject),
            app =>
            {
                app.GetHyperlinkToObject(hierarchyId ?? string.Empty, pageContentObjectId ?? string.Empty, out var link);
                return link;
            },
            CallKind.Read,
            ct);

    public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.GetWebHyperlinkToObject),
            app =>
            {
                app.GetWebHyperlinkToObject(hierarchyId ?? string.Empty, pageContentObjectId ?? string.Empty, out var link);
                return link;
            },
            CallKind.Read,
            ct);

    public Task<string> GetSpecialLocationAsync(CoreSpecialLocation location, CancellationToken ct)
    {
        var interopLocation = MapSpecialLocation(location);
        return RunAsync(
            nameof(IApplication.GetSpecialLocation),
            app =>
            {
                app.GetSpecialLocation(interopLocation, out var path);
                return path;
            },
            CallKind.Read,
            ct);
    }

    public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.NavigateTo),
            app =>
            {
                // fNewWindow is always false: never open a window the user didn't ask for (AD-11).
                app.NavigateTo(hierarchyId ?? string.Empty, objectId ?? string.Empty, fNewWindow: false);
                return true;
            },
            // AppState, not a read: a call that reached COM and then lost the connection isn't repeated (AD-3).
            CallKind.AppState,
            ct);

    public Task<string> CreateNewPageAsync(string sectionId, CoreNewPageStyle style, CancellationToken ct)
    {
        var interopStyle = MapNewPageStyle(style);
        return RunAsync(
            nameof(IApplication.CreateNewPage),
            app =>
            {
                app.CreateNewPage(sectionId ?? string.Empty, out var pageId, interopStyle);
                return pageId;
            },
            // A write is never retried after a disconnect (AD-3): the pipeline reports outcomeUnknown instead.
            CallKind.Write,
            ct);
    }

    public Task<string> CreateSectionAsync(string parentId, string sectionFileName, CancellationToken ct)
    {
        const string method = nameof(IApplication.OpenHierarchy);
        if (string.IsNullOrEmpty(parentId))
        {
            return Task.FromException<string>(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: no parent ID was given for the new section.",
                retryable: false));
        }

        if (SectionFileNames.Problem(sectionFileName) is { } problem)
        {
            return Task.FromException<string>(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: the new section's file name {problem}.",
                userAction: NameUserAction,
                retryable: false));
        }

        return RunAsync(
            method,
            app =>
            {
                // Relative to the parent's ID only, never an absolute path (AD-7 Create row). An existing name returns the
                // existing section's ID (spike 5.1 constraint 5).
                app.OpenHierarchy(sectionFileName, parentId, out var sectionId, CreateFileType.cftSection);
                return sectionId;
            },
            // A write is never retried after a disconnect (AD-3): the pipeline reports outcomeUnknown instead.
            CallKind.Write,
            ct);
    }

    public Task<string> CreateSectionGroupAsync(string parentId, string groupName, CancellationToken ct)
    {
        const string method = nameof(IApplication.OpenHierarchy);
        if (string.IsNullOrEmpty(parentId))
        {
            return Task.FromException<string>(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: no parent ID was given for the new section group.",
                retryable: false));
        }

        if (SectionFileNames.NameProblem(groupName) is { } problem)
        {
            return Task.FromException<string>(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: the new section group's name {problem}.",
                userAction: NameUserAction,
                retryable: false));
        }

        return RunAsync(
            method,
            app =>
            {
                // Relative to the parent's ID only, never an absolute path (AD-7 Create row).
                app.OpenHierarchy(groupName, parentId, out var groupId, CreateFileType.cftFolder);
                return groupId;
            },
            // A write is never retried after a disconnect (AD-3): the pipeline reports outcomeUnknown instead.
            CallKind.Write,
            ct);
    }

    public Task<string> CreateNotebookAsync(string folderPath, CancellationToken ct)
    {
        const string method = nameof(IApplication.OpenHierarchy);
        if (NotebookFolderProblem(folderPath) is { } problem)
        {
            return Task.FromException<string>(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: the new notebook's folder {problem}.",
                userAction: "Create a notebook only in a local folder (C:\\…), named with no characters a Windows folder name can't hold.",
                retryable: false));
        }

        return RunAsync(
            method,
            app =>
            {
                // An absolute local folder, no parent ID (AD-7 Create row). The caller checked it is absent or empty.
                app.OpenHierarchy(folderPath, string.Empty, out var notebookId, CreateFileType.cftNotebook);
                return notebookId;
            },
            // A write is never retried after a disconnect (AD-3): the pipeline reports outcomeUnknown instead.
            CallKind.Write,
            ct);
    }

    private const string NameUserAction = "Use a name with no path separators, no '..' and no characters a Windows file name can't hold.";

    /// <summary>
    /// Why <paramref name="folderPath"/> can't be a new notebook's folder, as a phrase that never quotes it; null when it is
    /// a fully qualified local path (not UNC, a device path or a URL) whose last segment is a valid name.
    /// </summary>
    private static string? NotebookFolderProblem(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath))
        {
            return "is empty";
        }

        var slashed = folderPath.Replace('/', '\\');
        if (slashed.StartsWith(@"\\", StringComparison.Ordinal) || slashed.StartsWith(@"\??\", StringComparison.Ordinal)
            || !System.IO.Path.IsPathFullyQualified(slashed) || slashed.Length < 3 || !char.IsAsciiLetter(slashed[0]) || slashed[1] != ':')
        {
            return "isn't a fully qualified local path";
        }

        var leaf = System.IO.Path.GetFileName(slashed.TrimEnd('\\'));
        return SectionFileNames.NameProblem(leaf) is { } problem ? "has a last segment that " + problem : null;
    }

    public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct)
    {
        const string method = nameof(IApplication.UpdatePageContent);
        if (OneNoteDates.ToUtc(expectedLastModified) is not { } date)
        {
            return Task.FromException(BadExpectedDate(method));
        }

        return RunAsync(
            method,
            app =>
            {
                // force is always false: OneNote checks the date for an open page (AD-7, AD-17).
                app.UpdatePageContent(pageXml ?? string.Empty, date, XMLSchema.xs2013, force: false);
                return true;
            },
            CallKind.Write,
            ct);
    }

    public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct)
    {
        const string method = nameof(IApplication.DeletePageContent);
        if (OneNoteDates.ToUtc(expectedLastModified) is not { } date)
        {
            return Task.FromException(BadExpectedDate(method));
        }

        return RunAsync(
            method,
            app =>
            {
                app.DeletePageContent(pageId ?? string.Empty, objectId ?? string.Empty, date, force: false);
                return true;
            },
            CallKind.Write,
            ct);
    }

    public Task UpdateHierarchyAsync(string changesXml, CancellationToken ct)
    {
        const string method = nameof(IApplication.UpdateHierarchy);
        if (string.IsNullOrEmpty(changesXml))
        {
            return Task.FromException(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: no hierarchy XML was given.",
                retryable: false));
        }

        return RunAsync(
            method,
            app =>
            {
                app.UpdateHierarchy(changesXml, XMLSchema.xs2013);
                return true;
            },
            // A write is never retried after a disconnect (AD-3): the pipeline reports outcomeUnknown instead.
            CallKind.Write,
            ct);
    }

    public Task DeleteHierarchyAsync(string objectId, string expectedLastModified, bool permanent, CancellationToken ct)
    {
        const string method = nameof(IApplication.DeleteHierarchy);
        if (string.IsNullOrEmpty(objectId))
        {
            return Task.FromException(new OneNoteException(
                ErrorCode.InvalidArgument,
                $"{method} wasn't sent: no object ID was given.",
                retryable: false));
        }

        // Never DateTime.MinValue, which skips OneNote's date check (AD-17; live, DeleteHierarchy, 2026-11-12).
        if (OneNoteDates.ToUtc(expectedLastModified) is not { } date)
        {
            return Task.FromException(BadExpectedDate(method, "object's hierarchy"));
        }

        return RunAsync(
            method,
            app =>
            {
                app.DeleteHierarchy(objectId, date, permanent);
                return true;
            },
            // A write is never retried after a disconnect (AD-3): the pipeline reports outcomeUnknown instead.
            CallKind.Write,
            ct);
    }

    public Task WaitForAbandonedWritesAsync(CancellationToken ct)
    {
        lock (gate)
        {
            return writesSettled is { } settled ? settled.Task.WaitAsync(ct) : Task.CompletedTask;
        }
    }

    /// <summary>
    /// Raw seam for unit tests and the integration suite's guarded writes: runs <paramref name="call"/> under the same
    /// lifecycle as every port method. A write passes <paramref name="retryOnDisconnect"/> false, which makes it a write
    /// for AD-3: no disconnect retry, refused with <see cref="ErrorCode.ComBusy"/> while an abandoned write is
    /// outstanding, and tracked if it is abandoned itself. Busy (<c>RPC_E_CALL_REJECTED</c>, <c>RETRYLATER</c>) is still
    /// retried with backoff: a rejected call never ran.
    /// </summary>
    internal Task<T> InvokeAsync<T>(Func<IApplication, T> call, CancellationToken ct, bool retryOnDisconnect = true, string method = "COM call") =>
        RunAsync(method, call, retryOnDisconnect ? CallKind.Read : CallKind.Write, ct);

    /// <summary>Writes whose caller stopped waiting while they ran in OneNote, and that haven't returned yet.</summary>
    internal int OutstandingWrites
    {
        get
        {
            lock (gate)
            {
                return outstandingWrites;
            }
        }
    }

    /// <summary>
    /// Every write ever counted as abandoned, settled or not. Only grows, so a test can tell that a write was never
    /// tracked even when a tracked one would have settled at once (a queued write that never ran has already ended).
    /// </summary>
    internal int AbandonedWritesTracked
    {
        get
        {
            lock (gate)
            {
                return abandonedWritesTracked;
            }
        }
    }

    public void Dispose()
    {
        StaThread? worker;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            worker = this.worker;
            this.worker = null;
        }

        idleTimer.Dispose();

        // Queued behind any work already there; the thread releases its reference on exit too.
        worker?.Post(worker.ReleaseApplication);
        worker?.Dispose();

        // activationLock is left undisposed: it never allocates a wait handle, and a call still in flight releases it.
    }

    internal static InteropScope MapScope(CoreScope scope) => scope switch
    {
        CoreScope.Self => InteropScope.hsSelf,
        CoreScope.Children => InteropScope.hsChildren,
        CoreScope.Notebooks => InteropScope.hsNotebooks,
        CoreScope.Sections => InteropScope.hsSections,
        CoreScope.Pages => InteropScope.hsPages,
        _ => throw new OneNoteException(ErrorCode.Internal, $"Unknown hierarchy scope {(int)scope}."),
    };

    internal static InteropPageInfo MapPageInfo(CorePageInfo pageInfo) => pageInfo switch
    {
        CorePageInfo.Basic => InteropPageInfo.piBasic,
        CorePageInfo.BinaryData => InteropPageInfo.piBinaryData,
        CorePageInfo.Selection => InteropPageInfo.piSelection,
        CorePageInfo.BinaryDataSelection => InteropPageInfo.piBinaryDataSelection,
        CorePageInfo.FileType => InteropPageInfo.piFileType,
        CorePageInfo.BinaryDataFileType => InteropPageInfo.piBinaryDataFileType,
        CorePageInfo.SelectionFileType => InteropPageInfo.piSelectionFileType,
        CorePageInfo.All => InteropPageInfo.piAll,
        _ => throw new OneNoteException(ErrorCode.Internal, $"Unknown page info {(int)pageInfo}."),
    };

    internal static InteropNewPageStyle MapNewPageStyle(CoreNewPageStyle style) => style switch
    {
        CoreNewPageStyle.Default => InteropNewPageStyle.npsDefault,
        CoreNewPageStyle.BlankPageWithTitle => InteropNewPageStyle.npsBlankPageWithTitle,
        CoreNewPageStyle.BlankPageNoTitle => InteropNewPageStyle.npsBlankPageNoTitle,
        _ => throw new OneNoteException(ErrorCode.Internal, $"Unknown page style {(int)style}."),
    };

    internal static InteropSpecialLocation MapSpecialLocation(CoreSpecialLocation location) => location switch
    {
        CoreSpecialLocation.BackUpFolder => InteropSpecialLocation.slBackUpFolder,
        CoreSpecialLocation.UnfiledNotesSection => InteropSpecialLocation.slUnfiledNotesSection,
        CoreSpecialLocation.DefaultNotebookFolder => InteropSpecialLocation.slDefaultNotebookFolder,
        _ => throw new OneNoteException(ErrorCode.Internal, $"Unknown special location {(int)location}."),
    };

    /// <summary>Reconnect triggers (AD-3). Not <c>0x800706BE</c>: it can mean bad XML (OneMore row 7).</summary>
    internal static bool IsReconnectTrigger(Exception ex) =>
        ex is InvalidComObjectException || unchecked((uint)ex.HResult) is 0x800706BA or 0x80010108;

    /// <summary>Busy codes retried with backoff. <c>0x8004201D</c> is deliberately absent: it is <c>ComBusy</c> at once.</summary>
    internal static bool IsBusy(Exception ex) =>
        ex is COMException && unchecked((uint)ex.HResult) is 0x80010001 or 0x8001010A;

    private async Task<T> RunAsync<T>(string method, Func<IApplication, T> call, CallKind kind, CancellationToken ct)
    {
        // First, before any other refusal: nothing but a read goes to OneNote while an abandoned write may still run (AD-3).
        ThrowIfWriteOutstanding(method, kind);
        BeginCall();
        try
        {
            var reconnected = false;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await AttemptAsync(method, call, kind, ct).ConfigureAwait(false);
                }
                catch (ReconnectRequiredException ex)
                {
                    // An attempt that never reached COM retries freely; one that did gets the read's one retry.
                    var mayRetry = attempt < MaxAttempts && (!ex.ReachedCom || (kind == CallKind.Read && !reconnected));
                    if (!mayRetry)
                    {
                        throw ex.ToComUnavailable(method);
                    }

                    reconnected |= ex.ReachedCom;
                }
            }
        }
        finally
        {
            EndCall();
        }
    }

    private async Task<T> AttemptAsync<T>(string method, Func<IApplication, T> call, CallKind kind, CancellationToken ct)
    {
        var (worker, settings) = await EnsureActivatedAsync(ct).ConfigureAwait(false);
        var timeout = settings.Timeouts.Call;
        var deadline = DateTime.UtcNow + timeout;
        var backoff = FirstBackoff;
        while (true)
        {
            // Again at each send: a write may have been abandoned while this call activated or backed off.
            ThrowIfWriteOutstanding(method, kind);
            var item = worker.Enqueue(
                () =>
                {
                    var app = worker.Application ?? throw new ReconnectRequiredException(null);
                    try
                    {
                        return call(app);
                    }
                    catch (Exception ex) when (IsReconnectTrigger(ex))
                    {
                        // A dead RCW (OneNote restarted) fails forever; drop it so the retry re-activates.
                        worker.ReleaseApplication();
                        throw new ReconnectRequiredException(ex);
                    }
                },
                ct);

            try
            {
                return await AwaitAsync(worker, item, deadline, timeout, method, kind, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsBusy(ex))
            {
                // Stop before the deadline so busy never poisons a worker (AD-3).
                if (deadline - DateTime.UtcNow - backoff < backoff)
                {
                    throw HResultMap.ToException(method, ex);
                }

                await Task.Delay(backoff, ct).ConfigureAwait(false);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
            catch (Exception ex) when (ex is not (OneNoteException or ReconnectRequiredException or OperationCanceledException))
            {
                throw HResultMap.ToException(method, ex);
            }
        }
    }

    /// <summary>
    /// Returns the live worker with a COM reference, activating one if needed. The per-call process check
    /// (<see cref="IProcessProbe.IsOneNoteStillRunning"/>) drops a reference to an OneNote that has gone or that WER
    /// names as crashed; a WER process whose command line can't be read doesn't drop it (2.7 Q3). Activation re-reads
    /// settings, then runs the connecting process check (<see cref="IProcessProbe.IsOneNoteRunning"/>, where that
    /// unreadable WER process does count as a crash), the integrity check and <c>CoCreateInstance</c>, in that order.
    /// </summary>
    private async Task<(StaThread Worker, GatewaySettings Settings)> EnsureActivatedAsync(CancellationToken ct)
    {
        var (worker, settings) = LiveWorker();
        if (worker.HasApplication && !probe.IsOneNoteStillRunning())
        {
            // OneNote closed or crashed: the reference is dead. Release it on its own thread; activation decides what happens next.
            worker.Post(worker.ReleaseApplication);
        }
        else if (worker.HasApplication)
        {
            return (worker, settings);
        }

        // Bounded: a hung activation ahead of this call is abandoned within its own activation timeout.
        var lockTimeout = settings.Timeouts.Activation;
        if (!await activationLock.WaitAsync(lockTimeout, ct).ConfigureAwait(false))
        {
            throw new OneNoteException(
                ErrorCode.Timeout,
                $"{ActivationWhat} timed out after {Seconds(lockTimeout)} s waiting for an earlier connection attempt.");
        }

        try
        {
            if (worker.IsPoisoned)
            {
                // The activation this call waited behind hung and was abandoned: fail like a call queued behind a hang.
                throw PoisonedFailure();
            }

            (worker, _) = LiveWorker();
            settings = readSettings();

            if (worker.HasApplication && probe.IsOneNoteStillRunning())
            {
                // Another call activated while this one waited: that reference is live, so it gets the per-call check,
                // not the connecting one (2.7 Q3). Still adopt the settings just read.
                SetSettings(settings);
                return (worker, settings);
            }

            if (!probe.IsOneNoteRunning())
            {
                worker.Post(worker.ReleaseApplication);
                if (!settings.AllowSilentActivation)
                {
                    var crashing = probe.IsOneNoteCrashing();
                    throw new OneNoteException(
                        ErrorCode.OneNoteNotRunning,
                        crashing ? CrashingMessage : "OneNote is not running. Start OneNote desktop, then retry.",
                        userAction: crashing ? CrashingUserAction : null);
                }
            }

            var integrity = probe.CheckIntegrity();
            if (!integrity.Matches)
            {
                throw new OneNoteException(
                    ErrorCode.IntegrityMismatch,
                    $"OneNote and this server run at different integrity levels ({integrity.Describe()}), so COM was not called.");
            }

            var item = worker.Enqueue(
                () =>
                {
                    if (worker.Application is not null)
                    {
                        return true;
                    }

                    try
                    {
                        worker.Application = (IApplication)createApplication();
                    }
                    catch (Exception ex) when (ex is not OneNoteException)
                    {
                        throw HResultMap.ToException(ActivationWhat, ex);
                    }

                    return true;
                },
                ct);
            var timeout = settings.Timeouts.Activation;

            // Activation writes nothing: an abandoned one isn't an outstanding write.
            await AwaitAsync(worker, item, DateTime.UtcNow + timeout, timeout, ActivationWhat, CallKind.Read, ct).ConfigureAwait(false);
            SetSettings(settings);
            return (worker, settings);
        }
        finally
        {
            activationLock.Release();
        }
    }

    /// <summary>
    /// Waits for <paramref name="item"/> until <paramref name="deadline"/>. An item still queued at the deadline is
    /// abandoned (it never touched COM). One that started and is still running is a hang: its worker is poisoned. A
    /// write that started and is then left (a hang, or the caller's cancel while it runs) is outstanding until it returns.
    /// </summary>
    private async Task<T> AwaitAsync<T>(StaThread worker, WorkItem<T> item, DateTime deadline, TimeSpan timeout, string method, CallKind kind, CancellationToken ct)
    {
        var remaining = deadline - DateTime.UtcNow;
        try
        {
            return await item.Task.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, ct).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!item.Task.IsCompleted)
        {
            var seconds = Seconds(timeout);
            if (item.TryAbandon(new OneNoteException(ErrorCode.Timeout, "abandoned")))
            {
                throw new OneNoteException(ErrorCode.Timeout, $"{method} timed out after {seconds} s waiting for an earlier call to OneNote.");
            }

            if (kind == CallKind.Write)
            {
                TrackAbandonedWrite(item.Task);
            }

            Poison(worker);
            throw new OneNoteException(ErrorCode.Timeout, $"{method} did not return within {seconds} s; the connection to OneNote was reset.");
        }
        catch (TimeoutException)
        {
            // Finished at the deadline: take its outcome.
            return await item.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (!item.TryAbandon(new OperationCanceledException(ct)))
            {
                // Already running: the caller stops waiting, but a hang must still poison the worker at the deadline.
                if (kind == CallKind.Write && !item.Task.IsCompleted)
                {
                    TrackAbandonedWrite(item.Task);
                }

                _ = WatchAfterCancelAsync(worker, item.Task, deadline);
            }

            throw;
        }
    }

    private async Task WatchAfterCancelAsync(StaThread worker, Task task, DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        try
        {
            await task.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!task.IsCompleted)
        {
            Poison(worker);
        }
#pragma warning disable CA1031 // The call's own outcome belongs to nobody now; only a hang matters here.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static string Seconds(TimeSpan timeout) => timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static OneNoteException PoisonedFailure() => new(
        ErrorCode.ComBusy,
        "An earlier call to OneNote stopped responding and was abandoned; retry.");

    /// <summary>The not-running message while Windows Error Reporting handles a crashed <c>ONENOTE.EXE</c> (AD-3).</summary>
    internal const string CrashingMessage =
        "OneNote crashed and Windows is still closing it, so the server didn't start a hidden copy. Start OneNote desktop once it has closed, then retry.";

    /// <summary>The userAction that goes with <see cref="CrashingMessage"/>, so the model waits for the crashed copy to close.</summary>
    internal const string CrashingUserAction = "Wait until the crashed OneNote has closed, start OneNote desktop, then retry.";

    private static OneNoteException BadExpectedDate(string method, string what = "page") => new(
        ErrorCode.InvalidArgument,
        $"{method} wasn't sent: the expected last-modified date isn't a date OneNote wrote.",
        userAction: $"Re-read the {what} and pass its lastModifiedTime exactly as OneNote wrote it.");

    /// <summary>
    /// AD-3: while an abandoned write may still be running in OneNote, only reads are sent. Anything else (a write,
    /// <c>NavigateTo</c>) fails <see cref="ErrorCode.ComBusy"/> without touching COM.
    /// </summary>
    private void ThrowIfWriteOutstanding(string method, CallKind kind)
    {
        if (kind == CallKind.Read)
        {
            return;
        }

        lock (gate)
        {
            if (outstandingWrites > 0)
            {
                throw new OneNoteException(
                    ErrorCode.ComBusy,
                    $"{method} wasn't sent: an earlier write to OneNote timed out or was cancelled and hasn't returned yet.",
                    userAction: "Wait for OneNote to finish the earlier write, then retry.");
            }
        }
    }

    /// <summary>Counts <paramref name="call"/> as an outstanding abandoned write until it returns, however it ends.</summary>
    private void TrackAbandonedWrite(Task call)
    {
        lock (gate)
        {
            outstandingWrites++;
            abandonedWritesTracked++;
            writesSettled ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = call.ContinueWith(
            t =>
            {
                // Its outcome belongs to nobody now; observe it so a fault isn't reported as unobserved.
                _ = t.Exception;
                TaskCompletionSource? settled = null;
                lock (gate)
                {
                    if (--outstandingWrites == 0)
                    {
                        settled = writesSettled;
                        writesSettled = null;
                    }
                }

                settled?.TrySetResult();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private (StaThread Worker, GatewaySettings Settings) LiveWorker()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (abandoned >= MaxAbandonedWorkers)
            {
                throw new OneNoteException(
                    ErrorCode.ComUnavailable,
                    $"OneNote has not returned {abandoned.ToString(CultureInfo.InvariantCulture)} earlier calls, so the server stopped calling it. Retry once OneNote responds again.");
            }

            worker ??= new StaThread($"OneNote-STA-{(++workerSerial).ToString(CultureInfo.InvariantCulture)}");
            return (worker, settings);
        }
    }

    private void Poison(StaThread worker)
    {
        lock (gate)
        {
            if (worker.IsPoisoned)
            {
                return;
            }

            worker.Poison(PoisonedFailure);
            if (this.worker == worker)
            {
                this.worker = null;
            }

            abandoned++;
        }

        // The abandoned call came back: it released its reference and exited, so OneNote is reachable again.
        _ = worker.Exited.ContinueWith(
            _ =>
            {
                lock (gate)
                {
                    abandoned--;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void SetSettings(GatewaySettings settings)
    {
        lock (gate)
        {
            this.settings = settings;
        }
    }

    private void BeginCall()
    {
        lock (gate)
        {
            inFlight++;
            if (!disposed)
            {
                idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    private void EndCall()
    {
        lock (gate)
        {
            inFlight--;
            if (inFlight == 0 && !disposed)
            {
                idleTimer.Change(settings.Timeouts.IdleRelease, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnIdle()
    {
        lock (gate)
        {
            if (disposed || inFlight > 0 || this.worker is not { HasApplication: true } worker)
            {
                return;
            }

            worker.Post(worker.ReleaseApplication);
        }
    }

    private static object CreateApplication()
    {
        var type = Type.GetTypeFromCLSID(OneNoteClsid.Application, throwOnError: true)!;
        return Activator.CreateInstance(type)!;
    }

    /// <summary>What a call is, for AD-3's rules: a read retries once after a disconnect and runs while a write is outstanding; the others do neither.</summary>
    private enum CallKind
    {
        Read,
        AppState,
        Write,
    }

    /// <summary>The reference is gone (released, or a reconnect trigger hit). <see cref="ReachedCom"/> says whether COM was called.</summary>
    private sealed class ReconnectRequiredException(Exception? cause)
        : Exception("The COM reference must be re-created.", cause)
    {
        public bool ReachedCom => InnerException is not null;

        public OneNoteException ToComUnavailable(string method) => InnerException is { } cause
            ? new OneNoteException(ErrorCode.ComUnavailable, HResultMap.FailureMessage(method, cause.HResult), cause.HResult, cause)
            : new OneNoteException(ErrorCode.ComUnavailable, $"{method} failed: the connection to OneNote was released and could not be re-established.");
    }
}

/// <summary>The settings the gateway reads at every activation (AD-3, AD-10).</summary>
internal readonly record struct GatewaySettings(Timeouts Timeouts, bool AllowSilentActivation)
{
    public static GatewaySettings Default { get; } = new(Timeouts.Default, AllowSilentActivation: false);

    public static GatewaySettings From(SettingsSnapshot snapshot) => new(snapshot.Timeouts, snapshot.AllowSilentActivation);
}
