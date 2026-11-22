using System.Globalization;
using OneNoteMcp.Core.Writes;

namespace OneNoteMcp.Core.AppState;

/// <summary>
/// AD-7's <c>AppState</c> pipeline, beside <see cref="WritePipeline"/>: the path every <c>AppState</c> call takes
/// (story 4.3). It runs only these steps, in this order:
/// <list type="number">
/// <item>At the start, while the gateway reports an abandoned write still outstanding, the call is
/// <see cref="ErrorCode.ComBusy"/>, retryable, before anything is checked, read or sent (AD-3). An abandoned
/// <c>AppState</c> call isn't an abandoned write: the gateway tracks only writes.</item>
/// <item>Step 1: the tool's argument checks (<see cref="AppStateOperation{TResult}.Validate"/>). No grant of any kind.</item>
/// <item>Steps 3 and 4: the tool's own reads and checks (<see cref="AppStateOperation{TResult}.ReadAsync"/>).</item>
/// <item value="7">The COM call. For a call that changes OneNote's state, a failure here with
/// <see cref="ErrorCode.Timeout"/>, <see cref="ErrorCode.ComUnavailable"/> or <see cref="ErrorCode.Cancelled"/>, or with
/// HRESULT <c>0x800706BE</c>, the deadline, or the client's cancel, gets <c>outcomeUnknown</c> (AD-9). It is never
/// retried. Every other failure, <see cref="ErrorCode.ModalDialogOpen"/> included, passes through unchanged.</item>
/// </list>
/// No write lock (stories 4.4 and 4.5 add it for <c>close_notebook</c> and <c>open_notebook</c>), no basis and no
/// snapshot. The deadline is 120 s (AD-7); a deadline given to the constructor (a test's) overrides it. Its expiry before
/// step 7 is <see cref="ErrorCode.Timeout"/>, retryable, since nothing was sent. The client's own cancel before step 7
/// stays an <see cref="OperationCanceledException"/>, which the tool runner reports as <see cref="ErrorCode.Cancelled"/>.
/// </summary>
public sealed class AppStatePipeline
{
    /// <summary>AD-7's deadline for an <c>AppState</c> call: the default 120 s.</summary>
    public static readonly TimeSpan DefaultDeadline = WritePipeline.DefaultDeadline;

    private readonly IOneNoteGateway gateway;
    private readonly TimeProvider clock;

    /// <param name="gateway">The gateway asked at the start whether an abandoned write is outstanding.</param>
    /// <param name="clock">The clock the deadline runs on.</param>
    /// <param name="deadline">The deadline, a test's override; when null, <see cref="DefaultDeadline"/>.</param>
    public AppStatePipeline(IOneNoteGateway gateway, TimeProvider? clock = null, TimeSpan? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        this.gateway = gateway;
        this.clock = clock ?? TimeProvider.System;
        Deadline = deadline ?? DefaultDeadline;
    }

    /// <summary>The deadline every call runs under.</summary>
    public TimeSpan Deadline { get; }

    /// <summary>Runs <paramref name="operation"/> and returns its COM call's result.</summary>
    public async Task<TResult> RunAsync<TResult>(AppStateOperation<TResult> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);

        using var deadlineSource = new CancellationTokenSource(Deadline, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineSource.Token);
        var token = linked.Token;
        try
        {
            // AD-3: while an abandoned write is still outstanding in this process, an AppState call is ComBusy, at its
            // start (AD-7 AppState pipeline; the two that take the lock check after it instead, stories 4.4 and 4.5).
            if (!AbandonedWritesSettled())
            {
                throw new OneNoteException(
                    ErrorCode.ComBusy,
                    "An earlier write that OneNote may still be making hasn't finished, so nothing was sent to OneNote.",
                    retryable: true);
            }

            // 1. The tool's argument checks.
            operation.Validate();

            // 3–4. The tool's own reads and checks.
            await operation.ReadAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            // 7. The COM call, never retried.
            try
            {
                return await operation.CallAsync(token).ConfigureAwait(false);
            }
            catch (OneNoteException ex) when (operation.ChangesState && WritePipeline.IsAmbiguous(ex))
            {
                throw new OneNoteException(ex.Code, ex.Message, ex.ComHResult, ex, outcomeUnknown: true, userAction: ex.UserAction);
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                throw new OneNoteException(
                    ErrorCode.Cancelled,
                    operation.ChangesState ? "The call was cancelled while OneNote was handling it." : "The call was cancelled.",
                    innerException: ex,
                    outcomeUnknown: operation.ChangesState);
            }
            catch (OperationCanceledException ex) when (deadlineSource.IsCancellationRequested)
            {
                throw DeadlineExpired(sent: true, unknown: operation.ChangesState, ex);
            }
        }
        catch (OperationCanceledException ex) when (deadlineSource.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Before step 7: nothing was sent.
            throw DeadlineExpired(sent: false, unknown: false, ex);
        }
    }

    /// <summary>The gateway's answer; a gateway that fails to answer counts as having none outstanding, as the write pipeline does.</summary>
    private bool AbandonedWritesSettled()
    {
        try
        {
            return gateway.WaitForAbandonedWritesAsync(CancellationToken.None).IsCompleted;
        }
#pragma warning disable CA1031 // The gateway itself refuses every non-read call while a write is outstanding (AD-3).
        catch (Exception)
#pragma warning restore CA1031
        {
            return true;
        }
    }

    private OneNoteException DeadlineExpired(bool sent, bool unknown, Exception inner) => new(
        ErrorCode.Timeout,
        string.Create(CultureInfo.InvariantCulture, $"The call didn't finish within its {Deadline.TotalSeconds:0.###} s deadline")
            + (sent ? ", while OneNote was handling it." : "; nothing was sent to OneNote."),
        innerException: inner,
        outcomeUnknown: unknown,
        retryable: unknown ? null : true); // nothing sent, or nothing changed: safe to retry
}
