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

public sealed partial class ComGateway
{
    /// <summary>
    /// Returns the live worker with a COM reference, activating one if needed. The per-call check
    /// (<see cref="LiveReferenceUsable"/>) drops a reference to an OneNote that has gone, that WER names as crashed, or,
    /// unless silent activation is allowed, that has no open window; a WER process whose command line can't be read
    /// doesn't drop it (2.7 Q3). Activation re-reads settings, then runs the connecting process check
    /// (<see cref="IProcessProbe.IsOneNoteRunning"/>, where that unreadable WER process does count as a crash), the
    /// window check (<see cref="IProcessProbe.HasOneNoteWindow"/>, unless silent activation is allowed), the integrity
    /// check and <c>CoCreateInstance</c>, in that order. An integrity report that lists no <c>ONENOTE.EXE</c> means
    /// OneNote exited after the earlier checks: not running, unless silent activation is allowed (backlog 4).
    /// </summary>
    private async Task<(StaThread Worker, GatewaySettings Settings)> EnsureActivatedAsync(CancellationToken ct)
    {
        var (worker, settings) = LiveWorker();
        if (worker.HasApplication && !LiveReferenceUsable(settings))
        {
            // OneNote closed, crashed, or lost its window (it is shutting down): holding on would keep it alive. Release
            // the reference on its own thread; activation decides what happens next.
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

            if (worker.HasApplication && LiveReferenceUsable(settings))
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
                    throw NotRunningFailure();
                }
            }
            else if (!settings.AllowSilentActivation && !probe.HasOneNoteWindow())
            {
                // Running without a window: closing (connecting would keep it alive, or start a hidden copy), still
                // starting, or started for COM. Not running, unless silent activation is allowed (AD-3, backlog 3).
                worker.Post(worker.ReleaseApplication);
                throw new OneNoteException(ErrorCode.OneNoteNotRunning, NoWindowMessage, userAction: NoWindowUserAction);
            }

            var integrity = probe.CheckIntegrity();
            if (integrity.OneNote.Count == 0 && !settings.AllowSilentActivation)
            {
                // OneNote exited after the running and window checks (backlog 4). An empty list would match, and
                // CoCreateInstance would then start a hidden copy: report it as not running instead (AD-3).
                worker.Post(worker.ReleaseApplication);
                throw NotRunningFailure();
            }

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
    /// abandoned (it never touched COM); one its poisoned worker already failed while queued takes that failure. One that
    /// started and is still running is a hang: its worker is poisoned. A
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
            switch (GiveUp(item, new OneNoteException(ErrorCode.Timeout, "abandoned")))
            {
                case GiveUpOutcome.AbandonedByCaller:
                    throw new OneNoteException(ErrorCode.Timeout, $"{method} timed out after {seconds} s waiting for an earlier call to OneNote.");
                case GiveUpOutcome.AbandonedElsewhere:
                    // Its worker was poisoned (or stopped) while it was still queued, as its own deadline passed: it never
                    // ran, so it isn't a hang or an outstanding write. Take the failure it was given; awaiting also covers
                    // the moment between that abandon and the task completing.
                    return await item.Task.ConfigureAwait(false);
            }

            // Started: it is still running in COM, a hang.
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
            // Abandoned while queued, by this cancel or by its worker being poisoned: it never ran, so nothing to watch.
            if (GiveUp(item, new OperationCanceledException(ct)) == GiveUpOutcome.Started)
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

    /// <summary>What became of a call its waiter gave up on (its deadline passed, or its caller cancelled).</summary>
    internal enum GiveUpOutcome
    {
        /// <summary>Still queued: this waiter abandoned it, so it never touches COM.</summary>
        AbandonedByCaller,

        /// <summary>Already failed by its worker (poisoned or stopping) before it ran: it never touched COM.</summary>
        AbandonedElsewhere,

        /// <summary>The STA thread started it: it is running in COM, or has just returned.</summary>
        Started,
    }

    /// <summary>Tries to abandon <paramref name="item"/> with <paramref name="failure"/> and says what became of it.</summary>
    /// <remarks>
    /// A failed abandon doesn't mean the call started: a poisoned worker drains its queue by abandoning every item,
    /// and that can win the race with this waiter's own deadline or cancel. Once the abandon fails the item has left
    /// <c>Queued</c> for good, so one read of its state settles which it was.
    /// </remarks>
    internal static GiveUpOutcome GiveUp(WorkItem item, Exception failure)
    {
        if (item.TryAbandon(failure))
        {
            return GiveUpOutcome.AbandonedByCaller;
        }

        return item.WasAbandoned ? GiveUpOutcome.AbandonedElsewhere : GiveUpOutcome.Started;
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

    /// <summary>
    /// <see cref="ErrorCode.OneNoteNotRunning"/> for activation: with the crash wording while Windows Error Reporting
    /// handles a crashed <c>ONENOTE.EXE</c>, else the plain message and the code's default userAction (AD-3).
    /// </summary>
    private OneNoteException NotRunningFailure()
    {
        var crashing = probe.IsOneNoteCrashing();
        return new OneNoteException(
            ErrorCode.OneNoteNotRunning,
            crashing ? CrashingMessage : "OneNote is not running. Start OneNote desktop, then retry.",
            userAction: crashing ? CrashingUserAction : null);
    }

    private static OneNoteException PoisonedFailure() => new(
        ErrorCode.ComBusy,
        "An earlier call to OneNote stopped responding and was abandoned; retry.");

    /// <summary>The not-running message while Windows Error Reporting handles a crashed <c>ONENOTE.EXE</c> (AD-3).</summary>
    internal const string CrashingMessage =
        "OneNote crashed and Windows is still closing it, so the server didn't start a hidden copy. Start OneNote desktop once it has closed, then retry.";

    /// <summary>The userAction that goes with <see cref="CrashingMessage"/>, so the model waits for the crashed copy to close.</summary>
    internal const string CrashingUserAction = "Wait until the crashed OneNote has closed, start OneNote desktop, then retry.";

    /// <summary>The not-running message for an <c>ONENOTE.EXE</c> with no open window (AD-3, backlog 3).</summary>
    internal const string NoWindowMessage =
        "OneNote is running without an open window (it is closing, still starting, or was started without one), so the server didn't connect to it.";

    /// <summary>The userAction that goes with <see cref="NoWindowMessage"/>.</summary>
    internal const string NoWindowUserAction =
        "Wait a few seconds for OneNote to finish closing or starting, make sure OneNote desktop is open in a window (minimised is fine), then retry.";

    /// <summary>
    /// The per-call check of a live reference (AD-3): OneNote still runs (<see cref="IProcessProbe.IsOneNoteStillRunning"/>)
    /// and, unless silent activation is allowed, still has a window. Without one it is shutting down, and the reference
    /// would keep it alive (backlog 3).
    /// </summary>
    private bool LiveReferenceUsable(GatewaySettings settings) =>
        probe.IsOneNoteStillRunning() && (settings.AllowSilentActivation || probe.HasOneNoteWindow());

    private static OneNoteException BadExpectedDate(string method, string what = "page") => new(
        ErrorCode.InvalidArgument,
        $"{method} wasn't sent: the expected last-modified date isn't a date OneNote wrote.",
        userAction: $"Re-read the {what} and pass its lastModifiedTime exactly as OneNote wrote it.");

    /// <summary>
    /// AD-3: while an abandoned write may still be running in OneNote, only reads are sent. Anything else (a write,
    /// <c>NavigateTo</c>, <c>SyncHierarchy</c>) fails <see cref="ErrorCode.ComBusy"/> without touching COM.
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
            if (inFlight == 0 && pickerHolds == 0 && !disposed)
            {
                idleTimer.Change(settings.Timeouts.IdleRelease, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnIdle()
    {
        lock (gate)
        {
            // A picker waiting for the user holds the reference too (story 4.9; AD-3, epic 4 A8).
            if (disposed || inFlight > 0 || pickerHolds > 0 || this.worker is not { HasApplication: true } worker)
            {
                return;
            }

            worker.Post(worker.ReleaseApplication);
        }
    }
}
