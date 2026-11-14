using OneNoteMcp.Com;
using OneNoteMcp.Core;
using Xunit;
using GiveUpOutcome = OneNoteMcp.Com.ComGateway.GiveUpOutcome;

namespace OneNoteMcp.Tests;

/// <summary>
/// AD-3: when a waiter gives up on a call (its deadline, or its caller's cancel), the gateway must tell a call that
/// started in COM from one that never ran. Only the first can be a hung write; the second was abandoned while queued,
/// by this waiter or by a poisoned worker draining its queue.
/// </summary>
public sealed class WorkItemGiveUpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AQueuedItem_IsAbandonedByTheCaller_AndNeverRuns()
    {
        var ran = false;
        var item = new WorkItem<bool>(() => ran = true, CancellationToken.None);

        Assert.Equal(GiveUpOutcome.AbandonedByCaller, ComGateway.GiveUp(item, new TimeoutException()));

        item.Run();
        Assert.False(ran);
        Assert.IsType<TimeoutException>(item.Task.Exception?.InnerException);
    }

    [Fact]
    public async Task AnItemThatStarted_IsStarted()
    {
        var item = new WorkItem<int>(() => 1, CancellationToken.None);
        item.Run();

        Assert.Equal(GiveUpOutcome.Started, ComGateway.GiveUp(item, new TimeoutException()));
        Assert.Equal(1, await item.Task);
    }

    [Fact]
    public void AnItemAbandonedByAnotherWaiter_IsAbandonedElsewhere_NotStarted()
    {
        var item = new WorkItem<int>(() => 1, CancellationToken.None);
        Assert.True(item.TryAbandon(new InvalidOperationException("first")));

        Assert.Equal(GiveUpOutcome.AbandonedElsewhere, ComGateway.GiveUp(item, new TimeoutException()));
        Assert.Equal("first", item.Task.Exception?.InnerException?.Message);
    }

    [Fact]
    public async Task AQueuedItemDrainedByAPoisonedWorker_IsAbandonedElsewhere_NotStarted()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var worker = new StaThread("give-up-test");
        var hung = worker.Enqueue(
            () =>
            {
                entered.Set();
                release.Wait();
                return 0;
            },
            Ct);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), Ct));

        var ran = false;
        var queued = worker.Enqueue(() => ran = true, Ct);
        worker.Poison(() => new OneNoteException(ErrorCode.ComBusy, "poisoned"));

        try
        {
            // The race: the poisoned worker failed the queued item first, so this waiter's own abandon loses.
            Assert.Equal(GiveUpOutcome.AbandonedElsewhere, ComGateway.GiveUp(queued, new TimeoutException()));
            Assert.Equal(ErrorCode.ComBusy, (await Assert.ThrowsAsync<OneNoteException>(() => queued.Task)).Code);
        }
        finally
        {
            release.Set();
        }

        Assert.Equal(0, await hung.Task);
        await worker.Exited.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.False(ran);
    }
}
