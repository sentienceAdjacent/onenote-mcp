using System.Diagnostics;
using System.Globalization;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// What one hold of the write lock looked like, for the log (AD-13: IDs and timings only). Times are UTC from the system's
/// precise clock, so holds logged by two processes compare. <see cref="WaitMs"/> is how long the hold waited to acquire.
/// <see cref="Abandoned"/> is true when a holder that died left the mutex abandoned, which counts as acquiring it (AD-18).
/// </summary>
public sealed record WriteLockHeld(string CorrelationId, DateTime AcquiredUtc, DateTime ReleasedUtc, long WaitMs, bool Abandoned)
{
    /// <summary>A UTC time as the log line writes it: round-trip ISO-8601, 100 ns precision.</summary>
    public static string Format(DateTime utc) => utc.ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>
/// AD-18's machine-wide write lock, the named mutex <c>Local\OneNoteMcp.Write</c>. A named <see cref="Mutex"/> is
/// thread-affine and the pipeline awaits between steps, so each hold acquires and releases it on a dedicated thread of
/// its own. Two holds therefore exclude each other in one process as across processes. A wait that expires is
/// <see cref="ErrorCode.ComBusy"/>, retryable; an <see cref="AbandonedMutexException"/> counts as acquiring. Reads never
/// take it. Each release reports the hold to <c>onReleased</c>, for the <c>write lock</c> log line.
/// </summary>
public sealed class WriteLock
{
    public const string DefaultName = @"Local\OneNoteMcp.Write";

    private readonly string name;
    private readonly Action<WriteLockHeld>? onReleased;

    /// <param name="name">The mutex name: <see cref="DefaultName"/>, or a unique one per unit test.</param>
    /// <param name="onReleased">Called on the hold's thread after each release, before the hold's dispose returns; an exception from it is ignored.</param>
    public WriteLock(string name = DefaultName, Action<WriteLockHeld>? onReleased = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        this.name = name;
        this.onReleased = onReleased;
    }

    public string Name => name;

    /// <summary>
    /// Waits up to <paramref name="wait"/> for the lock, on a new thread that then holds it until the returned hold is
    /// disposed. Throws <see cref="ErrorCode.ComBusy"/> when the wait expires, and <see cref="OperationCanceledException"/>
    /// when <paramref name="ct"/> is cancelled first. Nothing is held after either.
    /// </summary>
    public Task<WriteLockHold> AcquireAsync(string correlationId, TimeSpan wait, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(correlationId);
        ct.ThrowIfCancellationRequested();
        var hold = new WriteLockHold(this, correlationId);
        var thread = new Thread(() => hold.Run(wait, ct))
        {
            IsBackground = true,
            Name = "OneNoteMcp.WriteLock",
        };
        thread.Start();
        return hold.Acquired;
    }

    internal Mutex Open() => new(initiallyOwned: false, name);

    internal void Released(WriteLockHeld held) => onReleased?.Invoke(held);
}

/// <summary>
/// One hold of <see cref="WriteLock"/>: the thread that owns the mutex until <see cref="Dispose"/>. Dispose releases it
/// on that thread and returns once it is released, so a step after it never runs under the lock.
/// </summary>
public sealed class WriteLockHold : IDisposable
{
    private readonly WriteLock owner;
    private readonly string correlationId;
    private readonly TaskCompletionSource<WriteLockHold> acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim release = new(false);
    private readonly ManualResetEventSlim released = new(false);
    private int disposed;

    internal WriteLockHold(WriteLock owner, string correlationId)
    {
        this.owner = owner;
        this.correlationId = correlationId;
    }

    internal Task<WriteLockHold> Acquired => acquired.Task;

    /// <summary>Releases the lock (once) and waits until its thread has released it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        release.Set();
        released.Wait();
        release.Dispose();
        released.Dispose();
    }

    internal void Run(TimeSpan wait, CancellationToken ct)
    {
        Mutex mutex;
        try
        {
            mutex = owner.Open();
        }
#pragma warning disable CA1031 // Any failure to open the mutex fails the acquire; nothing is held.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            acquired.TrySetException(new OneNoteException(ErrorCode.Internal, $"The write lock couldn't be opened ({ex.GetType().Name}).", innerException: ex));
            return;
        }

        using (mutex)
        {
            var stopwatch = Stopwatch.StartNew();
            bool owned;
            var abandoned = false;
            try
            {
                var index = WaitHandle.WaitAny([mutex, ct.WaitHandle], wait);
                owned = index == 0;
                if (index == 1)
                {
                    acquired.TrySetCanceled(ct);
                    return;
                }
            }
            catch (AbandonedMutexException)
            {
                // A holder that died left it abandoned: the lock is ours (AD-18).
                owned = true;
                abandoned = true;
            }

            if (!owned)
            {
                acquired.TrySetException(new OneNoteException(
                    ErrorCode.ComBusy,
                    $"Another write held the write lock for longer than {wait.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} s, so nothing was written.",
                    userAction: "Another OneNote write is in progress. Retry in a moment."));
                return;
            }

            var acquiredUtc = DateTime.UtcNow;
            var waitMs = stopwatch.ElapsedMilliseconds;
            acquired.TrySetResult(this);
            release.Wait();
            var releasedUtc = DateTime.UtcNow;
            mutex.ReleaseMutex();
            try
            {
                owner.Released(new WriteLockHeld(correlationId, acquiredUtc, releasedUtc, waitMs, abandoned));
            }
#pragma warning disable CA1031 // The report is for the log only; it must never keep the hold from finishing.
            catch (Exception)
#pragma warning restore CA1031
            {
            }

            released.Set();
        }
    }
}
