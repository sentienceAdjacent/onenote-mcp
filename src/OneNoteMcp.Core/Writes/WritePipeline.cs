using System.Globalization;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// AD-7's step runner: the one path every write takes. It owns the order, the lock, the deadline and the outcome; a
/// <see cref="WriteOperation{TResult}"/> supplies the steps.
/// <list type="number">
/// <item>Access check (<see cref="WriteOperation{TResult}.AuthorizeAsync"/>). When it finds a notebook with a path but no
/// write grant, and the call can ask (<see cref="ToolCallContext.Prompt"/>), it raises the write-grant dialog
/// (<see cref="WriteGrantDialog"/>, AD-6) with the deadline paused and on the client's token only, then runs the check
/// once more, with no second dialog.</item>
/// <item>Take the write lock (AD-18), waiting up to <c>timeouts.callSeconds</c>, capped by what is left of the deadline.
/// A wait that expires is <see cref="ErrorCode.ComBusy"/>, retryable.</item>
/// <item>Read the current state; 4. validate; 5. optimistic check; 6. snapshot (AD-8). A step-6 failure that isn't a
/// <see cref="OneNoteException"/> (the snapshot's own COM reads keep their codes) or a cancellation becomes
/// <see cref="ErrorCode.JournalFailed"/>; like any failure before step 7, it is never <c>outcomeUnknown</c>.</item>
/// <item value="7">The COM call. A failure here with <see cref="ErrorCode.Timeout"/>, <see cref="ErrorCode.ComUnavailable"/>
/// or <see cref="ErrorCode.Cancelled"/>, or with HRESULT <c>0x800706BE</c>, may have written: it gets
/// <c>outcomeUnknown</c> (AD-9). No failure before step 7 ever does.</item>
/// <item>Release the lock, after dropping the written pages from the search page-text cache, whatever the outcome. After a
/// step-7 failure, while the gateway reports an abandoned write still running in OneNote, the lock is kept (in the
/// background; the result isn't delayed) until that call returns or the deadline runs out, whichever is first (AD-3).</item>
/// </list>
/// The whole pipeline has a deadline, 120 s by default (AD-7), not counting time spent on the grant dialog; its expiry is
/// <see cref="ErrorCode.Timeout"/>. The
/// client's own cancellation stays an <see cref="OperationCanceledException"/> before step 7, which the tool runner
/// reports as <see cref="ErrorCode.Cancelled"/>.
/// </summary>
public sealed class WritePipeline
{
    /// <summary>AD-7's deadline for a class that doesn't publish.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(120);

    /// <summary>The HRESULT that can mean OneNote crashed during the call (AD-9): RPC_S_CALL_FAILED.</summary>
    public const int CallFailedHResult = unchecked((int)0x800706BE);

    private readonly WriteLock writeLock;
    private readonly IOneNoteGateway gateway;
    private readonly PageTextCache? cache;
    private readonly Func<SettingsSnapshot> settings;
    private readonly TimeProvider clock;
    private readonly TimeSpan deadline;
    private readonly WriteGrantDialog? dialog;

    /// <param name="writeLock">The write lock (AD-18).</param>
    /// <param name="gateway">The gateway the operations call, asked after a step-7 failure whether an abandoned write is still running.</param>
    /// <param name="cache">This process's search page-text cache, or null when there is none.</param>
    /// <param name="settings">Reads <c>settings.json</c> for <c>timeouts.callSeconds</c>, once per write.</param>
    /// <param name="clock">The clock the deadline runs on.</param>
    /// <param name="deadline">The pipeline deadline; <see cref="DefaultDeadline"/> when null.</param>
    /// <param name="dialog">The write-grant dialog step 1 raises (AD-6), or null for none.</param>
    public WritePipeline(WriteLock writeLock, IOneNoteGateway gateway, PageTextCache? cache, Func<SettingsSnapshot> settings, TimeProvider? clock = null, TimeSpan? deadline = null, WriteGrantDialog? dialog = null)
    {
        ArgumentNullException.ThrowIfNull(writeLock);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(settings);
        this.writeLock = writeLock;
        this.gateway = gateway;
        this.cache = cache;
        this.settings = settings;
        this.clock = clock ?? TimeProvider.System;
        this.deadline = deadline ?? DefaultDeadline;
        this.dialog = dialog;
    }

    public TimeSpan Deadline => deadline;

    /// <summary>Runs <paramref name="operation"/> through steps 1 to 8 and returns its COM call's result.</summary>
    public async Task<TResult> RunAsync<TResult>(ToolCallContext context, WriteOperation<TResult> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);

        var start = clock.GetTimestamp();
        var paused = TimeSpan.Zero;
        using var deadlineSource = new CancellationTokenSource(deadline, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineSource.Token);
        var token = linked.Token;
        WriteLockHold? hold = null;
        var callFailed = false;
        try
        {
            // 1. Access check. A missing grant may raise the grant dialog (AD-6), then step 1 runs once more.
            try
            {
                await operation.AuthorizeAsync(token).ConfigureAwait(false);
            }
            catch (WriteGrantMissing missing) when (dialog is not null && context.Prompt is { } prompt)
            {
                token.ThrowIfCancellationRequested();

                // The deadline is paused while the user decides: the dialog waits on the client's token only, and
                // nothing is held meanwhile (no STA call, no write lock, no settings mutex).
                deadlineSource.CancelAfter(Timeout.InfiniteTimeSpan);
                var pausedAt = clock.GetTimestamp();
                try
                {
                    await dialog.AskAsync(prompt, missing, ct).ConfigureAwait(false);
                }
                finally
                {
                    paused += clock.GetElapsedTime(pausedAt);
                    var left = deadline - (clock.GetElapsedTime(start) - paused);
                    if (left > TimeSpan.Zero)
                    {
                        deadlineSource.CancelAfter(left);
                    }
                    else
                    {
                        await deadlineSource.CancelAsync().ConfigureAwait(false);
                    }
                }

                token.ThrowIfCancellationRequested();
                await operation.AuthorizeAsync(token).ConfigureAwait(false);
            }

            // 2. The write lock.
            var call = settings().Timeouts.Call;
            var remaining = deadline - (clock.GetElapsedTime(start) - paused);
            var cappedByDeadline = remaining <= call;
            var wait = cappedByDeadline ? (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) : call;
            try
            {
                hold = await writeLock.AcquireAsync(context.CorrelationId, wait, token).ConfigureAwait(false);
            }
            catch (OneNoteException ex) when (ex.Code == ErrorCode.ComBusy && cappedByDeadline)
            {
                // The wait ended because the deadline did, not the lock wait.
                throw DeadlineExpired(written: false, ex);
            }

            // 3–6. Read, validate, optimistic check, snapshot.
            await operation.ReadAsync(token).ConfigureAwait(false);
            operation.Validate();
            await operation.CheckBasisAsync(token).ConfigureAwait(false);
            try
            {
                await operation.SnapshotAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OneNoteException and not OperationCanceledException)
            {
                // Step 6 (AD-8): any other failure to build or store the snapshot is JournalFailed, never outcomeUnknown,
                // and the COM call doesn't run. A snapshot's own COM errors keep their codes (story 2.8 decision Q3).
                throw new OneNoteException(
                    ErrorCode.JournalFailed,
                    $"The recovery snapshot couldn't be taken ({ex.GetType().Name}), so nothing was written.",
                    innerException: ex);
            }

            // 7. The COM call.
            try
            {
                callFailed = true;
                var result = await operation.CallAsync(token).ConfigureAwait(false);
                callFailed = false;
                return result;
            }
            catch (OneNoteException ex) when (IsAmbiguous(ex))
            {
                throw new OneNoteException(ex.Code, ex.Message, ex.ComHResult, ex, outcomeUnknown: true, userAction: ex.UserAction);
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                throw new OneNoteException(ErrorCode.Cancelled, "The write was cancelled while OneNote was making it.", innerException: ex, outcomeUnknown: true);
            }
            catch (OperationCanceledException ex) when (deadlineSource.IsCancellationRequested)
            {
                throw DeadlineExpired(written: true, ex);
            }
        }
        catch (OperationCanceledException ex) when (deadlineSource.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Before step 7: nothing was written.
            throw DeadlineExpired(written: false, ex);
        }
        finally
        {
            // Whatever the outcome, before the lock goes (AD-7, AD-12).
            foreach (var pageId in operation.WrittenPageIds)
            {
                cache?.Remove(pageId);
            }

            // 8. Release the lock, unless a step-7 call abandoned in OneNote may still be writing (AD-3, AD-18).
            if (hold is not null)
            {
                var settling = callFailed ? AbandonedWrites() : Task.CompletedTask;
                if (settling.IsCompleted)
                {
                    hold.Dispose();
                }
                else
                {
                    // The result isn't held back: the lock goes in the background, when the call returns or at the deadline
                    // (which doesn't count time spent on the grant dialog).
                    _ = ReleaseWhenSettledAsync(hold, settling, deadline - (clock.GetElapsedTime(start) - paused), [.. operation.WrittenPageIds]);
                }
            }
        }
    }

    /// <summary>A step-7 failure that may have written (AD-9): the outcome is unknown.</summary>
    public static bool IsAmbiguous(OneNoteException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Code is ErrorCode.Timeout or ErrorCode.ComUnavailable or ErrorCode.Cancelled
            || exception.ComHResult == CallFailedHResult;
    }

    /// <summary>The gateway's wait for abandoned writes; a gateway that fails to answer releases the lock as before.</summary>
    private Task AbandonedWrites()
    {
        try
        {
            return gateway.WaitForAbandonedWritesAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // Called from finally: it must never replace the write's own outcome.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Keeps the lock until the abandoned call settles or <paramref name="remaining"/> (what is left of the deadline)
    /// runs out, whichever is first, then drops the written pages from the cache again and releases it.
    /// </summary>
    private async Task ReleaseWhenSettledAsync(WriteLockHold hold, Task settling, TimeSpan remaining, IReadOnlyList<string> writtenPageIds)
    {
        try
        {
            await settling.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, clock).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The deadline ran out, or the wait failed: release either way.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            // The abandoned call may have written after the first removal.
            foreach (var pageId in writtenPageIds)
            {
                cache?.Remove(pageId);
            }

            hold.Dispose();
        }
    }

    private OneNoteException DeadlineExpired(bool written, Exception inner) => new(
        ErrorCode.Timeout,
        string.Create(CultureInfo.InvariantCulture, $"The write didn't finish within its {deadline.TotalSeconds:0.###} s deadline")
            + (written ? ", while OneNote was making it." : "; nothing was written."),
        innerException: inner,
        outcomeUnknown: written,
        retryable: written ? null : true); // nothing was written: safe to retry (AD-18)
}
