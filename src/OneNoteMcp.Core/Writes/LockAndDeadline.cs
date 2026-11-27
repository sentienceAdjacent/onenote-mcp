namespace OneNoteMcp.Core.Writes;

/// <summary>
/// The lock-and-deadline core both AD-7 pipelines share (<see cref="WritePipeline"/> and the <c>AppState</c> pipeline): the
/// capped lock wait, the abandoned-write wait and its <see cref="ErrorCode.ComBusy"/> check, the release now or in the
/// background, and the <see cref="ErrorCode.Timeout"/> shape. Each pipeline keeps its own messages, step order and deadline
/// arithmetic.
/// </summary>
internal static class LockAndDeadline
{
    /// <summary>
    /// Takes <paramref name="writeLock"/>, waiting up to <paramref name="lockWait"/> capped by <paramref name="remaining"/>
    /// (what is left of the deadline). A wait the deadline ended becomes <paramref name="deadlineExpired"/>'s error; one the
    /// lock wait ended stays <see cref="ErrorCode.ComBusy"/>.
    /// </summary>
    public static async Task<WriteLockHold> AcquireAsync(
        WriteLock writeLock,
        string correlationId,
        TimeSpan lockWait,
        TimeSpan remaining,
        Func<Exception, OneNoteException> deadlineExpired,
        CancellationToken token)
    {
        var cappedByDeadline = remaining <= lockWait;
        var wait = cappedByDeadline ? (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) : lockWait;
        try
        {
            return await writeLock.AcquireAsync(correlationId, wait, token).ConfigureAwait(false);
        }
        catch (OneNoteException ex) when (ex.Code == ErrorCode.ComBusy && cappedByDeadline)
        {
            // The wait ended because the deadline did, not the lock wait.
            throw deadlineExpired(ex);
        }
    }

    /// <summary>The gateway's wait for abandoned writes; a gateway that fails to answer counts as having none outstanding.</summary>
    public static Task AbandonedWrites(IOneNoteGateway gateway)
    {
        try
        {
            return gateway.WaitForAbandonedWritesAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // Called from finally too: it must never replace the call's own outcome.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Task.CompletedTask;
        }
    }

    /// <summary>While the gateway reports an abandoned write still outstanding: <see cref="ErrorCode.ComBusy"/>, retryable, with <paramref name="message"/> (AD-3).</summary>
    public static void ThrowIfAbandonedWriteOutstanding(IOneNoteGateway gateway, string message)
    {
        if (!AbandonedWrites(gateway).IsCompleted)
        {
            throw new OneNoteException(ErrorCode.ComBusy, message, retryable: true);
        }
    }

    /// <summary>
    /// Releases <paramref name="hold"/> now when <paramref name="settling"/> is done; otherwise in the background, once it
    /// settles or <paramref name="remaining"/> (what is left of the deadline) runs out, whichever is first, running
    /// <paramref name="beforeBackgroundRelease"/> just before. The caller's result isn't held back.
    /// </summary>
    public static void Release(WriteLockHold hold, Task settling, TimeSpan remaining, TimeProvider clock, Action? beforeBackgroundRelease = null)
    {
        if (settling.IsCompleted)
        {
            hold.Dispose();
        }
        else
        {
            _ = ReleaseWhenSettledAsync(hold, settling, remaining, clock, beforeBackgroundRelease);
        }
    }

    /// <summary>The deadline's <see cref="ErrorCode.Timeout"/>: retryable unless the outcome is unknown.</summary>
    public static OneNoteException Timeout(string message, bool unknown, Exception inner) => new(
        ErrorCode.Timeout,
        message,
        innerException: inner,
        outcomeUnknown: unknown,
        retryable: unknown ? null : true); // nothing sent, or nothing changed: safe to retry (AD-18)

    private static async Task ReleaseWhenSettledAsync(WriteLockHold hold, Task settling, TimeSpan remaining, TimeProvider clock, Action? beforeRelease)
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
            beforeRelease?.Invoke();
            hold.Dispose();
        }
    }
}
