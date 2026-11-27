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
/// <item>A write (<c>CreateNewPage</c>, <c>UpdatePageContent</c>, <c>DeletePageContent</c>, <c>CloseNotebook</c>, or the raw seam without the
/// disconnect retry) that started in OneNote and was then left, by its deadline or its caller's cancel, is outstanding
/// until it returns. Meanwhile every call but a read (writes, <c>NavigateTo</c>) returns <see cref="ErrorCode.ComBusy"/>
/// without touching COM, and <see cref="WaitForAbandonedWritesAsync"/> stays pending. A write abandoned while still
/// queued never ran and isn't tracked.</item>
/// <item>A disconnect drops the reference, re-runs every activation rule and retries a read once; never a write or
/// <c>NavigateTo</c>.</item>
/// <item>Busy is retried with backoff until just before the deadline, then returns <see cref="ErrorCode.ComBusy"/>, so
/// busy never poisons a worker. <c>0x8004201D</c> returns <see cref="ErrorCode.ComBusy"/> at once.</item>
/// <item>Activation: process check (unless <c>allowSilentActivation</c>; an <c>ONENOTE.EXE</c> Windows Error Reporting is
/// handling doesn't count as running, nor, unless <c>allowSilentActivation</c>, one with no open window), integrity
/// check, then <c>CoCreateInstance</c> on the worker under <c>timeouts.activation</c>. Settings are re-read at every
/// activation.</item>
/// <item>The COM reference is released after <c>timeouts.idleRelease</c> with no call (5 s by default), or by the next
/// call's check once OneNote has no open window: a held reference keeps a closed OneNote alive (backlog 3). Nothing
/// watches the window between calls. While a QuickFiling picker is held (story 4.9), the idle release waits.</item>
/// </list>
/// Failures map through <see cref="HResultMap"/> (AD-9). It never kills or closes OneNote.
/// </summary>
public sealed partial class ComGateway : IOneNoteGateway, IDisposable
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
    private int pickerHolds;
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

    /// <param name="publish">True for <c>Publish</c>, which runs under <c>timeouts.publishSeconds</c> instead of <c>callSeconds</c> (AD-3).</param>
    private async Task<T> RunAsync<T>(string method, Func<IApplication, T> call, CallKind kind, CancellationToken ct, bool publish = false)
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
                    return await AttemptAsync(method, call, kind, publish, ct).ConfigureAwait(false);
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

    private async Task<T> AttemptAsync<T>(string method, Func<IApplication, T> call, CallKind kind, bool publish, CancellationToken ct)
    {
        var (worker, settings) = await EnsureActivatedAsync(ct).ConfigureAwait(false);
        var timeout = publish ? settings.Timeouts.Publish : settings.Timeouts.Call;
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
