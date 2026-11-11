using System.Globalization;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// AD-7's step runner: the one path every write takes. It owns the order, the lock, the deadline and the outcome; a
/// <see cref="WriteOperation{TResult}"/> supplies the steps.
/// <list type="number">
/// <item>Access check (<see cref="WriteOperation{TResult}.AuthorizeAsync"/>).</item>
/// <item>Take the write lock (AD-18), waiting up to <c>timeouts.callSeconds</c>, capped by what is left of the deadline.
/// A wait that expires is <see cref="ErrorCode.ComBusy"/>, retryable.</item>
/// <item>Read the current state; 4. validate; 5. optimistic check; 6. snapshot.</item>
/// <item value="7">The COM call. A failure here with <see cref="ErrorCode.Timeout"/>, <see cref="ErrorCode.ComUnavailable"/>
/// or <see cref="ErrorCode.Cancelled"/>, or with HRESULT <c>0x800706BE</c>, may have written: it gets
/// <c>outcomeUnknown</c> (AD-9). No failure before step 7 ever does.</item>
/// <item>Release the lock, after dropping the written pages from the search page-text cache, whatever the outcome.</item>
/// </list>
/// The whole pipeline has a deadline, 120 s by default (AD-7); its expiry is <see cref="ErrorCode.Timeout"/>. The
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
    private readonly PageTextCache? cache;
    private readonly Func<SettingsSnapshot> settings;
    private readonly TimeProvider clock;
    private readonly TimeSpan deadline;

    /// <param name="writeLock">The write lock (AD-18).</param>
    /// <param name="cache">This process's search page-text cache, or null when there is none.</param>
    /// <param name="settings">Reads <c>settings.json</c> for <c>timeouts.callSeconds</c>, once per write.</param>
    /// <param name="clock">The clock the deadline runs on.</param>
    /// <param name="deadline">The pipeline deadline; <see cref="DefaultDeadline"/> when null.</param>
    public WritePipeline(WriteLock writeLock, PageTextCache? cache, Func<SettingsSnapshot> settings, TimeProvider? clock = null, TimeSpan? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(writeLock);
        ArgumentNullException.ThrowIfNull(settings);
        this.writeLock = writeLock;
        this.cache = cache;
        this.settings = settings;
        this.clock = clock ?? TimeProvider.System;
        this.deadline = deadline ?? DefaultDeadline;
    }

    public TimeSpan Deadline => deadline;

    /// <summary>Runs <paramref name="operation"/> through steps 1 to 8 and returns its COM call's result.</summary>
    public async Task<TResult> RunAsync<TResult>(ToolCallContext context, WriteOperation<TResult> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(operation);

        var start = clock.GetTimestamp();
        using var deadlineSource = new CancellationTokenSource(deadline, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineSource.Token);
        var token = linked.Token;
        WriteLockHold? hold = null;
        try
        {
            // 1. Access check.
            await operation.AuthorizeAsync(token).ConfigureAwait(false);

            // 2. The write lock.
            var call = settings().Timeouts.Call;
            var remaining = deadline - clock.GetElapsedTime(start);
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
            await operation.SnapshotAsync(token).ConfigureAwait(false);

            // 7. The COM call.
            try
            {
                return await operation.CallAsync(token).ConfigureAwait(false);
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

            // 8. Release the lock.
            hold?.Dispose();
        }
    }

    /// <summary>A step-7 failure that may have written (AD-9): the outcome is unknown.</summary>
    public static bool IsAmbiguous(OneNoteException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Code is ErrorCode.Timeout or ErrorCode.ComUnavailable or ErrorCode.Cancelled
            || exception.ComHResult == CallFailedHResult;
    }

    private OneNoteException DeadlineExpired(bool written, Exception inner) => new(
        ErrorCode.Timeout,
        string.Create(CultureInfo.InvariantCulture, $"The write didn't finish within its {deadline.TotalSeconds:0.###} s deadline")
            + (written ? ", while OneNote was making it." : "; nothing was written."),
        innerException: inner,
        outcomeUnknown: written,
        retryable: written ? null : true); // nothing was written: safe to retry (AD-18)
}
