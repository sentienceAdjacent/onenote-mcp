using OneNoteMcp.Com.Interop;
using Xunit;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Spike 3.1, scenario <c>write-probes-r2-timeout</c> (Q4 a: its own scenario, last in sitting A). X3: a Word export of a
/// 30-page section with a 1 s call limit, so the call times out mid-export. The call runs through a second gateway
/// (<see cref="ProbeSession.CreateSideGateway"/>) after the guard's reads ran through the session's own, at 60 s; its
/// timeout poisons only that gateway. The publish folder is then watched for 120 s (does a partial file appear and
/// grow?), and step <c>timeout-retry</c> publishes to the same path at 60 s (<c>FileExists</c>?).
/// </summary>
public sealed class Round2TimeoutProbe(ITestOutputHelper output)
{
    public const string ScenarioName = "write-probes-r2-timeout";

    /// <summary>X3's call limit for the export.</summary>
    public const int ExportCallSeconds = 1;

    private const int Pages = 30;

    [Fact]
    public async Task WriteProbesR2Timeout()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.ProbeCallSeconds);
        var t = new ProbeTarget("local", await s.SetupAsync("setup", s.McpTestIdAsync), TestNotebook.NotebookName, Cloud: false);
        var stamp = HierarchyProbes.Stamp();
        var run = await s.SetupAsync("run-group", () => HierarchyProbes.RunGroupAsync(s, t, stamp));
        var c = new Round2Context(s, t, run, stamp);
        string? section = null;
        PublishLanding? export = null;

        await s.StepAsync("timeout-setup", async () =>
        {
            (section, _) = await Round2.SectionAsync(c, run, $"probe timeout {stamp}", Enumerable.Range(1, Pages).Select(i => $"probe timeout p{i} {stamp}").ToArray());
            s.Recorder.Row("x3-local-setup", "Export", "-", null, null, new { pages = await Round2.PageCountAsync(s, section) });
        });

        await s.StepAsync("timeout-export", async () =>
        {
            if (section is null)
            {
                s.Recorder.Row("x3-local-export", "Export", "Publish(pfWord)", null, null, new { notProbed = "no section: step timeout-setup didn't run or failed" });
                return;
            }

            var side = s.CreateSideGateway(ExportCallSeconds);
            export = await s.PublishFreshAsync($"x3-local-{stamp}", section, PublishFormat.pfWord, "probe-x3.docx", callGateway: side, stopOnTimeout: false, poll: TimeSpan.FromSeconds(ProbeSession.LandingPollSeconds));
            s.Recorder.Row("x3-local-export", "Export", "Publish(pfWord)", new { pages = Pages, callSeconds = ExportCallSeconds }, export.Attempt, new
            {
                timedOut = export.Attempt.Code == nameof(OneNoteMcp.Core.ErrorCode.Timeout),
                appearedAfterTimeout = export.Landing.FirstSeenMs is > 0, // not already there at the first poll
                grewAfterTimeout = TargetGrew(export.Landing.Changes),
                export.Summary,
            });
        });

        await s.StepAsync("timeout-retry", async () =>
        {
            if (section is null || export is null)
            {
                s.Recorder.Row("x3-local-retry", "Export", "Publish(pfWord)", null, null, new { notProbed = "step timeout-export didn't run (include it in ONENOTE_MCP_PROBE_STEPS)" });
                return;
            }

            var before = ProbeSession.Disk(export.Path);
            var retry = await s.PublishOneAsync(section, export.Path, PublishFormat.pfWord);
            s.Recorder.Row("x3-local-retry", "Export", "Publish(pfWord)", new { target = "the same path as the timed-out export", callSeconds = ProbeSession.ProbeCallSeconds }, retry, new
            {
                diskBefore = before,
                diskAfter = ProbeSession.Disk(export.Path),
                folder = ProbeSession.Snapshot(Path.GetDirectoryName(export.Path)!),
            });
        });

        s.Finish();
    }

    /// <summary>True only when the target file's own size increased between two successive polls (other files don't count).</summary>
    internal static bool TargetGrew(IReadOnlyList<LandingChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var sizes = changes.Select(ch => ch.TargetBytes).OfType<long>().ToList();
        return sizes.Zip(sizes.Skip(1)).Any(pair => pair.Second > pair.First);
    }
}
