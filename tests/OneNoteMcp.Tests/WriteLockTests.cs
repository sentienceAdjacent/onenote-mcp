using System.Collections.Concurrent;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Writes;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 2.4: AD-18's write lock in one process. Every test uses a mutex name of its own, never
/// <c>Local\OneNoteMcp.Write</c>, so a running server is never touched. Cross-process exclusion is the integration test.
/// </summary>
public sealed class WriteLockTests
{
    private readonly ConcurrentQueue<WriteLockHeld> released = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WriteLock NewLock() => new($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", released.Enqueue);

    [Fact]
    public async Task ASecondHold_InTheSameProcess_WaitsUntilTheFirstReleases()
    {
        var writeLock = NewLock();
        var first = await writeLock.AcquireAsync("a", TimeSpan.FromSeconds(5), Ct);

        var second = writeLock.AcquireAsync("b", TimeSpan.FromSeconds(10), Ct);
        await Task.Delay(300, Ct);
        Assert.False(second.IsCompleted);

        first.Dispose();
        using var held = await second.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        held.Dispose();

        var holds = released.ToArray();
        Assert.Equal(["a", "b"], holds.Select(h => h.CorrelationId));
        Assert.True(holds[0].ReleasedUtc <= holds[1].AcquiredUtc);
        Assert.True(holds[1].WaitMs >= 250, $"waited {holds[1].WaitMs} ms");
    }

    [Fact]
    public async Task AWaitThatExpires_IsComBusy_Retryable_AndHoldsNothing()
    {
        var writeLock = NewLock();
        using (await writeLock.AcquireAsync("a", TimeSpan.FromSeconds(5), Ct))
        {
            var ex = await Assert.ThrowsAsync<OneNoteException>(() => writeLock.AcquireAsync("b", TimeSpan.FromMilliseconds(100), Ct));

            Assert.Equal(ErrorCode.ComBusy, ex.Code);
            Assert.False(ex.OutcomeUnknown);
            Assert.True(ToolErrors.From(ex, OperationClass.Create).Retryable);
        }

        // Nothing is left held: a new hold gets it at once.
        using var again = await writeLock.AcquireAsync("c", TimeSpan.FromMilliseconds(500), Ct);
        Assert.Equal(["a"], released.Select(h => h.CorrelationId));
    }

    [Fact]
    public async Task AnAbandonedHolder_CountsAsAcquired()
    {
        var writeLock = NewLock();

        // A handle kept open here keeps the named mutex alive once the holder closes its own, as a waiting process's would.
        using var keepAlive = new Mutex(initiallyOwned: false, writeLock.Name);

        // A holder that dies without releasing: its thread exits while it owns the mutex.
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, writeLock.Name);
            mutex.WaitOne();
        });
        holder.Start();
        holder.Join();

        using (await writeLock.AcquireAsync("after-abandon", TimeSpan.FromSeconds(2), Ct))
        {
        }

        var hold = Assert.Single(released);
        Assert.True(hold.Abandoned);
        Assert.Equal("after-abandon", hold.CorrelationId);
    }

    [Fact]
    public async Task ACancelledWait_ThrowsOperationCanceled_AndHoldsNothing()
    {
        var writeLock = NewLock();
        using var cts = new CancellationTokenSource();
        using (await writeLock.AcquireAsync("a", TimeSpan.FromSeconds(5), Ct))
        {
            var waiting = writeLock.AcquireAsync("b", TimeSpan.FromSeconds(30), cts.Token);
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        }

        using var again = await writeLock.AcquireAsync("c", TimeSpan.FromMilliseconds(500), Ct);
        Assert.Equal(["a"], released.Select(h => h.CorrelationId));
    }

    [Fact]
    public async Task EachRelease_IsReported_WithItsUtcTimes_BeforeDisposeReturns()
    {
        var writeLock = NewLock();
        var before = DateTime.UtcNow;

        var hold = await writeLock.AcquireAsync("c1", TimeSpan.FromSeconds(1), Ct);
        hold.Dispose();
        hold.Dispose(); // a second dispose is harmless

        var held = Assert.Single(released);
        Assert.Equal("c1", held.CorrelationId);
        Assert.Equal(DateTimeKind.Utc, held.AcquiredUtc.Kind);
        Assert.True(before <= held.AcquiredUtc && held.AcquiredUtc <= held.ReleasedUtc && held.ReleasedUtc <= DateTime.UtcNow);
        Assert.False(held.Abandoned);
        Assert.EndsWith("Z", WriteLockHeld.Format(held.AcquiredUtc), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReportThatThrows_StillReleases()
    {
        var writeLock = new WriteLock($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", _ => throw new InvalidOperationException("log failed"));

        (await writeLock.AcquireAsync("a", TimeSpan.FromSeconds(1), Ct)).Dispose();

        using var again = await writeLock.AcquireAsync("b", TimeSpan.FromMilliseconds(500), Ct);
    }
}
