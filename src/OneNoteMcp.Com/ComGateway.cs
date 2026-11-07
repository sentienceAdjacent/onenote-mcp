using System.Globalization;
using System.Runtime.InteropServices;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using CorePageInfo = OneNoteMcp.Core.PageInfo;
using CoreScope = OneNoteMcp.Core.HierarchyScope;
using CoreSpecialLocation = OneNoteMcp.Core.SpecialLocation;
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
/// <item>A disconnect drops the reference, re-runs every activation rule and retries a read once.</item>
/// <item>Busy is retried with backoff until just before the deadline, then returns <see cref="ErrorCode.ComBusy"/>, so
/// busy never poisons a worker. <c>0x8004201D</c> returns <see cref="ErrorCode.ComBusy"/> at once.</item>
/// <item>Activation: process check (unless <c>allowSilentActivation</c>), integrity check, then <c>CoCreateInstance</c>
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

    private readonly IProcessProbe _probe;
    private readonly Func<GatewaySettings> _readSettings;
    private readonly Func<object> _createApplication;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _activationLock = new(1, 1);
    private readonly Timer _idleTimer;

    // Guarded by _gate.
    private StaThread? _worker;
    private GatewaySettings _settings = GatewaySettings.Default;
    private int _abandoned;
    private int _inFlight;
    private int _workerSerial;
    private bool _disposed;

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
        _probe = probe;
        _readSettings = readSettings;
        _createApplication = createApplication;
        _idleTimer = new Timer(_ => OnIdle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Abandoned workers whose hung call hasn't returned yet.</summary>
    internal int AbandonedWorkers
    {
        get
        {
            lock (_gate)
            {
                return _abandoned;
            }
        }
    }

    /// <summary>Whether the live worker holds a COM reference.</summary>
    internal bool HasApplication
    {
        get
        {
            lock (_gate)
            {
                return _worker?.HasApplication ?? false;
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
            retryOnDisconnect: true,
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
            retryOnDisconnect: true,
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
            retryOnDisconnect: true,
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
            retryOnDisconnect: true,
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
            retryOnDisconnect: true,
            ct);

    public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.FindMeta),
            app =>
            {
                app.FindMeta(startNodeId ?? string.Empty, name ?? string.Empty, out var xml, includeUnindexed, XMLSchema.xs2013);
                return xml;
            },
            retryOnDisconnect: true,
            ct);

    public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.GetHyperlinkToObject),
            app =>
            {
                app.GetHyperlinkToObject(hierarchyId ?? string.Empty, pageContentObjectId ?? string.Empty, out var link);
                return link;
            },
            retryOnDisconnect: true,
            ct);

    public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) =>
        RunAsync(
            nameof(IApplication.GetWebHyperlinkToObject),
            app =>
            {
                app.GetWebHyperlinkToObject(hierarchyId ?? string.Empty, pageContentObjectId ?? string.Empty, out var link);
                return link;
            },
            retryOnDisconnect: true,
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
            retryOnDisconnect: true,
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
            retryOnDisconnect: false,
            ct);

    /// <summary>
    /// Raw seam for unit tests and the integration suite's guarded writes: runs <paramref name="call"/> under the same
    /// lifecycle as every port method. A write passes <paramref name="retryOnDisconnect"/> false, which turns off only the
    /// disconnect retry (AD-3). Busy (<c>RPC_E_CALL_REJECTED</c>, <c>RETRYLATER</c>) is still retried with backoff: a
    /// rejected call never ran.
    /// </summary>
    internal Task<T> InvokeAsync<T>(Func<IApplication, T> call, CancellationToken ct, bool retryOnDisconnect = true, string method = "COM call") =>
        RunAsync(method, call, retryOnDisconnect, ct);

    public void Dispose()
    {
        StaThread? worker;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            worker = _worker;
            _worker = null;
        }

        _idleTimer.Dispose();

        // Queued behind any work already there; the thread releases its reference on exit too.
        worker?.Post(worker.ReleaseApplication);
        worker?.Dispose();

        // _activationLock is left undisposed: it never allocates a wait handle, and a call still in flight releases it.
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

    private async Task<T> RunAsync<T>(string method, Func<IApplication, T> call, bool retryOnDisconnect, CancellationToken ct)
    {
        BeginCall();
        try
        {
            var reconnected = false;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await AttemptAsync(method, call, ct).ConfigureAwait(false);
                }
                catch (ReconnectRequiredException ex)
                {
                    // An attempt that never reached COM retries freely; one that did gets the read's one retry.
                    var mayRetry = attempt < MaxAttempts && (!ex.ReachedCom || (retryOnDisconnect && !reconnected));
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

    private async Task<T> AttemptAsync<T>(string method, Func<IApplication, T> call, CancellationToken ct)
    {
        var (worker, settings) = await EnsureActivatedAsync(ct).ConfigureAwait(false);
        var timeout = settings.Timeouts.Call;
        var deadline = DateTime.UtcNow + timeout;
        var backoff = FirstBackoff;
        while (true)
        {
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
                return await AwaitAsync(worker, item, deadline, timeout, method, ct).ConfigureAwait(false);
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
    /// Returns the live worker with a COM reference, activating one if needed. The per-call process check drops a
    /// reference to an OneNote that has gone. Activation re-reads settings, then runs the process check, the integrity
    /// check and <c>CoCreateInstance</c>, in that order.
    /// </summary>
    private async Task<(StaThread Worker, GatewaySettings Settings)> EnsureActivatedAsync(CancellationToken ct)
    {
        var (worker, settings) = LiveWorker();
        if (worker.HasApplication && !_probe.IsOneNoteRunning())
        {
            // OneNote closed: the reference is dead. Release it on its own thread; activation decides what happens next.
            worker.Post(worker.ReleaseApplication);
        }
        else if (worker.HasApplication)
        {
            return (worker, settings);
        }

        // Bounded: a hung activation ahead of this call is abandoned within its own activation timeout.
        var lockTimeout = settings.Timeouts.Activation;
        if (!await _activationLock.WaitAsync(lockTimeout, ct).ConfigureAwait(false))
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
            settings = _readSettings();

            if (!_probe.IsOneNoteRunning())
            {
                worker.Post(worker.ReleaseApplication);
                if (!settings.AllowSilentActivation)
                {
                    throw new OneNoteException(
                        ErrorCode.OneNoteNotRunning,
                        "OneNote is not running. Start OneNote desktop, then retry.");
                }
            }
            else if (worker.HasApplication)
            {
                // Another call activated while this one waited; still adopt the settings just read.
                SetSettings(settings);
                return (worker, settings);
            }

            var integrity = _probe.CheckIntegrity();
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
                        worker.Application = (IApplication)_createApplication();
                    }
                    catch (Exception ex) when (ex is not OneNoteException)
                    {
                        throw HResultMap.ToException(ActivationWhat, ex);
                    }

                    return true;
                },
                ct);
            var timeout = settings.Timeouts.Activation;
            await AwaitAsync(worker, item, DateTime.UtcNow + timeout, timeout, ActivationWhat, ct).ConfigureAwait(false);
            SetSettings(settings);
            return (worker, settings);
        }
        finally
        {
            _activationLock.Release();
        }
    }

    /// <summary>
    /// Waits for <paramref name="item"/> until <paramref name="deadline"/>. An item still queued at the deadline is
    /// abandoned (it never touched COM). One that started and is still running is a hang: its worker is poisoned.
    /// </summary>
    private async Task<T> AwaitAsync<T>(StaThread worker, WorkItem<T> item, DateTime deadline, TimeSpan timeout, string method, CancellationToken ct)
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

    private (StaThread Worker, GatewaySettings Settings) LiveWorker()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_abandoned >= MaxAbandonedWorkers)
            {
                throw new OneNoteException(
                    ErrorCode.ComUnavailable,
                    $"OneNote has not returned {_abandoned.ToString(CultureInfo.InvariantCulture)} earlier calls, so the server stopped calling it. Retry once OneNote responds again.");
            }

            _worker ??= new StaThread($"OneNote-STA-{(++_workerSerial).ToString(CultureInfo.InvariantCulture)}");
            return (_worker, _settings);
        }
    }

    private void Poison(StaThread worker)
    {
        lock (_gate)
        {
            if (worker.IsPoisoned)
            {
                return;
            }

            worker.Poison(PoisonedFailure);
            if (_worker == worker)
            {
                _worker = null;
            }

            _abandoned++;
        }

        // The abandoned call came back: it released its reference and exited, so OneNote is reachable again.
        _ = worker.Exited.ContinueWith(
            _ =>
            {
                lock (_gate)
                {
                    _abandoned--;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void SetSettings(GatewaySettings settings)
    {
        lock (_gate)
        {
            _settings = settings;
        }
    }

    private void BeginCall()
    {
        lock (_gate)
        {
            _inFlight++;
            if (!_disposed)
            {
                _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    private void EndCall()
    {
        lock (_gate)
        {
            _inFlight--;
            if (_inFlight == 0 && !_disposed)
            {
                _idleTimer.Change(_settings.Timeouts.IdleRelease, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnIdle()
    {
        lock (_gate)
        {
            if (_disposed || _inFlight > 0 || _worker is not { HasApplication: true } worker)
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
