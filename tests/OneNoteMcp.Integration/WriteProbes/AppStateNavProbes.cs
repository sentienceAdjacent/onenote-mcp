using System.Diagnostics;
using System.Xml.Linq;
using OneNoteMcp.Com;
using OneNoteMcp.Com.Interop;
using Xunit;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Spike 4.12, scenario <c>app-state-nav</c> (one sitting, every step hitl): <c>NavigateToUrl</c> with each kind of link
/// (N1–N6), the <c>0x80042014</c> repeat window (R1), landing series and checks (K1, K2), and the <c>Windows</c> and
/// <c>Window</c> reads (W1–W6), in the Sitting script's order. It builds on spike 4.1's harness: the run folder
/// <c>%LOCALAPPDATA%\OneNoteMcp-Test\probes\app-state\&lt;stamp&gt;</c> holds the run's one notebook, <c>n1</c> (N4).
/// <para>
/// Every navigation goes through <see cref="TestNotebook"/>'s guard (MCP Test, MCP Test Cloud and the run's notebooks
/// for IDs; for URLs, only the exact strings this run produced), always with <c>fNewWindow=false</c>. Phill is present:
/// each navigation has a pause before it (what OneNote will show) and one after (what he saw); R1's pairs share one of
/// each, since nothing may fall between the two calls of a pair. Cloud calls run through a second gateway with the
/// 180 s limit, each followed by the settle, and the cloud step by a Sync Now check.
/// </para>
/// <para>
/// Rerun steps with <c>ONENOTE_MCP_PROBE_STEPS</c> and <c>ONENOTE_MCP_PROBE_STAMP=&lt;stamp&gt;</c>, as in 4.1;
/// <c>cleanup</c> is its own step.
/// </para>
/// </summary>
public sealed class AppStateNavProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "app-state-nav";

    [Fact]
    public async Task AppStateNav()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.ProbeCallSeconds);
        var c = AppState.Begin(s, cloud: false, runTags: [NavContext.RunTag]);
        await s.SetupAsync("setup", () => AppState.AdoptAsync(c));
        var n = new NavContext(c);
        s.Recorder.Row("setup-relied-on", "Relied on", "-", null, null, NavProbe.ReliedOn);

        await s.StepAsync("windows-read", () => WindowsReadAsync(n));
        await s.StepAsync("links-local", () => LinksAsync(n, cloud: false));
        await s.StepAsync("missing", () => MissingAsync(n, cloud: false));
        await s.StepAsync("repeat-local", () => RepeatAsync(n, cloud: false));
        await s.StepAsync("cloud", () => CloudAsync(n));
        await s.StepAsync("closed-notebook", () => ClosedNotebookAsync(n));
        await s.StepAsync("loose-file", () => LooseFileAsync(n));
        await s.StepAsync("vault", () => VaultAsync(n));
        await s.StepAsync("quick-note", () => QuickNoteAsync(n));
        await s.StepAsync("two-windows", () => TwoWindowsAsync(n));
        await s.StepAsync("cleanup", () => CleanupAsync(n));
        await s.StepAsync("no-window", () => NoWindowAsync(n));

        s.Finish();
    }

    // --- W1, W2: reading the windows, OneNote in the background, then in front -------------------------------------

    private static async Task WindowsReadAsync(NavContext n)
    {
        var s = n.S;
        await s.ObservePauseAsync(
            "windows-read-background",
            "nothing. The probe reads OneNote's list of windows three times and navigates nowhere. Leave this window in front, not OneNote.");
        await WindowsReadsAsync(n, "background");

        await s.PauseAsync(
            "windows-read-front",
            "Tell the team lead whether anything changed in OneNote during those reads (did it come to the front, did the page shown or the selection change?). "
            + $"Then continue, and straight after, within {NavProbe.FrontDelaySeconds} seconds, click OneNote's title bar to bring it to the front (don't click inside the page). "
            + $"The probe waits {NavProbe.FrontDelaySeconds} seconds, then reads the windows three more times.");
        await Task.Delay(TimeSpan.FromSeconds(NavProbe.FrontDelaySeconds), ProbeSession.Ct);
        await WindowsReadsAsync(n, "front");

        await s.ReportPauseAsync("windows-read", "whether anything changed in OneNote during the last three reads (the page shown, the selection, the window's position or size, or the focus).");
    }

    private static async Task WindowsReadsAsync(NavContext n, string oneNote)
    {
        var s = n.S;
        for (var i = 1; i <= 3; i++)
        {
            var before = await s.ViewAsync();
            var windows = await s.WindowsSnapshotAsync();
            var after = await s.ViewAsync();
            if (oneNote == "background" && i == 1)
            {
                s.Recorder.Row("w1", "W1", "Windows(read)", new { oneNote }, null, new { summary = NavProbe.Summarize(windows), windows });
            }

            s.Recorder.Row($"w2-{oneNote}-{i}", "W2", "Windows(read)", new { oneNote, read = i }, null, new
            {
                fv = new { before, after },
                focusChanged = before.Window != after.Window || before.Process != after.Process,
                viewChanged = before.Notebook != after.Notebook || before.Section != after.Section || before.Page != after.Page,
                summary = NavProbe.Summarize(windows),
                windows,
            });
        }
    }

    // --- N1 (L or C), K1, K2 --------------------------------------------------------------------------------------

    private static async Task LinksAsync(NavContext n, bool cloud)
    {
        var s = n.S;
        var where = NavProbe.Where(cloud);
        var gateway = n.Gateway(cloud);
        if (await n.TargetsAsync(cloud) is not { } t)
        {
            AppState.Missing(n.C, $"n1-{where}", "N1", $"{n.NotebookName(cloud)} has no two pages to navigate between", "setup");
            return;
        }

        var paragraph = await NavProbe.ParagraphIdAsync(s, t.B.PageId, gateway);
        var kinds = new (string Kind, string Id, string ObjectId, NavExpect Expect, string Shows)[]
        {
            ("page", t.B.PageId, string.Empty, new(t.NotebookId, t.B.SectionId, t.B.PageId), $"the page {t.Path(t.B)}"),
            ("section", t.A.SectionId, string.Empty, new(t.NotebookId, t.A.SectionId, null), $"the section {t.NotebookName} > {t.A.SectionName}"),
            ("notebook", t.NotebookId, string.Empty, new(t.NotebookId, null, null), $"the notebook {t.NotebookName}"),
            ("paragraph", t.B.PageId, paragraph ?? string.Empty, new(t.NotebookId, t.B.SectionId, t.B.PageId), $"the page {t.Path(t.B)}, with one paragraph selected"),
        };

        foreach (var (kind, id, objectId, expect, shows) in kinds)
        {
            var probe = $"n1-{where}-{kind}";
            if (kind == "paragraph" && paragraph is null)
            {
                AppState.Missing(n.C, probe, "N1", $"the page {t.B.PageName} has no paragraph with an objectID", "links");
                continue;
            }

            var (linked, link) = await s.LinkAsync(id, objectId, gateway);
            if (link is null)
            {
                s.Recorder.Row($"{probe}-link", "N1", "GetHyperlinkToObject", new { kind, where }, linked, null);
                continue;
            }

            if (kind == "page")
            {
                n.Links[(cloud, t.B.PageId)] = link;
            }

            var shape = LinkShape.Parse(link);
            await s.ObservePauseAsync(probe, $"OneNote comes to the front and shows {shows}.");
            await NavRowAsync(n, probe, "N1", cloud, "NavigateToUrl", new { kind, where, link = shape }, g => s.NavigateToUrlAsync(link, g), expect, shape.PageGuid);
            await s.ReportPauseAsync(probe, $"whether OneNote came to the front and shows {shows}{(kind == "paragraph" ? ", and whether a paragraph is selected or highlighted" : string.Empty)}.");
        }
    }

    // --- N3 (L or C), K2 ------------------------------------------------------------------------------------------

    private static async Task MissingAsync(NavContext n, bool cloud)
    {
        var s = n.S;
        var where = NavProbe.Where(cloud);
        var probe = $"n3-{where}";
        if (await n.TargetsAsync(cloud) is not { } t)
        {
            AppState.Missing(n.C, probe, "N3", $"{n.NotebookName(cloud)} has no two pages to navigate between", "setup");
            return;
        }

        if (await n.LinkForAsync(cloud, t.A.PageId) is not { } link)
        {
            AppState.Missing(n.C, probe, "N3", $"no link to {t.A.PageName}", "links");
            return;
        }

        var missing = s.MissingPageLink(link);
        var shape = LinkShape.Parse(missing);
        await s.ObservePauseAsync(
            probe,
            $"OneNote comes to the front and follows a link to a page that doesn't exist, in the section {t.NotebookName} > {t.A.SectionName}. It may show that section, stay where it is, or show a message.");
        await NavRowAsync(
            n,
            probe,
            "N3",
            cloud,
            "NavigateToUrl",
            new { where, link = shape, madeFrom = $"the page link of {t.A.PageName}, its page-id replaced by a fresh GUID" },
            g => s.NavigateToUrlAsync(missing, g),
            new NavExpect(t.NotebookId, t.A.SectionId, null),
            shape.PageGuid);
        await s.ReportPauseAsync(probe, "what OneNote shows now (which section and page), and any message or dialog.");
    }

    // --- R1 (L or C), K1 ------------------------------------------------------------------------------------------

    private static async Task RepeatAsync(NavContext n, bool cloud)
    {
        var s = n.S;
        var where = NavProbe.Where(cloud);
        var gateway = n.Gateway(cloud);
        if (await n.TargetsAsync(cloud) is not { } t)
        {
            AppState.Missing(n.C, $"r1-{where}", "R1", $"{n.NotebookName(cloud)} has no two pages to navigate between", "setup");
            return;
        }

        var linkA = await n.LinkForAsync(cloud, t.A.PageId);
        var linkB = await n.LinkForAsync(cloud, t.B.PageId);
        var pairs = NavProbe.RepeatDelaysSeconds.Count * 2;
        await s.ObservePauseAsync(
            $"r1-{where}",
            $"OneNote comes to the front and shows {t.Path(t.B)}; a few seconds later it jumps between {t.Path(t.A)} and {t.Path(t.B)} {pairs * 2} times: {pairs} pairs of jumps, the two jumps of a pair 0 to 5 seconds apart, "
            + $"and at least {(cloud ? ProbeSession.SettleSeconds : NavProbe.PairGapSeconds)} seconds between pairs. Some jumps may not happen. Watch only, and don't click.");

        // Positioning: one jump to B first, so the first pair's first call (to A) really moves the window (N3 may have
        // left it on A). Then the series' gap, as between pairs.
        var positioned = await s.NavigateToAsync(t.B.PageId, gateway);
        Stopwatch? sincePair = Stopwatch.StartNew();
        s.Recorder.Row($"r1-{where}-position", "R1", "NavigateTo", new { where, target = "page B", purpose = "positioning before the series; not a measured pair" }, positioned, null);
        if (cloud)
        {
            await s.SettleAsync($"r1-{where}-position", "R1");
        }

        foreach (var url in new[] { false, true })
        {
            var method = url ? "NavigateToUrl" : "NavigateTo";
            var (first, second) = url ? (linkA, linkB) : (t.A.PageId, t.B.PageId);
            if (first is null || second is null)
            {
                AppState.Missing(n.C, $"r1-{where}-{method}", "R1", "a page link is missing", "links");
                continue;
            }

            foreach (var delay in NavProbe.RepeatDelaysSeconds)
            {
                var probe = $"r1-{where}-{method}-{delay}s";

                // Both guards before the pair (reads), so nothing reads between its two calls.
                var (firstChecked, firstCheck) = await s.CheckNavigationAsync(first, url, gateway);
                var (secondChecked, secondCheck) = await s.CheckNavigationAsync(second, url, gateway);
                if (firstCheck is null || secondCheck is null)
                {
                    s.Recorder.Row(probe, "R1", method, new { delaySeconds = delay, where }, firstCheck is null ? firstChecked : secondChecked, new { guard = "refused or failed before the pair" });
                    continue;
                }

                var before = await s.ViewAsync();
                long? msSincePair = null;
                if (sincePair is not null)
                {
                    var gap = TimeSpan.FromSeconds(NavProbe.PairGapSeconds) - sincePair.Elapsed;
                    if (gap > TimeSpan.Zero)
                    {
                        await Task.Delay(gap, ProbeSession.Ct);
                    }

                    msSincePair = sincePair.ElapsedMilliseconds;
                }

                var firstCall = await s.NavigateCheckedAsync(firstCheck, gateway);
                var between = Stopwatch.StartNew();
                if (delay > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), ProbeSession.Ct);
                }

                var sentAfterMs = between.ElapsedMilliseconds;
                var secondCall = await s.NavigateCheckedAsync(secondCheck, gateway);
                sincePair = Stopwatch.StartNew();

                var landing = await s.LandingSeriesAsync(cloud, gateway);
                var k2 = await K2Async(n, landing, new NavExpect(t.NotebookId, t.B.SectionId, t.B.PageId), url ? LinkShape.Parse(linkB).PageGuid : null, gateway);
                var after = await s.ViewAsync();
                if (!firstCall.Ok)
                {
                    s.Recorder.Row($"{probe}-first", "R1", method, new { delaySeconds = delay, where, call = "first" }, firstCall, null);
                }

                s.Recorder.Row(probe, "R1", method, new { delaySeconds = delay, where, call = "second", targets = "first to A, second to B" }, secondCall, new
                {
                    first = firstCall,
                    second = secondCall,
                    secondSentAfterMs = sentAfterMs,
                    msSincePreviousPair = msSincePair,
                    landing,
                    k2,
                    fv = new { before, after },
                });
                if (cloud)
                {
                    await s.SettleAsync(probe, "R1");
                }
            }
        }

        await s.ReportPauseAsync($"r1-{where}", "anything you noticed: a jump that didn't happen, a message or dialog, or OneNote not responding.");
    }

    // --- N1 C, N2, N3 C, R1 C, then the Sync Now check --------------------------------------------------------------

    private static async Task CloudAsync(NavContext n)
    {
        var s = n.S;
        await LinksAsync(n, cloud: true);
        await WebLinkAsync(n);
        await MissingAsync(n, cloud: true);
        await RepeatAsync(n, cloud: true);
        await AppState.CheckSyncAsync(s, "cloud", $"{TestNotebook.CloudNotebookName}'s pages");
    }

    private static async Task WebLinkAsync(NavContext n)
    {
        var s = n.S;
        const string Probe = "n2-web-link";
        var gateway = n.Gateway(cloud: true);
        if (await n.TargetsAsync(cloud: true) is not { } t)
        {
            AppState.Missing(n.C, Probe, "N2", $"{TestNotebook.CloudNotebookName} has no two pages to navigate between", "setup");
            return;
        }

        var (linked, link) = await s.WebLinkAsync(t.A.PageId, gateway);
        if (link is null)
        {
            s.Recorder.Row($"{Probe}-link", "N2", "GetWebHyperlinkToObject", null, linked, null);
            return;
        }

        var shape = LinkShape.Parse(link);
        await s.ObservePauseAsync(
            Probe,
            $"OneNote may come to the front and show {t.Path(t.A)}, a security dialog may appear, or a browser may open a OneDrive page. "
            + "If a dialog appears, note its title and text and press its safe choice (Cancel or No). Close any browser tab it opened.");
        await NavRowAsync(n, Probe, "N2", true, "NavigateToUrl", new { link = shape, kind = "GetWebHyperlinkToObject, a page" }, g => s.NavigateToUrlAsync(link, g), new NavExpect(t.NotebookId, t.A.SectionId, t.A.PageId), shape.PageGuid);
        await s.ReportPauseAsync(Probe, "whether a dialog appeared (its title and text, and which button you pressed), whether a browser opened (and that you closed it), and what OneNote shows.");
    }

    // --- N4: a link into a closed run notebook ----------------------------------------------------------------------

    private static async Task ClosedNotebookAsync(NavContext n)
    {
        var s = n.S;
        var c = n.C;
        var name = c.RunName(NavContext.RunTag);
        await s.ObservePauseAsync("closed-notebook-make", $"a new notebook \"{name}\" appears in the notebook list (making it may bring OneNote to the front), then disappears again.");

        var (id, sectionId, pageId) = await MakeN1Async(n);
        if (id is null || sectionId is null || pageId is null)
        {
            return;
        }

        await PublishLooseAsync(n, sectionId); // N5's file, published while n1 is open; nothing opens it before N5

        var (linked, link) = await s.LinkAsync(pageId);
        s.Recorder.Row("n4-link", "N4", "GetHyperlinkToObject", new { target = "n1's probe page" }, linked, new { link = link is null ? null : LinkShape.Parse(link) });
        var info = await s.NotebookInfoAsync(id);
        var close = await AppState.CloseRowAsync(c, "n4-close-n1", "N4", id, args: new { notebook = NavContext.RunTag });
        if (link is null || !close.Closed || info is null)
        {
            AppState.Missing(c, "n4", "N4", link is null ? "no link to n1's page" : "n1 didn't close", "closed-notebook");
            return;
        }

        var shape = LinkShape.Parse(link);
        var openBefore = await NavProbe.OpenNotebookIdsAsync(s);
        await s.ObservePauseAsync(
            "closed-notebook",
            $"OneNote comes to the front and follows a link to a page of the notebook {ProbeSession.Label(info.Name, info.Path)}, which is now closed. It may open that notebook again, show a message, or do nothing.");
        await NavRowAsync(
            n,
            "n4-link-closed-n1",
            "N4",
            false,
            "NavigateToUrl",
            new { link = shape, notebook = "n1, closed by CloseNotebook (force=false) just before" },
            g => s.NavigateToUrlAsync(link, g),
            null,
            shape.PageGuid,
            async () =>
            {
                var notebooks = (await s.NotebooksAsync()).Elements(One + "Notebook").Select(NodeInfo.From).ToList();
                var reopened = await c.RunNotebookAsync(NavContext.RunTag);
                return new
                {
                    appeared = notebooks.Where(x => x.Id is not null && !openBefore.Contains(x.Id)).Select(x => ProbeView.Notebook(x, c.Root)).ToList(),
                    n1Open = reopened is not null,
                    n1 = reopened is null ? null : await s.NotebookInfoAsync(reopened),
                    vsBeforeClose = reopened is null ? null : new { sameId = reopened == id },
                };
            });
        await s.ReportPauseAsync("closed-notebook", $"whether the notebook \"{name}\" came back to the notebook list, what OneNote shows, and any message or dialog.");
    }

    /// <summary>n1 with one section and one probe page, made in the run folder (or, on a rerun, found there).</summary>
    private static async Task<(string? Id, string? Section, string? Page)> MakeN1Async(NavContext n)
    {
        var s = n.S;
        var c = n.C;
        var name = c.RunName(NavContext.RunTag);
        var sectionName = $"probe n1 s {c.Stamp}";
        var pageTitle = $"probe n1 p {c.Stamp}";
        if (await c.RunNotebookAsync(NavContext.RunTag) is { } existing)
        {
            s.AllowThrowawayName(existing, name); // a rerun of this run: the guard's writes (N5's publish) accept it again
            var tree = await s.TreeAsync(existing);
            var section = tree.Descendants(One + "Section").FirstOrDefault(e => (string?)e.Attribute("name") == sectionName);
            var page = section?.Elements(One + "Page").FirstOrDefault(e => (string?)e.Attribute("name") == pageTitle);
            s.Recorder.Row("n4-make", "N4", "-", null, null, new { reused = true, section = section is not null, page = page is not null });
            return (existing, (string?)section?.Attribute("ID"), (string?)page?.Attribute("ID"));
        }

        var view = await s.ViewAsync();
        var (attempt, id) = await s.CreateThrowawayAsync(name, requireEmpty: true, c.Root);
        var after = await s.ViewAsync();
        string? sectionId = null;
        string? pageId = null;
        if (id is not null)
        {
            sectionId = await s.MustCreateChildAsync(id, sectionName + ".one", CreateFileType.cftSection);
            pageId = await s.CreateProbePageAsync(sectionId, pageTitle);
        }

        s.Recorder.Row("n4-make", "N4", "OpenHierarchy(cftNotebook)", new { path = $"<run folder>\\{name}" }, attempt, new
        {
            id,
            notebook = id is null ? null : await s.NotebookInfoAsync(id),
            fv = new { before = view, after },
        });
        return (id, sectionId, pageId);
    }

    /// <summary>N5's setup: n1's section published with <c>pfOneNote</c> to <c>probe nav &lt;stamp&gt;.one</c> in the publish folder.</summary>
    private static async Task PublishLooseAsync(NavContext n, string sectionId)
    {
        var s = n.S;
        var path = n.PublishedPath;
        Attempt? attempt = null;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(ProbeSession.PublishDir);
            attempt = await s.PublishOneAsync(sectionId, path, PublishFormat.pfOneNote);
        }

        if (File.Exists(path))
        {
            s.PublishedFiles.Add(path);
        }

        s.Recorder.Row("n5-publish", "N5", "Publish(pfOneNote)", new { file = n.PublishedFileLabel, source = "n1's probe section" }, attempt, new { alreadyThere = attempt is null, disk = ProbeSession.Disk(path) });
    }

    // --- N5: a link to a loose .one, never opened -----------------------------------------------------------------

    private static async Task LooseFileAsync(NavContext n)
    {
        var s = n.S;
        var path = n.PublishedPath;
        if (!File.Exists(path))
        {
            AppState.Missing(n.C, "n5", "N5", "the published .one isn't there", "closed-notebook");
            return;
        }

        // Its name carries this run's stamp, so on a rerun of this run it is still this run's file.
        s.PublishedFiles.Add(path);
        var link = s.LooseFileLink(path);
        var shape = LinkShape.Parse(link);
        var openBefore = await NavProbe.OpenAtPathAsync(s, path, n.C.Root);
        var notebooksBefore = await NavProbe.OpenNotebookIdsAsync(s);
        await s.ObservePauseAsync(
            "loose-file",
            $"OneNote comes to the front and follows a link to the file \"{Path.GetFileName(path)}\" in the folder {ProbeSession.PublishDir}. "
            + "It may open it as a section (perhaps under Open Sections or Quick Notes), show a message, or do nothing.");
        await NavRowAsync(
            n,
            "n5-loose-file",
            "N5",
            false,
            "NavigateToUrl",
            new
            {
                link = shape,
                encoding = "path segments percent-encoded as GetHyperlinkToObject writes them (spaces %20), drive and backslashes kept",
                rawSpaces = link.Contains(' ', StringComparison.Ordinal),
                file = n.PublishedFileLabel,
                openBefore = openBefore.Count,
            },
            g => s.NavigateToUrlAsync(link, g),
            null,
            null,
            async () => new
            {
                openAfter = await NavProbe.OpenAtPathAsync(s, path, n.C.Root),
                appeared = (await s.NotebooksAsync()).Elements(One + "Notebook").Select(NodeInfo.From).Where(x => x.Id is not null && !notebooksBefore.Contains(x.Id)).Select(x => ProbeView.Notebook(x, n.C.Root)).ToList(),
            });
        await s.PauseAsync(
            "loose-file-report",
            $"Tell the team lead whether a section opened from \"{Path.GetFileName(path)}\" and where it shows in the notebook list (under Open Sections, as a notebook of its own, or elsewhere). "
            + "If it opened, close it now: right-click its section tab > Close This Section. If you can't, say so. Don't change anything else. Then continue.");
        var stillOpen = await NavProbe.OpenAtPathAsync(s, path, n.C.Root);
        s.Recorder.Row("n5-after-close-by-hand", "N5", "GetHierarchy", null, null, new { stillOpen, closed = stillOpen.Count == 0 });
    }

    // --- N6, W5: the Ballast Vault ----------------------------------------------------------------------------------

    private static async Task VaultAsync(NavContext n)
    {
        var s = n.S;
        var mcp = await s.McpTestIdAsync();
        var vault = NavProbe.VaultSection(await s.TreeAsync(mcp, CoreScope.Sections));
        if ((string?)vault?.Attribute("ID") is not { } vaultId)
        {
            AppState.Missing(n.C, "n6", "N6", $"MCP Test has no {BallastContent.VaultSectionName}", "the Ballast scenario (story 17)");
            return;
        }

        await s.PauseAsync("vault-unlock", $"In OneNote, click the section \"{BallastContent.VaultSectionName}\" in MCP Test, unlock it with its password, and click one of its pages so that it is shown. Then continue.");

        // Hierarchy only: the Vault's page IDs, never its content.
        var pageId = (await s.TreeAsync(vaultId)).Elements(One + "Page").Where(p => (string?)p.Attribute("isInRecycleBin") != "true").Select(p => (string?)p.Attribute("ID")).FirstOrDefault();
        if (pageId is null)
        {
            s.Recorder.Row("n6-unlocked", "N6", "GetHierarchy", null, null, new { notProbed = "no Vault page is listed: the Vault isn't unlocked", locked = await NavProbe.LockedAsync(s, mcp, vaultId) });
            return;
        }

        var (linked, link) = await s.LinkAsync(pageId);
        await W5RowAsync(n, "w5-unlocked", "unlocked, a Vault page shown", mcp, vaultId, pageId, linked, link);

        await s.PauseAsync("vault-lock", $"Lock the {BallastContent.VaultSectionName} now (right-click its section tab > Lock This Section), and leave it shown. Don't click anything else. Then continue.");
        await W5RowAsync(n, "w5-locked", "locked, the same Vault page shown", mcp, vaultId, pageId, null, null);

        await s.PauseAsync("vault-leave", $"Click a page of MCP Test > {TestNotebook.SeedSectionName}, so that a page outside the Vault is shown. Don't unlock the Vault. Then continue.");
        if (link is null)
        {
            AppState.Missing(n.C, "n6", "N6", "no link to the Vault page", "vault");
            return;
        }

        var shape = LinkShape.Parse(link);
        await s.ObservePauseAsync(
            "vault",
            $"OneNote comes to the front and follows a link to a page of the locked {BallastContent.VaultSectionName}. It may ask for the password: if it does, don't enter it. Press Cancel, and tell the team lead.");
        await NavRowAsync(
            n,
            "n6-locked-vault-link",
            "N6",
            false,
            "NavigateToUrl",
            new { link = shape, vault = "locked; its link was read while it was unlocked" },
            g => s.NavigateToUrlAsync(link, g),
            new NavExpect(mcp, vaultId, pageId),
            shape.PageGuid,
            async () => new { locked = await NavProbe.LockedAsync(s, mcp, vaultId) });
        await s.ReportPauseAsync("vault", "whether a password prompt appeared (and that you pressed Cancel), and what OneNote shows now. Leave the Vault locked.");
    }

    private static async Task W5RowAsync(NavContext n, string probe, string state, string mcp, string vaultId, string pageId, Attempt? linked, string? link)
    {
        var s = n.S;
        var before = await s.ViewAsync();
        var windows = await s.WindowsSnapshotAsync();
        var after = await s.ViewAsync();
        var current = windows.Current;
        s.Recorder.Row(probe, "W5", "Windows(read)", new { vault = state }, linked, new
        {
            locked = await NavProbe.LockedAsync(s, mcp, vaultId),
            current = new
            {
                pageIsVaultPage = current?.Page == pageId,
                sectionIsVault = current?.SectionId?.Value as string == vaultId,
                page = current?.PageId,
                section = current?.SectionId,
            },
            link = link is null ? null : LinkShape.Parse(link),
            summary = NavProbe.Summarize(windows),
            windows,
            fv = new { before, after },
        });
    }

    // --- W3: a quick-note window ----------------------------------------------------------------------------------

    private static async Task QuickNoteAsync(NavContext n)
    {
        var s = n.S;
        await s.PauseAsync("quick-note-open", "Press Win+Alt+N to open a quick note (a small separate OneNote window). Don't type in it. Leave it open, then continue.");
        await WindowsRowAsync(n, "w3-quick-note-open", "W3", new { quickNote = "open" });
        await s.PauseAsync("quick-note-close", "Close the quick note window with its X (it stays empty). Tell the team lead if OneNote asked anything, and whether an empty quick note page was left in a notebook. Then continue.");
        await WindowsRowAsync(n, "w3-quick-note-closed", "W3", new { quickNote = "closed" });
    }

    private static async Task<WindowsSnapshot> WindowsRowAsync(NavContext n, string probe, string question, object args)
    {
        var s = n.S;
        var before = await s.ViewAsync();
        var windows = await s.WindowsSnapshotAsync();
        var after = await s.ViewAsync();
        s.Recorder.Row(probe, question, "Windows(read)", args, null, new { summary = NavProbe.Summarize(windows), windows, fv = new { before, after } });
        return windows;
    }

    // --- W4: two windows on one page --------------------------------------------------------------------------------

    private static async Task TwoWindowsAsync(NavContext n)
    {
        var s = n.S;
        if (await n.TargetsAsync(cloud: false) is not { } t)
        {
            AppState.Missing(n.C, "w4", "W4", "MCP Test has no two pages to navigate between", "setup");
            return;
        }

        await s.PauseAsync(
            "two-windows-open",
            $"In OneNote, click {t.Path(t.A)} so that it is shown, then press Ctrl+M to open a second OneNote window on the same page. Leave both open, and don't click in either. Then continue.");
        var start = await WindowsRowAsync(n, "w4-two-windows", "W4", new { windows = "two, on one page" });

        WindowsSnapshot? afterApp = null;
        await s.ObservePauseAsync("w4-app-navigate", $"one of the two OneNote windows comes to the front and shows {t.Path(t.B)}.");
        await NavRowAsync(
            n,
            "w4-app-navigate",
            "W4",
            false,
            "NavigateTo",
            new { target = "page B", windows = start.ListedWindows.Count },
            g => s.NavigateToAsync(t.B.PageId, g),
            new NavExpect(t.NotebookId, t.B.SectionId, t.B.PageId),
            null,
            async () =>
            {
                afterApp = await s.WindowsSnapshotAsync();
                return new { moved = NavProbe.Moves(start, afterApp), windows = afterApp };
            });
        await s.ReportPauseAsync("w4-app-navigate", "which window moved to the new page: the first window, or the second one you opened with Ctrl+M.");

        if (afterApp is null || NavProbe.SecondWindow(afterApp, t.B.PageId) is not { } second)
        {
            s.Recorder.Row("w4-window-navigate", "W4", "Window.NavigateTo", null, null, new { notProbed = "fewer than two windows were listed" });
            return;
        }

        var target = t.C.PageId != t.B.PageId ? t.C : t.A;
        var found = false;
        await s.ObservePauseAsync("w4-window-navigate", $"the OneNote window that did not move shows {t.Path(target)} instead.");
        await NavRowAsync(
            n,
            "w4-window-navigate",
            "W4",
            false,
            "Window.NavigateTo",
            new { window = new { second.Index, handle = second.HandleValue }, target = target == t.C ? "page C" : "page A" },
            async g =>
            {
                var (attempt, ok) = await s.WindowNavigateToAsync(second.HandleValue!.Value, target.PageId, g);
                found = ok;
                return attempt;
            },
            new NavExpect(t.NotebookId, target.SectionId, target.PageId),
            null,
            async () =>
            {
                var windows = await s.WindowsSnapshotAsync();
                return new { windowFound = found, moved = NavProbe.Moves(afterApp, windows), windows };
            });
        await s.ReportPauseAsync(
            "w4-window-navigate",
            "which window moved this time, and what each window shows. Then close the second window (the one you opened with Ctrl+M) with its X, so one window is left.");
    }

    // --- cleanup: n1 and the published file go ----------------------------------------------------------------------

    private static async Task CleanupAsync(NavContext n)
    {
        var s = n.S;
        var path = n.PublishedPath;
        string file;
        if (!File.Exists(path))
        {
            file = "already gone";
        }
        else
        {
            var open = await NavProbe.OpenAtPathAsync(s, path, n.C.Root);
            if (open.Count > 0)
            {
                await s.PauseAsync(
                    "cleanup-loose-file",
                    $"The file \"{Path.GetFileName(path)}\" is still open in OneNote as a section. Close it: right-click its section tab > Close This Section. Then continue.");
                open = await NavProbe.OpenAtPathAsync(s, path, n.C.Root);
            }

            if (open.Count > 0)
            {
                file = "kept: still open in OneNote";
            }
            else
            {
                try
                {
                    File.Delete(path);
                    file = "deleted";
                }
                catch (IOException ex)
                {
                    file = $"kept: {ex.GetType().Name}";
                }
                catch (UnauthorizedAccessException ex)
                {
                    file = $"kept: {ex.GetType().Name}";
                }
            }
        }

        s.Recorder.Row("cleanup-published", "Cleanup", "-", new { file = n.PublishedFileLabel }, null, new { file, exists = File.Exists(path) });
        await AppState.CleanupAsync(n.C);

        var notebooks = (await s.NotebooksAsync()).ToString();
        s.Recorder.Row("cleanup-test-notebooks", "Cleanup", "GetHierarchy", null, null, new
        {
            mcpTest = TestNotebookGuard.FindNotebookId(notebooks, TestNotebook.ResolveFolder(Environment.GetEnvironmentVariable)) is not null,
            mcpTestCloud = TestNotebookGuard.FindCloudNotebookId(notebooks) is not null,
        });
    }

    // --- W6: no OneNote window (last step, after cleanup) -----------------------------------------------------------

    private static async Task NoWindowAsync(NavContext n)
    {
        var s = n.S;
        if (await n.TargetsAsync(cloud: false) is not { } t)
        {
            AppState.Missing(n.C, "w6", "W6", "MCP Test has no page to navigate to", "setup");
            return;
        }

        await s.PauseAsync(
            "no-window-close",
            "Close OneNote's window with its X (top right). Don't use File > Exit or Task Manager. "
            + "Then tell the team lead whether OneNote's icon is still in the taskbar or in the notification area by the clock. Then continue.");
        if (!s.Probe.IsOneNoteRunning())
        {
            s.Recorder.Row("w6-exited", "W6", "-", null, null, new { oneNoteExited = true, breaker = s.Probe.Tripped });
            await s.PauseAsync("no-window-exited", "OneNote has exited. The run stops here. Start OneNote again yourself when you're ready (the probe never starts it). Then continue.");
            return; // the step's breaker check stops the run
        }

        await WindowsRowAsync(n, "w6-windows", "W6", new { windows = "none: the last one was closed by hand" });
        await s.ObservePauseAsync("no-window", $"OneNote may open a window again and show {t.Path(t.A)}, or nothing may show.");
        await NavRowAsync(
            n,
            "w6-navigate",
            "W6",
            false,
            "NavigateTo",
            new { target = "page A", windows = "none" },
            g => s.NavigateToAsync(t.A.PageId, g),
            new NavExpect(t.NotebookId, t.A.SectionId, t.A.PageId),
            null,
            async () => new { windows = await s.WindowsSnapshotAsync(), oneNoteRunning = s.Probe.IsOneNoteRunning() });
        await s.ReportPauseAsync("no-window", "whether a OneNote window appeared, and what it shows. Leave OneNote running; if it has exited, start it again yourself.");
    }

    // --- One navigation row: F/V, the call, K1's landing series, K2's checks ------------------------------------------

    private static async Task NavRowAsync(
        NavContext n,
        string probe,
        string question,
        bool cloud,
        string method,
        object args,
        Func<ComGateway, Task<Attempt>> call,
        NavExpect? expect,
        string? linkPageGuid,
        Func<Task<object?>>? afterwards = null)
    {
        var s = n.S;
        var gateway = n.Gateway(cloud);
        var before = await s.ViewAsync();
        var attempt = await call(gateway);
        var landing = await s.LandingSeriesAsync(cloud, gateway);
        var k2 = await K2Async(n, landing, expect, linkPageGuid, gateway);
        var after = await s.ViewAsync();
        var extra = afterwards is null ? null : await afterwards();
        s.Recorder.Row(probe, question, method, args, attempt, new { fNewWindow = false, landing, k2, fv = new { before, after }, extra });
        if (cloud)
        {
            await s.SettleAsync(probe, question);
        }
    }

    /// <summary>
    /// K2: three ways to check a landing. (a) The window's IDs at the end of the landing series against the target's;
    /// (b) <c>GetHyperlinkToObject</c> on the window's current page (when it lies in an allowed notebook) against the
    /// followed link's <c>page-id</c> GUID (OneMore row 5); (c) the hierarchy's <c>isCurrentlyViewed</c> IDs.
    /// </summary>
    private static async Task<object> K2Async(NavContext n, LandingSeries landing, NavExpect? expect, string? linkPageGuid, ComGateway gateway)
    {
        var s = n.S;
        var last = landing.Last;
        object? hyperlink = null;
        if (linkPageGuid is not null && last?.PageId is { Length: > 0 } current)
        {
            var (attempt, link) = await ProbeSession.TryValueAsync(() => TestNotebook.HyperlinkAsync(gateway, current, string.Empty, web: false, s.RunProbeRoot, ProbeSession.Ct));
            await s.CheckTimeoutAsync("GetHyperlinkToObject-k2", attempt, s.LimitOf(gateway));
            var guid = attempt.Ok ? LinkShape.Parse(link).PageGuid : null;
            hyperlink = new { attempt.Outcome, attempt.HResult, currentPageGuid = guid, matches = attempt.Ok ? guid == linkPageGuid : (bool?)null };
        }

        var viewed = await s.ViewedIdsAsync(gateway);
        return new
        {
            window = NavProbe.Compare(expect, last?.NotebookId, last?.SectionId, last?.PageId),
            hyperlink,
            hierarchy = NavProbe.Compare(expect, viewed.Notebook, viewed.Section, viewed.Page),
            viewed = new { notebook = viewed.Notebook, section = viewed.Section, page = viewed.Page },
        };
    }
}

/// <summary>Spike 4.12: one run's state (4.1's context, the targets and links read so far, the cloud gateway).</summary>
internal sealed class NavContext(AppStateContext c)
{
    /// <summary>The run's one notebook, made in the run folder (N4).</summary>
    public const string RunTag = "n1";

    private readonly Dictionary<bool, NavTargets?> targets = new();
    private ComGateway? cloudGateway;

    public AppStateContext C { get; } = c;

    public ProbeSession S => C.S;

    /// <summary>Links read so far, by (cloud, hierarchy ID); each is in <see cref="ProbeSession.NavigableUrls"/>.</summary>
    public Dictionary<(bool Cloud, string Id), string> Links { get; } = new();

    /// <summary>N5's file: <c>probe nav &lt;stamp&gt;.one</c> in the publish folder.</summary>
    public string PublishedPath => Path.Combine(ProbeSession.PublishDir, $"probe nav {C.Stamp}.one");

    /// <summary>How rows name N5's file.</summary>
    public string PublishedFileLabel => "<publish folder>\\probe nav <stamp>.one";

    /// <summary>The session's gateway (60 s) locally; on the cloud a second gateway with 4.1's 180 s limit.</summary>
    public ComGateway Gateway(bool cloud) => cloud ? cloudGateway ??= S.CreateSideGateway(ProbeSession.CloudProbeCallSeconds) : S.Gateway;

    public string NotebookName(bool cloud) => cloud ? TestNotebook.CloudNotebookName : TestNotebook.NotebookName;

    /// <summary>MCP Test's (Seed, then Probes) or MCP Test Cloud's (Probes) pages A, B and C, read once from the hierarchy.</summary>
    public async Task<NavTargets?> TargetsAsync(bool cloud)
    {
        if (!targets.TryGetValue(cloud, out var found))
        {
            var id = cloud ? await S.CloudIdAsync(Gateway(cloud)) : await S.McpTestIdAsync();
            found = NavProbe.Pick(
                await S.TreeAsync(id, CoreScope.Pages, Gateway(cloud)),
                cloud ? ProbeSession.ProbesSectionName : TestNotebook.SeedSectionName,
                cloud ? null : ProbeSession.ProbesSectionName);
            targets[cloud] = found;
        }

        return found;
    }

    /// <summary>The link to <paramref name="id"/>, read once (guarded) and kept.</summary>
    public async Task<string?> LinkForAsync(bool cloud, string id)
    {
        if (Links.TryGetValue((cloud, id), out var link) && S.NavigableUrls.Contains(link))
        {
            return link;
        }

        var (attempt, read) = await S.LinkAsync(id, string.Empty, Gateway(cloud));
        if (read is null)
        {
            S.Recorder.Row("link", "N1", "GetHyperlinkToObject", new { cloud }, attempt, null);
            return null;
        }

        Links[(cloud, id)] = read;
        return read;
    }
}

/// <summary>One page a navigation targets.</summary>
internal sealed record NavPage(string SectionId, string SectionName, string PageId, string PageName);

/// <summary>A test notebook's navigation targets: A (the preferred section's first page), B (in another section where possible) and C (a third page, or B).</summary>
internal sealed record NavTargets(string NotebookId, string NotebookName, NavPage A, NavPage B, NavPage C)
{
    public string Path(NavPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return $"{NotebookName} > {page.SectionName} > {(page.PageName.Length == 0 ? "(untitled page)" : page.PageName)}";
    }
}

/// <summary>What a navigation should land on (null where the link doesn't say).</summary>
internal sealed record NavExpect(string? Notebook, string? Section, string? Page);

/// <summary>Spike 4.12's pure helpers (unit-tested in <c>ProbeHelpersTests</c>) and small reads.</summary>
internal static class NavProbe
{
    /// <summary>R1's delays between the two calls of a pair.</summary>
    public static readonly IReadOnlyList<int> RepeatDelaysSeconds = [0, 1, 2, 3, 5];

    /// <summary>R1: at least this long between pairs (locally; on the cloud the 10 s settle is longer).</summary>
    public const int PairGapSeconds = 6;

    /// <summary>W2: how long Phill has to bring OneNote to the front after the pause.</summary>
    public const int FrontDelaySeconds = 10;

    public static string Where(bool cloud) => cloud ? "cloud" : "local";

    /// <summary>The earlier findings this spike relies on and doesn't re-probe (Design Notes), as the setup row lists them.</summary>
    public static readonly object ReliedOn = new[]
    {
        "NavigateTo takes focus (spike 1 row 25)",
        "NavigateTo makes OneNote download a page's missing images (story 1.21's probe)",
        "NavigateTo does nothing visible with no window (story 1.21's review; W6 measures it here)",
        "a close and reopen changes every ID and keeps path (spike 2.2 constraint 9); CloseNotebook itself is spike 4.1's",
        "a published .one opened by OpenHierarchy lands as a hidden Open Section, in plaintext (5.1 constraints 7 and 17)",
        "OneMore (research rows 5, 10, 14, 24, 25): the link's page GUID survives moves; release window wrappers; the cloud repeat window is about 2 s; NavigateToUrl reports success for a missing page; WindowHandle can be an inner pane",
    };

    /// <summary>
    /// The pages to navigate between in a notebook's pages-scope tree: A, the first page of <paramref name="firstSection"/>;
    /// B, the first page of <paramref name="secondSection"/> if given, else of the first other section with a page, else
    /// the next page in A's section; C, a third page, else B. Recycle-bin, encrypted and Vault sections are left out.
    /// Null when there aren't two pages.
    /// </summary>
    public static NavTargets? Pick(XElement notebook, string firstSection, string? secondSection)
    {
        ArgumentNullException.ThrowIfNull(notebook);
        var ns = notebook.Name.Namespace;
        var sections = notebook.Descendants(ns + "Section")
            .Where(e => (string?)e.Attribute("isInRecycleBin") != "true"
                && !e.Ancestors().Any(a => (string?)a.Attribute("isRecycleBin") == "true")
                && (string?)e.Attribute("encrypted") != "true"
                && (string?)e.Attribute("name") != BallastContent.VaultSectionName)
            .ToList();
        IEnumerable<NavPage> Pages(XElement section) => section.Elements(ns + "Page")
            .Where(p => (string?)p.Attribute("isInRecycleBin") != "true" && (string?)p.Attribute("ID") is not null)
            .Select(p => new NavPage((string)section.Attribute("ID")!, (string?)section.Attribute("name") ?? string.Empty, (string)p.Attribute("ID")!, (string?)p.Attribute("name") ?? string.Empty));

        var all = sections.SelectMany(Pages).ToList();
        var a = sections.Where(e => (string?)e.Attribute("name") == firstSection).SelectMany(Pages).FirstOrDefault();
        if (a is null)
        {
            return null;
        }

        var others = all.Where(p => p.PageId != a.PageId).ToList();
        var b = (secondSection is null ? null : others.FirstOrDefault(p => p.SectionName == secondSection))
            ?? others.FirstOrDefault(p => p.SectionId != a.SectionId)
            ?? others.FirstOrDefault();
        if (b is null)
        {
            return null;
        }

        var c = others.FirstOrDefault(p => p.PageId != b.PageId) ?? b;
        return new NavTargets((string)notebook.Attribute("ID")!, (string?)notebook.Attribute("name") ?? string.Empty, a, b, c);
    }

    /// <summary>
    /// W1's summary of a snapshot: <c>Count</c>, the index base (the lowest <c>Item(i)</c> that answered), the indexes
    /// that failed and how, which index <c>CurrentWindow</c> is (by handle), and how many distinct windows were listed.
    /// </summary>
    public static WindowsSummary Summarize(WindowsSnapshot windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var answered = windows.Items.Where(i => i.ItemHResult is null).ToList();
        var currentHandle = windows.Current?.HandleValue;
        return new WindowsSummary(
            windows.Count.Value,
            windows.Count.HResult,
            answered.Count == 0 ? null : answered.Min(i => i.Index),
            windows.Items.Where(i => i.ItemHResult is not null).Select(i => new FailedItem(i.Index, i.ItemHResult!)).ToList(),
            currentHandle is null ? null : answered.FirstOrDefault(i => i.HandleValue == currentHandle)?.Index,
            windows.Current?.ItemHResult,
            windows.ListedWindows.Count,
            windows.ListedWindows.Select(w => w.TopLevel).ToList());
    }

    /// <summary>W4: for each window listed after, its page before and after (by handle), and whether it moved.</summary>
    public static IReadOnlyList<WindowMove> Moves(WindowsSnapshot before, WindowsSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return after.ListedWindows.Select(w =>
        {
            var was = before.ListedWindows.FirstOrDefault(b => b.HandleValue == w.HandleValue);
            return new WindowMove(w.Index, w.HandleValue, was?.Page, w.Page, was is not null && was.Page != w.Page);
        }).ToList();
    }

    /// <summary>W4: the window to move with <c>Window.NavigateTo</c>: the last listed window not showing <paramref name="movedToPageId"/>, else the last listed; null with fewer than two.</summary>
    public static WindowRead? SecondWindow(WindowsSnapshot windows, string movedToPageId)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var listed = windows.ListedWindows;
        return listed.Count < 2 ? null : listed.LastOrDefault(w => w.Page != movedToPageId) ?? listed[^1];
    }

    /// <summary>K2: per level, whether the actual ID is the expected one (null where nothing is expected).</summary>
    public static object? Compare(NavExpect? expect, string? notebook, string? section, string? page) =>
        expect is null ? null : new
        {
            notebook = expect.Notebook is null ? (bool?)null : expect.Notebook == notebook,
            section = expect.Section is null ? (bool?)null : expect.Section == section,
            page = expect.Page is null ? (bool?)null : expect.Page == page,
        };

    /// <summary>N1: a paragraph's <c>objectID</c> on the page (MCP Test or MCP Test Cloud only): the first in an outline, else the title's.</summary>
    public static async Task<string?> ParagraphIdAsync(ProbeSession s, string pageId, ComGateway? gateway = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        var page = await s.PageAsync(pageId, gateway: gateway);
        var ns = page.Name.Namespace;
        static string? Id(XElement? e) => (string?)e?.Attribute("objectID");
        return Id(page.Elements(ns + "Outline").Descendants(ns + "OE").FirstOrDefault(e => Id(e) is not null))
            ?? Id(page.Descendants(ns + "OE").FirstOrDefault(e => Id(e) is not null));
    }

    /// <summary>The Ballast Vault section in MCP Test's sections-scope tree (outside the recycle bin), or null.</summary>
    public static XElement? VaultSection(XElement notebook)
    {
        ArgumentNullException.ThrowIfNull(notebook);
        return notebook.Elements(notebook.Name.Namespace + "Section")
            .FirstOrDefault(e => (string?)e.Attribute("name") == BallastContent.VaultSectionName && (string?)e.Attribute("isInRecycleBin") != "true");
    }

    /// <summary>The Vault's <c>locked</c> and <c>encrypted</c> attributes, from a fresh sections-scope read.</summary>
    public static async Task<object> LockedAsync(ProbeSession s, string mcp, string vaultId)
    {
        ArgumentNullException.ThrowIfNull(s);
        var vault = ById(await s.TreeAsync(mcp, CoreScope.Sections), vaultId);
        return new { locked = (string?)vault?.Attribute("locked"), encrypted = (string?)vault?.Attribute("encrypted") };
    }

    /// <summary>The IDs of the open notebooks.</summary>
    public static async Task<HashSet<string>> OpenNotebookIdsAsync(ProbeSession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return (await s.NotebooksAsync()).Elements(One + "Notebook").Select(e => (string?)e.Attribute("ID")).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// N5: every element of a full sections-scope read whose <c>path</c> is <paramref name="path"/>: its kind, name and
    /// ID, and where it sits (its container's kind, and the notebook it is in, named only when loggable).
    /// </summary>
    public static async Task<IReadOnlyList<object>> OpenAtPathAsync(ProbeSession s, string path, string? runProbeRoot)
    {
        ArgumentNullException.ThrowIfNull(s);
        return OpenAtPath(await s.TreeAsync(string.Empty, CoreScope.Sections), path, runProbeRoot);
    }

    /// <summary><see cref="OpenAtPathAsync"/>'s pure part.</summary>
    public static IReadOnlyList<object> OpenAtPath(XElement all, string path, string? runProbeRoot)
    {
        ArgumentNullException.ThrowIfNull(all);
        static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        return all.Descendants()
            .Where(e => Same((string?)e.Attribute("path"), path))
            .Select(e =>
            {
                var notebook = e.Ancestors(e.Name.Namespace + "Notebook").FirstOrDefault();
                var loggable = notebook is null || ProbeView.IsLoggable((string?)notebook.Attribute("name"), (string?)notebook.Attribute("path"), runProbeRoot);
                return (object)new
                {
                    kind = e.Name.LocalName,
                    name = loggable ? (string?)e.Attribute("name") : null,
                    id = (string?)e.Attribute("ID"),
                    container = e.Parent?.Name.LocalName,
                    notebook = notebook is null ? null : loggable ? (string?)notebook.Attribute("name") : ProbeView.OtherNotebook,
                    isCurrentlyViewed = (string?)e.Attribute("isCurrentlyViewed") == "true",
                };
            })
            .ToList();
    }
}

/// <summary>W1: <see cref="NavProbe.Summarize"/>'s result.</summary>
internal sealed record WindowsSummary(object? Count, string? CountHResult, int? IndexBase, IReadOnlyList<FailedItem> Failed, int? CurrentIndex, string? CurrentHResult, int Windows, IReadOnlyList<bool?> TopLevel);

/// <summary>An <c>Item(i)</c> that failed, and how.</summary>
internal sealed record FailedItem(int? Index, string HResult);

/// <summary>W4: one window's page before and after a navigation.</summary>
internal sealed record WindowMove(int? Index, ulong? Handle, string? PageBefore, string? PageAfter, bool Moved);
