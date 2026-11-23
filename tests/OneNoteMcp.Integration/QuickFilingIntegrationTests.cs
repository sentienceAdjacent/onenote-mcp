using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using Xunit;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 4.9's live sitting, steps 3 and 4 (4.9 plan OQ3 a): <c>quick_filing</c>'s use case through the <c>AppState</c> pipeline
/// and the real <see cref="ComGateway"/>, with a 20 s wait instead of 300 s. Opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c>, and
/// it needs Phill at the screen: it opens a picker whose description says what to do. While the picker is open it reads the
/// notebooks and MCP Test's seed page through the same gateway (spike 4.13 F4). The short wait then ends with
/// <see cref="ToolWarnings.PickerLeftOpen"/>, the picker stays on screen, and the gateway lets go of it, so the idle release
/// (5 s) drops the COM reference. Phill then picks a section in that old picker: the answer must arrive with no error or hang
/// and be dropped (logged as <c>lateAnswer</c>), and a final read must pass (spine Deferred: the last reference dropped with a
/// picker on screen). Nothing is written. Settings go to a temporary <c>settings.json</c>, never the user's. It prints IDs,
/// codes and timings only.
/// </summary>
public sealed class QuickFilingIntegrationTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The sitting's short wait (OQ3 a).</summary>
    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(20);

    /// <summary>How long the test waits for Phill's pick in the old picker, after the idle release.</summary>
    private static readonly TimeSpan PickWait = TimeSpan.FromMinutes(3);

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: only a temporary settings file.
        }
    }

    [Fact]
    public async Task QuickFiling_ReadsGoThroughWhileThePickerIsOpen_TheShortWaitEndsLeftOpen_AndALatePickAfterIdleReleaseIsHarmless()
    {
        using var gateway = Start();
        var notebook = await TestNotebook.EnsureAsync(gateway, Ct);
        var logs = new List<QuickFilingLogEntry>();
        var useCase = new QuickFilingUseCase(gateway, new AppStatePipeline(gateway), waitLimit: ShortWait, log: e =>
        {
            lock (logs)
            {
                logs.Add(e);
            }
        });

        // Sitting step 3: the picker, and reads while it is open.
        var call = useCase.ExecuteAsync(
            new ToolCallContext("quick-filing-sitting"),
            "MCP Test: don't choose yet",
            "Leave this open for about 30 s (until the test's wait has ended), then choose any section in MCP Test.",
            Ct);
        await WaitUntil(() => gateway.PickerHolds == 1 || call.IsCompleted, TimeSpan.FromSeconds(30));
        Assert.False(call.IsCompleted, "quick_filing ended before its picker was held.");

        var notebooks = await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct);
        var page = await gateway.GetPageContentAsync(notebook.PageId, PageInfo.Basic, Ct);
        Assert.False(string.IsNullOrEmpty(notebooks));
        Assert.False(string.IsNullOrEmpty(page));
        Assert.False(call.IsCompleted, "The picker was answered before the reads finished; rerun and don't choose yet.");
        output.WriteLine("reads with the picker open: ok");

        var result = await call;
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(ToolWarnings.PickerLeftOpen, warning.Code);
        Assert.Null(result.Meta.ObjectId);
        Assert.Equal(0, gateway.PickerHolds);
        output.WriteLine($"wait ended after {ShortWait.TotalSeconds:0} s: {warning.Code}");

        // Sitting step 4: the idle release drops the reference with the old picker still on screen; then Phill picks in it.
        await WaitUntil(() => !gateway.HasApplication, TimeSpan.FromSeconds(30));
        output.WriteLine("idle release with the picker on screen: done");
        await WaitUntil(
            () =>
            {
                lock (logs)
                {
                    return logs.Exists(l => l.Outcome == "lateAnswer");
                }
            },
            PickWait);
        QuickFilingLogEntry late;
        lock (logs)
        {
            late = logs.Single(l => l.Outcome == "lateAnswer");
        }

        output.WriteLine($"late answer: {(late.ChosenId is null ? "nothing chosen" : late.ChosenId)}, dropped");

        // The final read: OneNote still answers through the same gateway (a fresh activation).
        Assert.False(string.IsNullOrEmpty(await gateway.GetHierarchyAsync(notebook.NotebookId, CoreScope.Self, Ct)));
        output.WriteLine("final read: ok");
    }

    private ComGateway Start()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");
        File.WriteAllText(settingsPath, """{ "schemaVersion": 1 }""");
        return new ComGateway(new ProcessProbe(), new SettingsStore(settingsPath, Path.Combine(tempDir, "journal")));
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan limit)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < limit, $"Condition not met within {limit.TotalSeconds:0} s.");
            await Task.Delay(100, Ct);
        }
    }
}
