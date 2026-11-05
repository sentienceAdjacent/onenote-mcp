using System.Diagnostics;
using System.Runtime.InteropServices;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using Xunit;
using InteropPageInfo = OneNoteMcp.Com.Interop.PageInfo;
using InteropSpecialLocation = OneNoteMcp.Com.Interop.SpecialLocation;
using XMLSchema = OneNoteMcp.Com.Interop.XMLSchema;

namespace OneNoteMcp.Tests;

/// <summary>
/// Every row of story 5's I/O matrix (AD-3) against <see cref="FakeApplication"/>, with timeouts injected in
/// milliseconds, plus each port method's argument mapping.
/// </summary>
public sealed class ComGatewayLifecycleTests
{
    private const int RpcServerUnavailable = unchecked((int)0x800706BA);
    private const int RpcDisconnected = unchecked((int)0x80010108);
    private const int RpcCallFailed = unchecked((int)0x800706BE);
    private const int RpcCallRejected = unchecked((int)0x80010001);
    private const int RetryLater = unchecked((int)0x8001010A);
    private const int NotYetSynchronized = unchecked((int)0x8004201D);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- Hang ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Hang_TimesOut_QueuedCallFailsComBusy_NextCallGetsFreshWorker()
    {
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var app = new FakeApplication();
        app.OnCall = _ =>
        {
            entered.Set();
            gate.Wait();
            return "<late/>";
        };
        using var gateway = Gateway(app, Settings(callMs: 400));

        var hung = gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), Ct));
        await Task.Delay(100, Ct);
        var queued = gateway.GetHierarchyParentAsync("{p}", Ct);

        var timeout = await Assert.ThrowsAsync<OneNoteException>(() => hung);
        Assert.Equal(ErrorCode.Timeout, timeout.Code);
        var busy = await Assert.ThrowsAsync<OneNoteException>(() => queued);
        Assert.Equal(ErrorCode.ComBusy, busy.Code);
        Assert.Equal(1, gateway.AbandonedWorkers);

        app.OnCall = _ => "<fresh/>";
        Assert.Equal("<fresh/>", await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct));

        var threads = app.Calls.Where(c => c.Method == "GetHierarchy").Select(c => c.ThreadId).ToList();
        Assert.Equal(2, threads.Count);
        Assert.NotEqual(threads[0], threads[1]);
        Assert.Equal(2, app.Activations);

        // The abandoned call returns: its worker releases its reference and exits, clearing the count.
        gate.Set();
        await WaitUntil(() => gateway.AbandonedWorkers == 0);
    }

    [Fact]
    public async Task CancellingAStartedHungCall_StillPoisonsAtTheDeadline_NextCallGetsFreshWorker()
    {
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var app = new FakeApplication();
        app.OnCall = _ =>
        {
            entered.Set();
            gate.Wait();
            return "<late/>";
        };
        using var gateway = Gateway(app, Settings(callMs: 300));

        var hung = gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, cancel.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), Ct));
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hung);

        // The worker is poisoned at the call's original deadline, not left running with the queue behind it.
        await WaitUntil(() => gateway.AbandonedWorkers == 1);
        app.OnCall = _ => "<fresh/>";
        Assert.Equal("<fresh/>", await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct));
        var threads = app.Calls.Select(c => c.ThreadId).Distinct().ToList();
        Assert.Equal(2, threads.Count);

        gate.Set();
        await WaitUntil(() => gateway.AbandonedWorkers == 0);
    }

    // --- Abandoned bound ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoAbandonedWorkers_EveryCallIsComUnavailable_UntilAnAbandonedCallReturns()
    {
        using var gate = new ManualResetEventSlim(false);
        var app = new FakeApplication();
        app.OnCall = _ =>
        {
            gate.Wait();
            return "<late/>";
        };
        using var gateway = Gateway(app, Settings(callMs: 200));

        for (var i = 0; i < ComGateway.MaxAbandonedWorkers; i++)
        {
            var ex = await Assert.ThrowsAsync<OneNoteException>(() => gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct));
            Assert.Equal(ErrorCode.Timeout, ex.Code);
        }

        Assert.Equal(2, gateway.AbandonedWorkers);
        var activations = app.Activations;
        var calls = app.Calls.Count;

        var unavailable = await Assert.ThrowsAsync<OneNoteException>(() => gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct));
        Assert.Equal(ErrorCode.ComUnavailable, unavailable.Code);
        Assert.Equal(activations, app.Activations);
        Assert.Equal(calls, app.Calls.Count);

        app.OnCall = _ => "<ok/>";
        gate.Set();
        await WaitUntil(() => gateway.AbandonedWorkers == 0);
        Assert.Equal("<ok/>", await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct));
    }

    // --- Disconnect ---------------------------------------------------------------------------------------------

    public static TheoryData<string> ReconnectTriggers => ["0x800706BA", "0x80010108", "InvalidComObjectException"];

    [Theory]
    [MemberData(nameof(ReconnectTriggers))]
    public async Task Disconnect_ReactivatesWithEveryRule_AndRetriesTheReadOnce(string trigger)
    {
        var probe = new TestProbe();
        var app = new FakeApplication();
        var failures = 1;
        app.OnCall = _ => Interlocked.Decrement(ref failures) >= 0 ? throw Trigger(trigger) : "<ok/>";
        var settingsReads = 0;
        using var gateway = new ComGateway(probe, () =>
        {
            Interlocked.Increment(ref settingsReads);
            return Settings();
        }, app.Factory);

        Assert.Equal("<ok/>", await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct));

        Assert.Equal(2, app.Activations);
        Assert.Equal(2, app.Calls.Count);
        Assert.Equal(2, probe.IntegrityChecks);
        Assert.Equal(2, settingsReads);
    }

    [Theory]
    [MemberData(nameof(ReconnectTriggers))]
    public async Task Disconnect_Twice_IsComUnavailable(string trigger)
    {
        var app = new FakeApplication { OnCall = _ => throw Trigger(trigger) };
        using var gateway = Gateway(app, Settings());

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => gateway.GetPageContentAsync("{p}", PageInfo.Basic, Ct));

        Assert.Equal(ErrorCode.ComUnavailable, ex.Code);
        Assert.Equal(2, app.Calls.Count);
        Assert.Equal(2, app.Activations);
        Assert.NotNull(ex.ComHResult);
    }

    [Fact]
    public async Task RpcCallFailed_IsNotAReconnectTrigger()
    {
        var app = new FakeApplication { OnCall = _ => throw new COMException("bad xml?", RpcCallFailed) };
        using var gateway = Gateway(app, Settings());

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct));

        Assert.Equal(ErrorCode.Internal, ex.Code);
        Assert.Single(app.Calls);
        Assert.Equal(1, app.Activations);
    }

    [Fact]
    public async Task RawSeam_WithoutRetry_DoesNotRetryADisconnect()
    {
        var app = new FakeApplication { OnCall = _ => throw new COMException("gone", RpcServerUnavailable) };
        using var gateway = Gateway(app, Settings());

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => gateway.InvokeAsync(
            a =>
            {
                a.GetHierarchyParent("{w}", out var parent);
                return parent;
            },
            Ct,
            retryOnDisconnect: false));

        Assert.Equal(ErrorCode.ComUnavailable, ex.Code);
        Assert.Single(app.Calls);
    }

    // --- Busy ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RpcCallRejected)]
    [InlineData(RetryLater)]
    public async Task Busy_IsRetriedWithBackoff_ThenSucceeds(int hresult)
    {
        var app = new FakeApplication();
        var busy = 3;
        app.OnCall = _ => Interlocked.Decrement(ref busy) >= 0 ? throw new COMException("busy", hresult) : "<ok/>";
        using var gateway = Gateway(app, Settings(callMs: 5000));

        Assert.Equal("<ok/>", await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct));
        Assert.Equal(4, app.Calls.Count);
        Assert.Equal(1, app.Activations);
    }

    [Fact]
    public async Task Busy_StopsBeforeTheDeadline_WithComBusy_AndNeverPoisons()
    {
        var app = new FakeApplication { OnCall = _ => throw new COMException("busy", RpcCallRejected) };
        using var gateway = Gateway(app, Settings(callMs: 600));

        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<OneNoteException>(() => gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct));
        stopwatch.Stop();

        Assert.Equal(ErrorCode.ComBusy, ex.Code);
        Assert.Equal(RpcCallRejected, ex.ComHResult);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(600), $"busy retry ran {stopwatch.Elapsed}");
        Assert.True(app.Calls.Count > 1);
        Assert.Equal(0, gateway.AbandonedWorkers);

        app.OnCall = _ => "<ok/>";
        Assert.Equal("<ok/>", await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct));
        Assert.Single(app.Calls.Select(c => c.ThreadId).Distinct());
    }

    // --- Not synced ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task NotYetSynchronized_IsComBusyAtOnce_NoRetry()
    {
        var app = new FakeApplication { OnCall = _ => throw new COMException("sync", NotYetSynchronized) };
        using var gateway = Gateway(app, Settings(callMs: 5000));

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => gateway.GetPageContentAsync("{p}", PageInfo.Basic, Ct));

        Assert.Equal(ErrorCode.ComBusy, ex.Code);
        Assert.Single(app.Calls);
    }

    // --- Not running --------------------------------------------------------------------------------------------

    [Fact]
    public async Task NotRunning_IsOneNoteNotRunning_NothingCreated_NoIntegrityCheck()
    {
        var probe = new TestProbe { Running = false };
        var app = new FakeApplication { OnCall = _ => "<ok/>" };
        using var gateway = new ComGateway(probe, () => Settings(), app.Factory);

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct));

        Assert.Equal(ErrorCode.OneNoteNotRunning, ex.Code);
        Assert.Equal(0, app.Activations);
        Assert.Equal(0, probe.IntegrityChecks);
    }

    [Fact]
    public async Task NotRunning_WithAllowSilentActivation_Activates()
    {
        var probe = new TestProbe { Running = false };
        var app = new FakeApplication { OnCall = _ => "<ok/>" };
        using var gateway = new ComGateway(probe, () => Settings(silent: true), app.Factory);

        Assert.Equal("<ok/>", await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct));
        Assert.Equal(1, app.Activations);
        Assert.Equal(1, probe.IntegrityChecks);
    }

    [Fact]
    public async Task OneNoteCloses_ReferenceIsDropped_AndNotRunningIsReported()
    {
        var probe = new TestProbe();
        var app = new FakeApplication { OnCall = _ => "<ok/>" };
        using var gateway = new ComGateway(probe, () => Settings(), app.Factory);
        await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct);
        Assert.True(gateway.HasApplication);

        probe.Running = false;
        var ex = await Assert.ThrowsAsync<OneNoteException>(() => gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct));

        Assert.Equal(ErrorCode.OneNoteNotRunning, ex.Code);
        await WaitUntil(() => !gateway.HasApplication);
        Assert.Single(app.Calls);
    }

    // --- Integrity ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task IntegrityMismatch_IsFast_AndComIsNeverCreated()
    {
        var probe = new TestProbe { Integrity = new IntegrityReport(0x2000, [new OneNoteIntegrity(42, 0x3000)]) };
        var app = new FakeApplication { OnCall = _ => "<ok/>" };
        using var gateway = new ComGateway(probe, () => Settings(), app.Factory);

        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<OneNoteException>(() => gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct));
        stopwatch.Stop();

        Assert.Equal(ErrorCode.IntegrityMismatch, ex.Code);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"integrity check took {stopwatch.Elapsed}");
        Assert.Equal(0, app.Activations);
        Assert.Contains("server=Medium onenote=[42:High]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IntegrityReport_UnreadableLevel_IsAMismatch()
    {
        Assert.False(new IntegrityReport(0x2000, [new OneNoteIntegrity(1, 0x2000), new OneNoteIntegrity(2, null)]).Matches);
        Assert.False(new IntegrityReport(null, []).Matches);
        Assert.True(new IntegrityReport(0x2000, []).Matches);
        Assert.True(new IntegrityReport(0x2000, [new OneNoteIntegrity(1, 0x2000)]).Matches);
        Assert.Equal("server=Medium onenote=[2:unreadable]", new IntegrityReport(0x2000, [new OneNoteIntegrity(2, null)]).Describe());
    }

    [Fact]
    public void IntegrityReport_SkipsAOneNoteThatExited_ButKeepsAnUnreadableLiveOne()
    {
        int? Level(int pid) => pid == 1 ? 0x2000 : null;
        bool Exists(int pid) => pid != 2; // 2 exited between listing and opening; 3 is alive but its token is denied

        var report = ProcessProbe.BuildReport(0x2000, [1, 2, 3], Level, Exists);
        Assert.Equal([1, 3], report.OneNote.Select(o => o.ProcessId));
        Assert.False(report.Matches);

        var onlyExited = ProcessProbe.BuildReport(0x2000, [2], Level, Exists);
        Assert.Empty(onlyExited.OneNote);
        Assert.True(onlyExited.Matches); // the process check decides not-running
    }

    [Fact]
    public void StartupLog_Integrity_InfoOnMatch_WarningOnMismatch()
    {
        var logger = new CapturingLogger();

        Server.Logging.StartupLog.Integrity(logger, new IntegrityReport(0x2000, [new OneNoteIntegrity(7, 0x2000)]));
        Server.Logging.StartupLog.Integrity(logger, new IntegrityReport(0x3000, [new OneNoteIntegrity(7, 0x2000)]));

        Assert.Equal(2, logger.Entries.Count);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, logger.Entries[0].Level);
        Assert.Equal("integrity check match=true server=Medium onenote=[7:Medium]", logger.Entries[0].Message);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, logger.Entries[1].Level);
        Assert.StartsWith("integrity check match=false server=High onenote=[7:Medium]", logger.Entries[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessProbe_ReadsThisProcessIntegrityLevel()
    {
        var report = new ProcessProbe().CheckIntegrity();

        Assert.NotNull(report.ServerLevel);
        Assert.InRange(report.ServerLevel.Value, 0x1000, 0x4000);
    }

    // --- Activation hang ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ActivationHang_TimesOut_PoisonsTheWorker_NextCallActivatesAFreshOne()
    {
        using var gate = new ManualResetEventSlim(false);
        var app = new FakeApplication { OnCall = _ => "<ok/>" };
        var first = true;
        using var gateway = new ComGateway(new TestProbe(), () => Settings(activationMs: 300), () =>
        {
            if (first)
            {
                first = false;
                gate.Wait();
            }

            return app;
        });

        var hung = gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct);
        await Task.Delay(100, Ct);
        var queued = gateway.GetHierarchyParentAsync("{p}", Ct);

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => hung);
        Assert.Equal(ErrorCode.Timeout, ex.Code);
        var busy = await Assert.ThrowsAsync<OneNoteException>(() => queued);
        Assert.Equal(ErrorCode.ComBusy, busy.Code); // as Hang: a call waiting behind the hung activation fails ComBusy
        Assert.Equal(1, gateway.AbandonedWorkers);

        Assert.Equal("<ok/>", await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct));

        gate.Set();
        await WaitUntil(() => gateway.AbandonedWorkers == 0);
    }

    // --- Idle ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Idle_ReleasesTheReference_NextCallReactivates()
    {
        var app = new FakeApplication { OnCall = _ => "<ok/>" };
        using var gateway = Gateway(app, Settings(idleMs: 200));

        await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct);
        Assert.True(gateway.HasApplication);
        await WaitUntil(() => !gateway.HasApplication);

        await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct);
        Assert.Equal(2, app.Activations);
    }

    [Fact]
    public async Task Settings_AreReadAtEveryActivation()
    {
        var reads = 0;
        var app = new FakeApplication { OnCall = _ => "<ok/>" };
        using var gateway = new ComGateway(new TestProbe(), () =>
        {
            Interlocked.Increment(ref reads);
            return Settings(idleMs: 150);
        }, app.Factory);

        await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct);
        await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct);
        Assert.Equal(1, reads);

        await WaitUntil(() => !gateway.HasApplication);
        await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, Ct);
        Assert.Equal(2, reads);
    }

    // --- Port method argument mapping ---------------------------------------------------------------------------

    [Fact]
    public async Task PortMethods_MapTheirArguments_AndAlwaysPassXs2013()
    {
        var app = new FakeApplication { OnCall = c => $"<{c.Method}/>" };
        using var gateway = Gateway(app, Settings());

        Assert.Equal("<GetHierarchyParent/>", await gateway.GetHierarchyParentAsync("{o}", Ct));
        Assert.Equal("<GetPageContent/>", await gateway.GetPageContentAsync("{p}", PageInfo.BinaryData, Ct));
        Assert.Equal("<GetBinaryPageContent/>", await gateway.GetBinaryPageContentAsync("{p}", "{cb}", Ct));
        Assert.Equal("<FindPages/>", await gateway.FindPagesAsync("{s}", "query", includeUnindexed: true, Ct));
        Assert.Equal("<FindMeta/>", await gateway.FindMetaAsync("{s}", "meta", includeUnindexed: false, Ct));
        Assert.Equal("<GetHyperlinkToObject/>", await gateway.GetHyperlinkToObjectAsync("{h}", "{oe}", Ct));
        Assert.Equal("<GetWebHyperlinkToObject/>", await gateway.GetWebHyperlinkToObjectAsync("{h}", string.Empty, Ct));
        Assert.Equal("<GetSpecialLocation/>", await gateway.GetSpecialLocationAsync(SpecialLocation.UnfiledNotesSection, Ct));

        var calls = app.Calls.ToDictionary(c => c.Method, c => c.Args);
        Assert.Equal(["{o}"], calls["GetHierarchyParent"]);
        Assert.Equal(["{p}", InteropPageInfo.piBinaryData, XMLSchema.xs2013], calls["GetPageContent"]);
        Assert.Equal(["{p}", "{cb}"], calls["GetBinaryPageContent"]);
        Assert.Equal(["{s}", "query", true, false, XMLSchema.xs2013], calls["FindPages"]); // fDisplay is always false (AD-11)
        Assert.Equal(["{s}", "meta", false, XMLSchema.xs2013], calls["FindMeta"]);
        Assert.Equal(["{h}", "{oe}"], calls["GetHyperlinkToObject"]);
        Assert.Equal(["{h}", string.Empty], calls["GetWebHyperlinkToObject"]);
        Assert.Equal([InteropSpecialLocation.slUnfiledNotesSection], calls["GetSpecialLocation"]);
    }

    [Fact]
    public async Task GetHierarchy_PassesStartScopeAndXs2013()
    {
        var app = new FakeApplication { OnCall = _ => "<ok/>" };
        using var gateway = Gateway(app, Settings());

        await gateway.GetHierarchyAsync("{n}", HierarchyScope.Pages, Ct);

        Assert.Equal(["{n}", Com.Interop.HierarchyScope.hsPages, XMLSchema.xs2013], app.Calls.Single().Args);
    }

    [Theory]
    [InlineData(PageInfo.Basic, 0)]
    [InlineData(PageInfo.BinaryData, 1)]
    [InlineData(PageInfo.Selection, 2)]
    [InlineData(PageInfo.BinaryDataSelection, 3)]
    [InlineData(PageInfo.FileType, 4)]
    [InlineData(PageInfo.BinaryDataFileType, 5)]
    [InlineData(PageInfo.SelectionFileType, 6)]
    [InlineData(PageInfo.All, 7)]
    public void MapPageInfo_MatchesTypeLibraryValues(PageInfo pageInfo, int expected) =>
        Assert.Equal(expected, (int)ComGateway.MapPageInfo(pageInfo));

    [Theory]
    [InlineData(SpecialLocation.BackUpFolder, 0)]
    [InlineData(SpecialLocation.UnfiledNotesSection, 1)]
    [InlineData(SpecialLocation.DefaultNotebookFolder, 2)]
    public void MapSpecialLocation_MatchesTypeLibraryValues(SpecialLocation location, int expected) =>
        Assert.Equal(expected, (int)ComGateway.MapSpecialLocation(location));

    // --- Worker -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StaThread_WorkQueuedAfterDispose_FailsInsteadOfHanging()
    {
        var sta = new StaThread("test-sta");
        sta.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => sta.InvokeAsync(() => 1, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct));
        await sta.Exited.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task StaThread_Poison_FailsQueuedWork_AndExitsWhenTheRunningItemReturns()
    {
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var sta = new StaThread("test-sta");
        var running = sta.InvokeAsync(
            () =>
            {
                entered.Set();
                gate.Wait();
                return 1;
            },
            Ct);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), Ct));
        var queued = sta.InvokeAsync(() => 2, Ct);

        sta.Poison(() => new OneNoteException(ErrorCode.ComBusy, "poisoned"));

        Assert.Equal(ErrorCode.ComBusy, (await Assert.ThrowsAsync<OneNoteException>(() => queued)).Code);
        Assert.Equal(ErrorCode.ComBusy, (await Assert.ThrowsAsync<OneNoteException>(() => sta.InvokeAsync(() => 3, Ct))).Code);
        Assert.False(sta.Exited.IsCompleted);

        gate.Set();
        Assert.Equal(1, await running);
        await sta.Exited.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    // --- Helpers ------------------------------------------------------------------------------------------------

    private static ComGateway Gateway(FakeApplication app, GatewaySettings settings) =>
        new(new TestProbe(), () => settings, app.Factory);

    private static GatewaySettings Settings(int callMs = 5000, int activationMs = 5000, int idleMs = 60_000, bool silent = false) =>
        new(
            new Timeouts(
                TimeSpan.FromMilliseconds(callMs),
                TimeSpan.FromMilliseconds(callMs),
                TimeSpan.FromMilliseconds(activationMs),
                TimeSpan.FromMilliseconds(idleMs)),
            silent);

    private static Exception Trigger(string trigger) => trigger switch
    {
        "0x800706BA" => new COMException("gone", RpcServerUnavailable),
        "0x80010108" => new COMException("disconnected", RpcDisconnected),
        _ => new InvalidComObjectException("dead RCW"),
    };

    private static async Task WaitUntil(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "Condition not met within 5 s.");
            await Task.Delay(20, Ct);
        }
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class TestProbe : IProcessProbe
    {
        private int _integrityChecks;
        private volatile bool _running = true;

        public bool Running
        {
            get => _running;
            set => _running = value;
        }

        public IntegrityReport Integrity { get; set; } = new(0x2000, [new OneNoteIntegrity(1, 0x2000)]);

        public int IntegrityChecks => Volatile.Read(ref _integrityChecks);

        public bool IsOneNoteRunning() => Running;

        public IntegrityReport CheckIntegrity()
        {
            Interlocked.Increment(ref _integrityChecks);
            return Integrity;
        }
    }
}
