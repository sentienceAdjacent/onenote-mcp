using OneNoteMcp.Com.Interop;
using Xunit;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Story 5.1, scenario <c>write-probes-cloud-jam-repro</c>: finds which call pattern stalled MCP Test Cloud's sync
/// (0xE0000644 9zu13) in run write-probes-hierarchy-cloud-20261111-165147, where the section groups reached OneDrive
/// but none of the sections did. One variable per step, and Phill checks View Sync Status after each before the next
/// runs; the first step after which the notebook stops syncing is the cause. Run it with ONENOTE_MCP_PROBE_STEPS to
/// take one step at a time (r1-with-page, r1-empty, r2-same-name, r3-no-extension, r4-burst). Everything is created inside a fresh
/// "probe run &lt;stamp&gt;" group on MCP Test Cloud, through the same guard as the other probes.
/// </summary>
public sealed class CloudJamRepro(ITestOutputHelper output)
{
    public const string ScenarioName = "write-probes-cloud-jam-repro";

    [Fact]
    public async Task WriteProbesCloudJamRepro()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.CloudProbeCallSeconds);
        var t = new ProbeTarget("cloud", await s.SetupAsync("setup", s.CloudIdAsync), TestNotebook.CloudNotebookName, Cloud: true);
        var stamp = HierarchyProbes.Stamp();
        var run = await s.SetupAsync("run-group", () => HierarchyProbes.RunGroupAsync(s, t, stamp));
        var where = $"{TestNotebook.CloudNotebookName} > probe run {stamp}";

        // R1a: one section, with a page added at once (a section made in OneNote's UI always starts with a page).
        await s.StepAsync("r1-with-page", async () =>
        {
            var (a, id) = await s.CreateChildAsync(run, $"probe r1a {stamp}.one", CreateFileType.cftSection);
            var page = id is null ? null : await s.CreateProbePageAsync(id, $"probe r1a page {stamp}");
            s.Recorder.Row("r1-with-page", "Cloud jam", "OpenHierarchy(cftSection) + CreateNewPage", new { relativeName = "probe r1a <stamp>.one" }, a, new { id, page });
            await CheckSyncAsync(s, "r1a", $"the section \"probe r1a {stamp}\" (with one page) in {where}");
        });

        // R1b: one section, left empty. Every section in Misplaced Sections after the jam (s1, s2, s3, nested) had no
        // pages, so an empty section that OneDrive won't take is a candidate in its own right.
        await s.StepAsync("r1-empty", async () =>
        {
            var (a, id) = await s.CreateChildAsync(run, $"probe r1b {stamp}.one", CreateFileType.cftSection);
            s.Recorder.Row("r1-empty", "Cloud jam", "OpenHierarchy(cftSection)", new { relativeName = "probe r1b <stamp>.one", pages = 0 }, a, new { id });
            await CheckSyncAsync(s, "r1b", $"the empty section \"probe r1b {stamp}\" in {where}");
        });

        // R2: a section, then the same name again 2 s later, while the first may still be uploading. It gets a page,
        // so only the second create differs from R1a.
        await s.StepAsync("r2-same-name", async () =>
        {
            var (a, id) = await s.CreateChildAsync(run, $"probe r2 {stamp}.one", CreateFileType.cftSection);
            if (id is not null)
            {
                await s.CreateProbePageAsync(id, $"probe r2 page {stamp}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ProbeSession.Ct);
            var (again, sameId) = await s.CreateChildAsync(run, $"probe r2 {stamp}.one", CreateFileType.cftSection);
            s.Recorder.Row("r2-same-name", "Cloud jam", "OpenHierarchy(cftSection) x2", new { relativeName = "probe r2 <stamp>.one, twice, 2 s apart" }, again, new
            {
                first = a,
                id,
                sameId = sameId is not null && sameId == id,
            });
            await CheckSyncAsync(s, "r2", $"the section \"probe r2 {stamp}\" in {where}");
        });

        // R3: a section name without ".one" (OneNote answered hrSectionDoesNotExist in the jammed run).
        await s.StepAsync("r3-no-extension", async () =>
        {
            var (a, id) = await s.CreateChildAsync(run, $"probe r3 {stamp}", CreateFileType.cftSection);
            s.Recorder.Row("r3-no-extension", "Cloud jam", "OpenHierarchy(cftSection)", new { relativeName = "probe r3 <stamp> (no .one)" }, a, new { id });
            await CheckSyncAsync(s, "r3", $"anything named \"probe r3 {stamp}\" in {where}");
        });

        // R4: the jammed run's burst, replayed back to back with no pauses: the same creates in the same order and
        // nesting (run group > section, same name again, no extension, a nested group with a section, then a second
        // group holding three sections and a group, and pages in the first section). R1-R3 each passed alone, so what
        // is left is pace and depth: in run 165147 the creates slowed from 5 s to 8 s and 16 s, then one hung.
        await s.StepAsync("r4-burst", async () =>
        {
            var calls = new List<object>();
            async Task<string?> Create(string parent, string name, CreateFileType type, string label)
            {
                var (a, id) = await s.CreateChildAsync(parent, name, type);
                calls.Add(new { label, a.Outcome, a.HResultName, a.Ms });
                return id;
            }

            await Create(run, $"probe sec {stamp}.one", CreateFileType.cftSection, "sec");
            await Create(run, $"probe sec {stamp}.one", CreateFileType.cftSection, "sec again");
            await Create(run, $"probe noext {stamp}", CreateFileType.cftSection, "noext");
            var sub = await Create(run, $"probe sub {stamp}", CreateFileType.cftFolder, "sub");
            if (sub is not null)
            {
                await Create(sub, $"probe nested {stamp}.one", CreateFileType.cftSection, "nested");
            }

            var upd = await Create(run, $"probe upd {stamp}", CreateFileType.cftFolder, "upd");
            if (upd is not null)
            {
                var s1 = await Create(upd, $"probe s1 {stamp}.one", CreateFileType.cftSection, "s1");
                await Create(upd, $"probe s2 {stamp}.one", CreateFileType.cftSection, "s2");
                await Create(upd, $"probe s3 {stamp}.one", CreateFileType.cftSection, "s3");
                await Create(upd, $"probe upd sub {stamp}", CreateFileType.cftFolder, "upd sub");
                if (s1 is not null)
                {
                    foreach (var n in new[] { "p1", "p2", "p3" })
                    {
                        var page = await s.CreateProbePageAsync(s1, $"probe {n} {stamp}");
                        calls.Add(new { label = n, page = page is not null });
                    }
                }
            }

            s.Recorder.Row("r4-burst", "Cloud jam", "OpenHierarchy x10 + CreateNewPage x3, back to back", new { shape = "run 165147's creates, same order and nesting" }, null, new { calls });
            await CheckSyncAsync(s, "r4", $"the groups \"probe sub {stamp}\" and \"probe upd {stamp}\" and their sections in {where}");
        });

        s.Finish();
    }

    public const string TimeoutScenarioName = "write-probes-cloud-jam-timeout";

    /// <summary>
    /// R5, scenario <c>write-probes-cloud-jam-timeout</c>: the one difference left after R1-R4 all synced. In run 165147
    /// the s3 create hit the call limit, the gateway abandoned the call (and its STA thread) while OneNote finished it
    /// the same second, and the next run's first create hung. Here the run group's create gets a 2 s limit, so it times
    /// out mid-create; the session then trips its breaker and stops, as it did then. Phill checks sync afterwards, and a
    /// fresh R1a run shows whether the next create goes through.
    /// </summary>
    [Fact]
    public async Task WriteProbesCloudJamTimeout()
    {
        ProbeSession.RequireScenario(TimeoutScenarioName);
        using var s = new ProbeSession(TimeoutScenarioName, output, ProbeSession.TimeoutReproCallSeconds);
        var t = new ProbeTarget("cloud", await s.SetupAsync("setup", s.CloudIdAsync), TestNotebook.CloudNotebookName, Cloud: true);
        var stamp = HierarchyProbes.Stamp();

        // Expected to time out and stop the session (ProbeStoppedException after the timeout pause).
        await s.SetupAsync("r5-timeout", () => HierarchyProbes.RunGroupAsync(s, t, stamp));
        s.Recorder.Row("r5-timeout", "Cloud jam", "OpenHierarchy(cftFolder), 2 s limit", new { relativeName = "probe run <stamp>" }, null, new
        {
            note = "the create finished inside 2 s, so nothing timed out; R5 proved nothing",
        });
        s.Finish();
    }

    internal static Task<bool> CheckSyncAsync(ProbeSession s, string step, string what) => s.PauseAsync(
        $"{step}-check-sync",
        $"In OneNote open File > Info > View Sync Status, press Sync Now on MCP Test Cloud (Phill, 2026-11-11: force a sync between steps), "
        + $"wait about 30 seconds and expand MCP Test Cloud. Tell the team lead whether it says \"Up to date\" or shows an error (with its code), "
        + $"and whether {what} shows in OneNote for the web. Then continue.");
}
