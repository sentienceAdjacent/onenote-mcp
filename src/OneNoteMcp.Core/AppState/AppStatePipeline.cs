using System.Globalization;
using OneNoteMcp.Core.Writes;

namespace OneNoteMcp.Core.AppState;

/// <summary>
/// AD-7's <c>AppState</c> pipeline, beside <see cref="WritePipeline"/>: the path every <c>AppState</c> call takes
/// (stories 4.3 and 4.4). It runs only these steps, in this order:
/// <list type="number">
/// <item>For an operation that doesn't take the write lock, at the start: while the gateway reports an abandoned write still
/// outstanding, the call is <see cref="ErrorCode.ComBusy"/>, retryable, before anything is checked, read or sent (AD-3). An
/// abandoned <c>AppState</c> call isn't an abandoned write unless the gateway sends it as one (<c>CloseNotebook</c>,
/// <c>OpenHierarchy</c>).</item>
/// <item>Step 1: the tool's argument checks (<see cref="AppStateOperation{TResult}.Validate"/>). No grant of any kind.</item>
/// <item>Step 2, only for an operation that takes it (<see cref="AppStateOperation{TResult}.TakesWriteLock"/>; AD-18): the write
/// lock, waiting up to the lock wait (<c>timeouts.callSeconds</c>), capped by what is left of the deadline. A wait that
/// expires is <see cref="ErrorCode.ComBusy"/>, retryable (or <see cref="ErrorCode.Timeout"/> when the deadline is what ended
/// it). Then, while the gateway reports an abandoned write still outstanding, <see cref="ErrorCode.ComBusy"/>, retryable,
/// before anything is read (AD-3).</item>
/// <item>Steps 3 and 4: the tool's own reads and checks (<see cref="AppStateOperation{TResult}.ReadAsync"/>).</item>
/// <item value="7">The COM call. For a call that changes OneNote's state, a failure here with
/// <see cref="ErrorCode.Timeout"/>, <see cref="ErrorCode.ComUnavailable"/> or <see cref="ErrorCode.Cancelled"/>, or with
/// HRESULT <c>0x800706BE</c>, the deadline, or the client's cancel, gets <c>outcomeUnknown</c> (AD-9); an operation that marks
/// its send (<see cref="AppStateOperation{TResult}.MarksStateChange"/>) gets it only once it has marked it. It is never
/// retried. Every other failure, <see cref="ErrorCode.ModalDialogOpen"/> included, passes through unchanged.</item>
/// <item>Step 8, for an operation that took the lock: release it, whatever the outcome. Once step 7 has started, while the
/// gateway reports an abandoned write still running in OneNote, the lock is kept (in the background; the result isn't
/// delayed) until that call returns or the deadline runs out, whichever is first (AD-3, AD-18).</item>
/// </list>
/// No basis and no snapshot. The deadline is 120 s (AD-7); a deadline given to the constructor (a test's) overrides it. Its
/// expiry before the state-changing call is sent is <see cref="ErrorCode.Timeout"/>, retryable. The client's own cancel
/// before step 7 stays an <see cref="OperationCanceledException"/>, which the tool runner reports as
/// <see cref="ErrorCode.Cancelled"/>.
/// </summary>
public sealed class AppStatePipeline
{
    /// <summary>AD-7's deadline for an <c>AppState</c> call: the default 120 s.</summary>
    public static readonly TimeSpan DefaultDeadline = WritePipeline.DefaultDeadline;

    private readonly IOneNoteGateway gateway;
    private readonly TimeProvider clock;
    private readonly WriteLock? writeLock;
    private readonly Func<TimeSpan> lockWait;

    /// <param name="gateway">The gateway asked whether an abandoned write is outstanding.</param>
    /// <param name="clock">The clock the deadline runs on.</param>
    /// <param name="deadline">The deadline, a test's override; when null, <see cref="DefaultDeadline"/>.</param>
    /// <param name="writeLock">The write lock (AD-18) the operations that take it wait on; null when none may run here.</param>
    /// <param name="lockWait">How long step 2 waits for the lock (<c>timeouts.callSeconds</c>), read once per call; when null, the default 30 s.</param>
    public AppStatePipeline(IOneNoteGateway gateway, TimeProvider? clock = null, TimeSpan? deadline = null, WriteLock? writeLock = null, Func<TimeSpan>? lockWait = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        this.gateway = gateway;
        this.clock = clock ?? TimeProvider.System;
        Deadline = deadline ?? DefaultDeadline;
        this.writeLock = writeLock;
        this.lockWait = lockWait ?? (() => Settings.Timeouts.Default.Call);
    }

    /// <summary>The deadline every call runs under.</summary>
    public TimeSpan Deadline { get; }

    /// <summary>Runs <paramref name="operation"/> and returns its COM call's result.</summary>
    /// <param name="operation">The call.</param>
    /// <param name="ct">The client's token.</param>
    /// <param name="correlationId">The tool call's correlation ID, for the write lock's log line (AD-13).</param>
    public async Task<TResult> RunAsync<TResult>(AppStateOperation<TResult> operation, CancellationToken ct, string correlationId = "")
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(correlationId);

        var start = clock.GetTimestamp();
        using var deadlineSource = new CancellationTokenSource(Deadline, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineSource.Token);
        var token = linked.Token;
        WriteLockHold? hold = null;
        var calling = false;
        try
        {
            // AD-3: while an abandoned write is still outstanding in this process, an AppState call is ComBusy: at its start
            // for the lock-free ones, right after step 2 for the two that take the lock (AD-7 AppState pipeline, AD-18).
            if (!operation.TakesWriteLock)
            {
                ThrowIfAbandonedWriteOutstanding("nothing was sent to OneNote");
            }

            // 1. The tool's argument checks.
            operation.Validate();

            // 2. The write lock, for close_notebook and open_notebook only (Phill, 2026-11-22, OQ1 a).
            if (operation.TakesWriteLock)
            {
                hold = await AcquireAsync(correlationId, start, token).ConfigureAwait(false);
                ThrowIfAbandonedWriteOutstanding("nothing was read or sent to OneNote");
            }

            // 3–4. The tool's own reads and checks.
            await operation.ReadAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            // 7. The COM call, never retried.
            calling = true;
            try
            {
                return await operation.CallAsync(token).ConfigureAwait(false);
            }
            catch (OneNoteException ex) when (operation.OutcomeMayBeUnknown && WritePipeline.IsAmbiguous(ex))
            {
                throw new OneNoteException(ex.Code, ex.Message, ex.ComHResult, ex, outcomeUnknown: true, userAction: ex.UserAction);
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                var unknown = operation.OutcomeMayBeUnknown;
                throw new OneNoteException(
                    ErrorCode.Cancelled,
                    unknown ? "The call was cancelled while OneNote was handling it."
                        : operation.ChangesState ? "The call was cancelled before anything that changes OneNote's state was sent."
                        : "The call was cancelled.",
                    innerException: ex,
                    outcomeUnknown: unknown,
                    retryable: !unknown && operation.ChangesState ? true : null); // a cancel before close_notebook's CloseNotebook closed nothing (AD-9)
            }
            catch (OperationCanceledException ex) when (deadlineSource.IsCancellationRequested)
            {
                var unknown = operation.OutcomeMayBeUnknown;
                throw DeadlineExpired(
                    unknown || !operation.ChangesState ? ", while OneNote was handling it." : ", before anything that changes OneNote's state was sent.",
                    unknown,
                    ex);
            }
        }
        catch (OperationCanceledException ex) when (deadlineSource.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Before step 7: nothing was sent.
            throw DeadlineExpired("; nothing was sent to OneNote.", unknown: false, ex);
        }
        finally
        {
            // 8. Release the lock, unless a call abandoned in OneNote may still be running (AD-3, AD-18). Step 2 refused any
            // write outstanding before, so one outstanding once step 7 started is this call's.
            if (hold is not null)
            {
                var settling = calling ? LockAndDeadline.AbandonedWrites(gateway) : Task.CompletedTask;
                LockAndDeadline.Release(hold, settling, Deadline - clock.GetElapsedTime(start), clock);
            }
        }
    }

    /// <summary>Step 2: the lock, waiting up to the lock wait capped by what is left of the deadline.</summary>
    private async Task<WriteLockHold> AcquireAsync(string correlationId, long start, CancellationToken token)
    {
        if (writeLock is null)
        {
            throw new OneNoteException(ErrorCode.Internal, "This call takes the write lock, but the AppState pipeline was built without one; nothing was sent to OneNote.");
        }

        return await LockAndDeadline.AcquireAsync(
            writeLock,
            correlationId,
            lockWait(),
            Deadline - clock.GetElapsedTime(start),
            ex => DeadlineExpired("; nothing was sent to OneNote.", unknown: false, ex),
            token).ConfigureAwait(false);
    }

    private void ThrowIfAbandonedWriteOutstanding(string what) =>
        LockAndDeadline.ThrowIfAbandonedWriteOutstanding(gateway, $"An earlier write that OneNote may still be making hasn't finished, so {what}.");

    private OneNoteException DeadlineExpired(string tail, bool unknown, Exception inner) => LockAndDeadline.Timeout(
        string.Create(CultureInfo.InvariantCulture, $"The call didn't finish within its {Deadline.TotalSeconds:0.###} s deadline") + tail,
        unknown,
        inner);
}
