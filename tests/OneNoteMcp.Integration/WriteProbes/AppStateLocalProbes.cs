using System.Xml.Linq;
using OneNoteMcp.Com.Interop;
using Xunit;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Spike 4.1, scenario <c>app-state-local</c> (sitting A): <c>SyncHierarchy</c>, <c>CloseNotebook</c> (never
/// <c>force</c>) and <c>OpenHierarchy</c> <c>cftNone</c> on MCP Test, throwaways <c>b</c> and <c>c</c> (adopted by exact
/// name) and the run's own notebooks <c>o1</c>…<c>o4</c>, made in the run's probe folder
/// <c>%LOCALAPPDATA%\OneNoteMcp-Test\probes\app-state\&lt;stamp&gt;</c>. Matrix rows S1 L, S2, C1, C2 L, C4, C5, C6, O1, O4
/// L, O5–O10, L and D, in the Sitting script's order. Phill is present: every close, and every open of a closed
/// notebook, has a pause before it (what he'll see) and one after (what he saw).
/// <para>
/// Rerun one step with <c>ONENOTE_MCP_PROBE_STEPS</c>; a step of an earlier run (such as <c>cleanup</c>) needs
/// <c>ONENOTE_MCP_PROBE_STAMP=&lt;that run's stamp&gt;</c> too, so it finds that run's probe folder.
/// </para>
/// </summary>
public sealed class AppStateLocalProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "app-state-local";

    [Fact]
    public async Task AppStateLocal()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.ProbeCallSeconds);
        var c = AppState.Begin(s, cloud: false);
        await s.SetupAsync("setup", () => AppState.AdoptAsync(c));
        s.Recorder.Row("c6", "C6", "CloseNotebook", null, null, new { notProbed = "by design (Q2 a): closing the last open notebook isn't probed; C4 (closing the notebook on screen) is the nearest probe" });

        await s.StepAsync("sync-local", () => SyncLocalAsync(c));
        await s.StepAsync("make", () => MakeAsync(c));
        await s.StepAsync("fresh", () => FreshAsync(c));
        await s.StepAsync("close-open-b", () => CloseOpenBAsync(c));
        await s.StepAsync("already-open", () => AlreadyOpenAsync(c));
        await s.StepAsync("missing", () => MissingAsync(c));
        await s.StepAsync("not-notebook", () => NotNotebookAsync(c));
        await s.StepAsync("section-group", () => SectionGroupAsync(c));
        await s.StepAsync("pending-local", () => PendingLocalAsync(c));
        await s.StepAsync("close-shown", () => CloseShownAsync(c));
        await s.StepAsync("junction-unc", () => JunctionUncAsync(c));
        await s.StepAsync("modal", () => ModalAsync(c));
        await s.StepAsync("late-files", () => LateFilesAsync(c));
        await s.StepAsync("cleanup", () => AppState.CleanupAsync(c));

        s.Finish();
    }

    // --- S1 L, S2 -------------------------------------------------------------------------------------------------

    private static async Task SyncLocalAsync(AppStateContext c)
    {
        var s = c.S;
        await AppState.SyncRowAsync(c, "s1-local-mcp-test", "S1", await s.McpTestIdAsync(), "the MCP Test notebook");

        var synthetic = ProbeSession.SyntheticId();
        if (await s.ExistsAnywhereAsync(synthetic))
        {
            throw new InvalidOperationException("The synthetic ID exists in a notebook.");
        }

        s.SyncGone.Add(synthetic);
        await AppState.SyncRowAsync(c, "s2-synthetic-id", "S2", synthetic, "a fresh {GUID}{1}{B0} that no notebook has");
        await s.ReportPauseAsync("sync-local", "any sync icon, progress bar, or dialog while MCP Test was synced. Probably nothing.");
    }

    // --- Setup: o1…o4 in the run's probe folder (O7's creates first, so L runs 5 min later) -------------------------

    private static async Task MakeAsync(AppStateContext c)
    {
        var s = c.S;
        var names = string.Join(", ", c.RunTags.Select(t => $"\"{c.RunName(t)}\""));
        await s.ObservePauseAsync("make", $"four new notebooks appear in OneNote's notebook list: {names}, in the folder {c.Root}. Creating one may bring OneNote to the front.");

        // o2 and o4 come last, so "fresh" closes them as soon after their create as possible.
        foreach (var tag in new[] { "o1", "o3", "o2", "o4" })
        {
            var folder = c.RunPath(c.RunName(tag));
            var view = await s.ViewAsync();
            var (attempt, id) = await s.CreateThrowawayAsync(c.RunName(tag), requireEmpty: true, c.Root);
            var after = await s.ViewAsync();
            if (id is not null)
            {
                switch (tag)
                {
                    case "o1":
                        var section = await s.MustCreateChildAsync(id, $"probe o1 s {c.Stamp}.one", CreateFileType.cftSection);
                        await s.CreateProbePageAsync(section, $"probe o1 p {c.Stamp}");
                        break;
                    case "o3":
                        var group = await s.MustCreateChildAsync(id, c.GroupName, CreateFileType.cftFolder);
                        await s.MustCreateChildAsync(group, $"probe sg s {c.Stamp}.one", CreateFileType.cftSection);
                        break;
                    case "o4":
                        await s.MustCreateChildAsync(id, $"probe o4 s {c.Stamp}.one", CreateFileType.cftSection);
                        break;
                }
            }

            s.Recorder.Row($"make-{tag}", "Setup", "OpenHierarchy(cftNotebook)", new { path = $"<run folder>\\{c.RunName(tag)}", folder = "absent" }, attempt, new
            {
                id,
                notebook = id is null ? null : await s.NotebookInfoAsync(id),
                fv = new { before = view, after },
                diskAtCreate = ProbeSession.Snapshot(folder), // O7: listed at create
            });
        }

        await s.ReportPauseAsync("make", "whether OneNote came to the front, and any dialog.");
    }

    // --- O7, and S2's closed run notebook -------------------------------------------------------------------------

    private static async Task FreshAsync(AppStateContext c)
    {
        var s = c.S;
        var targets = new List<(string Tag, string Id, NodeInfo Info)>();
        foreach (var tag in new[] { "o2", "o4" })
        {
            if (await c.RunNotebookAsync(tag) is { } id && await s.NotebookInfoAsync(id) is { } info)
            {
                targets.Add((tag, id, info));
            }
            else
            {
                AppState.Missing(c, $"o7-{tag}", "O7", $"notebook {tag} isn't open", "make");
            }
        }

        if (targets.Count == 0)
        {
            return;
        }

        await s.ObservePauseAsync("fresh", $"the notebooks {string.Join(" and ", targets.Select(t => ProbeSession.Label(t.Info.Name, t.Info.Path)))} disappear from the notebook list, then may come back.");
        var atClose = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (tag, id, _) in targets)
        {
            var close = await AppState.CloseRowAsync(c, $"o7-close-{tag}", "O7", id, args: new { notebook = tag, sections = tag == "o4" ? 1 : 0 });
            atClose[tag] = new { close.Closed, files = ProbeSession.Snapshot(c.RunPath(c.RunName(tag))) };
        }

        // S2's second half: a closed run notebook's old ID (CloseRowAsync added it to SyncGone).
        if (targets.FirstOrDefault(t => t.Tag == "o2") is { Id: { } old } && s.SyncGone.Contains(old))
        {
            await AppState.SyncRowAsync(c, "s2-closed-run-notebook", "S2", old, "o2's ID from before its close");
        }
        else
        {
            AppState.Missing(c, "s2-closed-run-notebook", "S2", "o2 didn't close", "fresh");
        }

        foreach (var (tag, _, info) in targets)
        {
            var folder = c.RunPath(c.RunName(tag));
            await AppState.OpenRowAsync(c, $"o7-open-{tag}", "O7", folder, $"{tag}'s folder, created and closed moments ago ({(tag == "o4" ? "one section" : "no section")})", info, extra: new
            {
                atClose = atClose.GetValueOrDefault(tag),
                diskAtOpen = ProbeSession.Snapshot(folder),
            });
        }

        await s.ReportPauseAsync("fresh", "any dialog, and whether the two notebooks came back to the notebook list.");
    }

    // --- C1, C5, O1 -----------------------------------------------------------------------------------------------

    private static async Task CloseOpenBAsync(AppStateContext c)
    {
        var s = c.S;
        var b = await c.AdoptedAsync(AppState.NameB);
        if (b is null || await s.NotebookInfoAsync(b) is not { } info)
        {
            AppState.Missing(c, "c1-close-b", "C1", "throwaway b isn't open", "setup");
            return;
        }

        var countsBefore = FillSchedule.Count(await s.TreeAsync(b));
        var grants = await AppState.GrantTargetsAsync(c, b);
        var grantsBefore = await s.ProbeGrantsAsync(grants);

        await s.ObservePauseAsync("close-open-b", $"the notebook {ProbeSession.Label(info.Name, info.Path)} disappears from the notebook list, then comes back.");
        var close = await AppState.CloseRowAsync(c, "c1-close-b", "C1", b, args: new { notebook = "b", pending = "nothing", shown = "nothing" });
        var grantsAfterClose = await s.ProbeGrantsAsync(grants);

        object? grantsAfterReopen = null;
        if (close.Closed)
        {
            await AppState.OpenRowAsync(c, "o1-open-b", "O1", info.Path!, "b's folder, closed moments ago", info, countsBefore: countsBefore);
            await c.AdoptedAsync(AppState.NameB);
            grantsAfterReopen = await s.ProbeGrantsAsync(grants);
        }
        else
        {
            AppState.Missing(c, "o1-open-b", "O1", "b didn't close", "close-open-b");
        }

        s.Recorder.Row("c5-local", "C5", "list_grants", new { grants = grants.Count }, null, new { before = grantsBefore, afterClose = grantsAfterClose, afterReopen = grantsAfterReopen });
        await s.ReportPauseAsync("close-open-b", "whether OneNote came to the front, whether the page shown changed, and any dialog.");
    }

    // --- O4 L -----------------------------------------------------------------------------------------------------

    private static async Task AlreadyOpenAsync(AppStateContext c)
    {
        var s = c.S;
        var mcp = await s.McpTestIdAsync();
        var mcpInfo = await s.NotebookInfoAsync(mcp);
        await s.PauseAsync("already-open-show", $"In OneNote, click the page {ProbeSession.SafePage(cloud: false)}, so that it is the page shown. Don't type. Then continue.");
        await AppState.OpenRowAsync(c, "o4-local-mcp-test", "O4", TestNotebook.ResolveFolder(Environment.GetEnvironmentVariable), "MCP Test's folder, while it is open", mcpInfo, allowOpenTestNotebooks: true);
        await s.PauseAsync(
            "already-open-mcp-test-report",
            $"Tell the team lead whether OneNote switched to the notebook MCP Test just now (and which page it shows), or stayed on {ProbeSession.SafePage(cloud: false)}. "
            + $"Then click {ProbeSession.SafePage(cloud: false)} again, so that it is shown. Don't type. Then continue.");

        if (await c.AdoptedAsync(AppState.NameC) is { } cId && await s.NotebookInfoAsync(cId) is { } cInfo)
        {
            await AppState.OpenRowAsync(c, "o4-local-c", "O4", cInfo.Path!, "c's folder, while it is open", cInfo);
            await s.ReportPauseAsync("already-open-c", $"whether OneNote switched to the notebook {ProbeSession.Label(cInfo.Name, cInfo.Path)}, and which page it shows now.");
        }
        else
        {
            AppState.Missing(c, "o4-local-c", "O4", "throwaway c isn't open", "setup");
        }
    }

    // --- O5, O6 ---------------------------------------------------------------------------------------------------

    private static async Task MissingAsync(AppStateContext c)
    {
        await AppState.OpenRowAsync(c, "o5-missing", "O5", c.RunPath($"probe missing {c.Stamp}"), "a missing folder in the run folder");
        await AppState.OpenRowAsync(c, "o5-missing-parent", "O5", Path.Combine(c.RunPath($"probe no parent {c.Stamp}"), $"probe child {c.Stamp}"), "a folder under a missing parent");
    }

    private static async Task NotNotebookAsync(AppStateContext c)
    {
        var empty = c.RunPath(c.EmptyFolderName);
        Directory.CreateDirectory(empty);
        await AppState.OpenRowAsync(c, "o6-empty-folder", "O6", empty, "an empty folder");

        var marker = c.RunPath(c.MarkerFolderName);
        Directory.CreateDirectory(marker);
        await File.WriteAllTextAsync(Path.Combine(marker, TestNotebook.ProbeMarkerFile), "spike 4.1 probe marker", ProbeSession.Ct);
        await AppState.OpenRowAsync(c, "o6-marker-folder", "O6", marker, "a folder holding only probe-marker.txt");
    }

    // --- O8 -------------------------------------------------------------------------------------------------------

    private static async Task SectionGroupAsync(AppStateContext c)
    {
        var s = c.S;
        if (await c.RunNotebookAsync("o3") is not { } o3 || await s.NotebookInfoAsync(o3) is not { } info)
        {
            AppState.Missing(c, "o8", "O8", "o3 isn't open", "make");
            return;
        }

        var group = (await s.TreeAsync(o3, CoreScope.Sections)).Descendants(One + "SectionGroup").FirstOrDefault(g => (string?)g.Attribute("name") == c.GroupName);
        var groupId = (string?)group?.Attribute("ID");
        var groupPath = (string?)group?.Attribute("path") ?? Path.Combine(info.Path!, c.GroupName);
        s.Recorder.Row("o8-setup", "O8", "GetHierarchy", null, null, new { groupId, groupPath, disk = ProbeSession.Snapshot(groupPath) });

        var whileOpen = await AppState.OpenRowAsync(c, "o8-group-o3-open", "O8", groupPath, "o3's section-group folder, while o3 is open", extra: new { groupId });
        s.Recorder.Row("o8-group-o3-open-id", "O8", "-", null, null, new { returnedGroupId = whileOpen.Id is not null && whileOpen.Id == groupId });

        await s.ObservePauseAsync("section-group", $"the notebook {ProbeSession.Label(info.Name, info.Path)} disappears from the notebook list. Then its section group \"{c.GroupName}\" may appear as a notebook of its own.");
        var close = await AppState.CloseRowAsync(c, "o8-close-o3", "O8", o3, args: new { notebook = "o3" });
        if (close.Closed)
        {
            await AppState.OpenRowAsync(c, "o8-group-o3-closed", "O8", groupPath, "o3's section-group folder, after o3 closed", extra: new { groupId });
        }
        else
        {
            AppState.Missing(c, "o8-group-o3-closed", "O8", "o3 didn't close", "section-group");
        }

        await s.ReportPauseAsync("section-group", $"whether a notebook named like \"{c.GroupName}\" appeared in the notebook list, and any dialog.");
    }

    // --- C2 L -----------------------------------------------------------------------------------------------------

    private static async Task PendingLocalAsync(AppStateContext c)
    {
        var s = c.S;
        if (await c.AdoptedAsync(AppState.NameC) is not { } cId || await s.NotebookInfoAsync(cId) is not { } info)
        {
            AppState.Missing(c, "c2-local", "C2", "throwaway c isn't open", "setup");
            return;
        }

        var pending = await AppState.PendingPageAsync(c, cId, "local");
        c.PendingLocal = pending;
        if (!await s.EnsureNothingShownAsync(cId, "CloseNotebook"))
        {
            s.Recorder.Row("c2-local", "C2", "CloseNotebook", null, ProbeSession.NotShownRefusal, null);
            return;
        }

        await s.ObservePauseAsync(
            "pending-local",
            $"the notebook {ProbeSession.Label(info.Name, info.Path)} disappears from the notebook list. If a dialog appears and stays, tell the team lead its title and text, then press Cancel. "
            + "The probe then opens the notebook again, and it comes back.");
        await AppState.PendingCloseAsync(c, "c2-local", "C2", cId, info, pending, AppState.NameC);
        await s.ReportPauseAsync("pending-local", "any dialog (its title and text, and whether you pressed Cancel), and whether OneNote now shows a \"Misplaced Sections\" entry. Look, don't open it.");
    }

    // --- C4 -------------------------------------------------------------------------------------------------------

    private static async Task CloseShownAsync(AppStateContext c)
    {
        var s = c.S;
        if (await c.AdoptedAsync(AppState.NameC) is not { } cId || await s.NotebookInfoAsync(cId) is not { } info)
        {
            AppState.Missing(c, "c4", "C4", "throwaway c isn't open", "setup");
            return;
        }

        var tree = await s.TreeAsync(cId);
        var page = tree.Descendants(One + "Page").FirstOrDefault(p => (string?)p.Attribute("name") == c.PendingLocal?.Title && (string?)p.Attribute("isInRecycleBin") != "true")
            ?? tree.Descendants(One + "Page").FirstOrDefault(p => (string?)p.Attribute("isInRecycleBin") != "true" && !p.Ancestors().Any(a => (string?)a.Attribute("isRecycleBin") == "true"));
        if (page is null)
        {
            AppState.Missing(c, "c4", "C4", "throwaway c has no page to show", "pending-local");
            return;
        }

        var section = page.Parent;
        await s.PauseAsync(
            "close-shown-show",
            $"In OneNote, click the page \"{(string?)page.Attribute("name")}\" in the section \"{(string?)section?.Attribute("name")}\" of the notebook {ProbeSession.Label(info.Name, info.Path)}, "
            + "so that it is the page shown in OneNote's only window. Don't type. Then continue.");
        await s.ObservePauseAsync("close-shown", $"the notebook {ProbeSession.Label(info.Name, info.Path)}, whose page is on screen, disappears from the notebook list. Watch what OneNote's window shows afterwards.");
        var close = await AppState.CloseRowAsync(c, "c4-close-shown", "C4", cId, shownCheck: false, args: new { notebook = "c", shown = "its page, in OneNote's only window" });
        await s.ReportPauseAsync(
            "close-shown",
            "what OneNote's window shows now that the notebook is closed (a blank page, another notebook's page, a message), and any dialog. "
            + $"After this pause the notebook {ProbeSession.Label(info.Name, info.Path)} comes back to the notebook list.");
        if (close.Closed)
        {
            await AppState.OpenRowAsync(c, "c4-reopen", "C4", info.Path!, "c's folder, closed while shown", info);
            await c.AdoptedAsync(AppState.NameC);
            await s.ReportPauseAsync("close-shown-reopen", "whether the notebook came back, and what the window shows now.");
        }
    }

    // --- O9, O10 --------------------------------------------------------------------------------------------------

    private static async Task JunctionUncAsync(AppStateContext c)
    {
        var s = c.S;
        var o1Path = c.RunPath(c.RunName("o1"));
        var o1 = await c.RunNotebookAsync("o1");
        var o1Info = o1 is null ? null : await s.NotebookInfoAsync(o1);
        var label = o1Info is not null ? ProbeSession.Label(o1Info.Name, o1Info.Path) : $"in the folder {o1Path} (closed now)"; // read live, never guessed
        await s.ObservePauseAsync("junction-unc", $"the notebook {label} disappears from the notebook list if it is there, then appears and disappears twice more (opened through two other paths, so it may show another name).");
        if (o1 is not null)
        {
            var close = await AppState.CloseRowAsync(c, "o9-close-o1", "O9", o1, args: new { notebook = "o1" });
            if (!close.Closed)
            {
                AppState.Missing(c, "o9", "O9", "o1 didn't close", "junction-unc");
                return;
            }
        }

        if (!Directory.Exists(o1Path))
        {
            AppState.Missing(c, "o9", "O9", "o1's folder doesn't exist", "make");
            return;
        }

        var junction = c.RunPath(c.JunctionName);
        if (!Directory.Exists(junction))
        {
            s.MakeJunction(junction, o1Path);
        }

        s.Recorder.Row("o9-junction-made", "O9", "mklink /J", new { link = "<run folder>\\" + c.JunctionName, target = "<run folder>\\" + c.RunName("o1") }, null, new { isJunction = Junctions.IsJunction(junction) });
        var viaJunction = await AppState.OpenRowAsync(c, "o9-junction", "O9", junction, "a junction in the run folder pointing at o1's folder (o1 closed)", extra: new { junctionPath = junction, targetPath = o1Path });
        if (viaJunction.Notebook is { Id: { } j })
        {
            await AppState.CloseRowAsync(c, "o9-close-junction", "O9", j, args: new { notebook = "o1 through the junction" });
        }

        var unc = $@"\\localhost\{o1Path[0]}${o1Path[2..]}";
        if (!Directory.Exists(unc))
        {
            s.Recorder.Row("o10-unc", "O10", "OpenHierarchy(cftNone)", new { path = unc }, null, new { notProbed = "the \\\\localhost\\<drive>$ share isn't reachable without elevation (Directory.Exists is false); nothing was sent to OneNote" });
        }
        else
        {
            var viaUnc = await AppState.OpenRowAsync(c, "o10-unc", "O10", unc, "o1's folder through \\\\localhost\\<drive>$ (o1 closed)", extra: new { mapsTo = TestNotebookGuard.MapLocalhostAdminShare(unc) });
            if (viaUnc.Notebook is { Id: { } u })
            {
                await AppState.CloseRowAsync(c, "o10-close-unc", "O10", u, args: new { notebook = "o1 through the UNC path" });
            }
        }

        await s.ReportPauseAsync("junction-unc", "any dialog, and the name the notebook had in the list each time it appeared.");
    }

    // --- D --------------------------------------------------------------------------------------------------------

    private static async Task ModalAsync(AppStateContext c)
    {
        const string Q = "D";
        var s = c.S;
        var mcp = await s.McpTestIdAsync();
        var o1Path = c.RunPath(c.RunName("o1"));
        if (await c.RunNotebookAsync("o1") is not null)
        {
            s.Recorder.Row("d-o1-state", Q, "-", null, null, new { o1Open = true, note = "o1 should be closed here (junction-unc); the open below then shows what cftNone does on an open notebook under the dialog" });
        }

        var cId = await c.AdoptedAsync(AppState.NameC);

        // D may close c (or o1) with the shown check off: take them off screen first, so C4's shown page can't leak into D.
        foreach (var id in new[] { cId, await c.RunNotebookAsync("o1") }.OfType<string>())
        {
            if (!await s.EnsureNothingShownAsync(id, "CloseNotebook"))
            {
                s.Recorder.Row("d", Q, "-", null, ProbeSession.NotShownRefusal, new { notProbed = "a notebook D may close was still on screen" });
                return;
            }
        }

        if (!await s.PauseAsync(
                "d-open",
                "In OneNote, open File > Options and leave the Options dialog open. Don't change anything in it. Then continue. While it is open, the probe syncs MCP Test, "
                + $"opens the closed notebook in the folder {o1Path} (it may appear in the notebook list), and closes it again"
                + (cId is not null && await s.NotebookInfoAsync(cId) is { } cInfo ? $" (or, if it didn't open, closes {ProbeSession.Label(cInfo.Name, cInfo.Path)})." : ".")))
        {
            s.Recorder.Row("d", Q, "-", null, null, new { notProbed = "pause skipped" });
            return;
        }

        s.ShownCheckOff = true;
        try
        {
            var view = await s.ViewAsync();
            var sync = await s.SyncHierarchyAsync(mcp);
            var afterSync = await s.ViewAsync();
            s.Recorder.Row("d-sync", Q, "SyncHierarchy", new { target = "MCP Test" }, sync, new { applied = "not observable (SyncHierarchy returns before any sync is done)", fv = new { before = view, after = afterSync } });

            var (open, opened) = await s.OpenExistingAsync(o1Path);
            var afterOpen = await s.ViewAsync();
            var notebooks = (await s.NotebooksAsync()).ToString();
            var openedNotebook = opened is not null && TestNotebookGuard.NotebookPath(notebooks, opened) is not null;
            s.Recorder.Row("d-open", Q, "OpenHierarchy(cftNone)", new { path = "o1's folder (closed)" }, open, new { id = opened, applied = openedNotebook, fv = new { before = afterSync, after = afterOpen } });

            var target = openedNotebook ? opened! : cId;
            if (target is null)
            {
                s.Recorder.Row("d-close", Q, "CloseNotebook", null, null, new { notProbed = "neither o1 (opened) nor c is open" });
                return;
            }

            var beforeClose = await s.ViewAsync();
            var close = await s.CloseNotebookAsync(target, shownCheck: false);
            var afterClose = await s.ViewAsync();
            var stillThere = TestNotebookGuard.NotebookPath((await s.NotebooksAsync()).ToString(), target) is not null;
            s.Recorder.Row("d-close", Q, "CloseNotebook", new { target = openedNotebook ? "o1" : "c", force = false }, close, new { applied = close.Ok && !stillThere, stillOpen = stillThere, fv = new { before = beforeClose, after = afterClose } });
        }
        finally
        {
            s.ShownCheckOff = false;
            await s.PauseAsync("d-close-options", "In OneNote, close the Options dialog with Cancel. Then tell the team lead whether any notebook appeared or disappeared while it was open. Then continue.");
        }
    }

    // --- L --------------------------------------------------------------------------------------------------------

    private static async Task LateFilesAsync(AppStateContext c)
    {
        var s = c.S;
        var folders = new[] { c.RunName("o2"), c.RunName("o4"), c.EmptyFolderName, c.MarkerFolderName }
            .Select(n => (Name: n, Path: c.RunPath(n)))
            .Where(f => Directory.Exists(f.Path))
            .ToList();
        if (folders.Count == 0)
        {
            AppState.Missing(c, "l-late-files", "L", "none of the O6 and O7 folders exists", "make, not-notebook");
            return;
        }

        var latest = folders.Max(f => Directory.GetCreationTimeUtc(f.Path));
        var wait = latest + TimeSpan.FromMinutes(5) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            s.Echo(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"late-files: waiting {wait.TotalSeconds:0} s, so the listing is at least 5 min after the last create"));
            await Task.Delay(wait, ProbeSession.Ct);
        }

        foreach (var (name, path) in folders)
        {
            var files = ProbeSession.Snapshot(path);
            s.Recorder.Row($"l-{name.Replace(c.Stamp, "<stamp>", StringComparison.Ordinal)}", "L", "-", new { folder = name.Replace(c.Stamp, "<stamp>", StringComparison.Ordinal) }, null, new
            {
                minutesSinceCreate = Math.Round((DateTime.UtcNow - Directory.GetCreationTimeUtc(path)).TotalMinutes, 1),
                files,
                extensions = files.Where(f => f.Bytes >= 0).Select(f => Path.GetExtension(f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                openAsNotebook = TestNotebookGuard.NotebooksUnder((await s.NotebooksAsync()).ToString(), c.Root).Any(n => string.Equals(n.Path.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase)),
            });
        }
    }
}

/// <summary>Spike 4.1: one run's state, shared by its steps (and read live again wherever a step may run alone).</summary>
internal sealed class AppStateContext(ProbeSession s, string stamp, bool cloud)
{
    public ProbeSession S { get; } = s;

    public string Stamp { get; } = stamp;

    public bool Cloud { get; } = cloud;

    public string Root => S.RunProbeRoot!;

    /// <summary>O1's fill series runs up to 30 s locally; O2's up to 180 s on the cloud.</summary>
    public TimeSpan FillLimit => Cloud ? TimeSpan.FromSeconds(180) : TimeSpan.FromSeconds(30);

    public IReadOnlyList<string> RunTags { get; } = ["o1", "o2", "o3", "o4"];

    public string RunName(string tag) => $"{TestNotebook.ThrowawayNamePrefix}{Stamp} {tag}";

    public string RunPath(string leaf) => Path.Combine(Root, leaf);

    public string GroupName => $"probe sg {Stamp}";

    public string EmptyFolderName => $"probe empty {Stamp}";

    public string MarkerFolderName => $"probe marker {Stamp}";

    public string JunctionName => $"probe junction {Stamp}";

    /// <summary>Which of b, c and the cloud throwaway were open when the run began (by exact name), as read then: the only ones cleanup reopens.</summary>
    public Dictionary<string, NodeInfo> OpenAtStart { get; } = new(StringComparer.Ordinal);

    /// <summary>C5's grant list on the cloud, made before the close and reused after the reopen (as the local C5 does).</summary>
    public IReadOnlyList<(string Path, string Name)>? CloudGrantTargets { get; set; }

    /// <summary>C2 L's page (title and edit marker), for C4.</summary>
    public PendingPage? PendingLocal { get; set; }

    /// <summary>C2 C's page and the throwaway before its close, for O2's reopen and C5.</summary>
    public PendingPage? PendingCloud { get; set; }

    public NodeInfo? CloudBeforeClose { get; set; }

    public (int Sections, int Pages)? CloudCountsBeforeClose { get; set; }

    public IReadOnlyList<object>? CloudGrantsBefore { get; set; }

    public IReadOnlyList<object>? CloudGrantsAfterClose { get; set; }

    /// <summary>The open run notebook whose folder is <c>&lt;run folder&gt;\&lt;tag's name&gt;</c>, read live, or null.</summary>
    public async Task<string?> RunNotebookAsync(string tag)
    {
        var path = RunPath(RunName(tag));
        return TestNotebookGuard.NotebooksUnder((await S.NotebooksAsync()).ToString(), Root)
            .Where(n => string.Equals(n.Path.TrimEnd('\\', '/'), path, StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Id)
            .FirstOrDefault();
    }

    /// <summary>Throwaway b or c by its exact name (adopted again, since a reopen gives it a new ID), or null when it isn't open.</summary>
    public async Task<string?> AdoptedAsync(string exactName)
    {
        var id = await S.AdoptThrowawayAsync(exactName);
        if (id is not null && (await S.NotebookInfoAsync(id))?.Path is { } path)
        {
            S.RecordedPaths.Add(path);
        }

        return id;
    }

    /// <summary>The cloud throwaway (adopted again after a reopen), recording its URL, or null when it isn't open.</summary>
    public async Task<string?> CloudThrowawayAsync()
    {
        var id = await S.AdoptCloudThrowawayAsync();
        if (id is not null && (await S.NotebookInfoAsync(id))?.Path is { } path)
        {
            S.RecordedPaths.Add(path);
        }

        return id;
    }
}

/// <summary>Spike 4.1: a probe page with a pending edit: its title, and the marker text the edit adds.</summary>
internal sealed record PendingPage(string SectionName, string Title, string Marker);

/// <summary>A close row's result: the attempt, the notebook before, and whether it is gone from the notebook list.</summary>
internal sealed record CloseResult(Attempt Attempt, NodeInfo? Before, bool Closed);

/// <summary>An open row's result: the attempt, the ID returned, and the open notebook with that ID (null when it isn't one).</summary>
internal sealed record OpenResult(Attempt Attempt, string? Id, NodeInfo? Notebook);

/// <summary>Spike 4.1's probe steps and rows shared by the local and cloud scenarios.</summary>
internal static class AppState
{
    public const string NameB = "MCP Test Throwaway 1112-034716 b";
    public const string NameC = "MCP Test Throwaway 1112-034716 c";

    /// <summary>
    /// The run's probe folder: new, for a new stamp; or an earlier run's, when <c>ONENOTE_MCP_PROBE_STAMP</c> names it
    /// (a rerun of later steps, such as <c>cleanup</c>).
    /// </summary>
    public static AppStateContext Begin(ProbeSession s, bool cloud)
    {
        var reused = Environment.GetEnvironmentVariable("ONENOTE_MCP_PROBE_STAMP");
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ONENOTE_MCP_PROBE_STEPS")) && string.IsNullOrWhiteSpace(reused))
        {
            // Before any COM call and before any folder is made: a rerun in a new, empty folder would clean up the wrong run.
            var runs = Directory.Exists(TestNotebook.AppStateProbesDir)
                ? Directory.EnumerateDirectories(TestNotebook.AppStateProbesDir).Where(d => TestNotebookGuard.IsRunProbeRoot(d, ProbeSession.ThrowawayRoot)).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList()
                : [];
            throw new InvalidOperationException(
                "ONENOTE_MCP_PROBE_STEPS is set without ONENOTE_MCP_PROBE_STAMP. A rerun of some steps must name the run it continues, "
                + $"so set ONENOTE_MCP_PROBE_STAMP to that run's stamp, one of the run folders under probes\\app-state: {(runs.Count == 0 ? "(none)" : string.Join(", ", runs))}.");
        }

        var stamp = string.IsNullOrWhiteSpace(reused) ? HierarchyProbes.Stamp() : reused.Trim();
        s.BeginRunProbeRoot(stamp, reuse: !string.IsNullOrWhiteSpace(reused));
        s.RerunHint = $"This run's stamp is {stamp}. To rerun its remaining steps (cleanup included), set ONENOTE_MCP_PROBE_STAMP={stamp} and ONENOTE_MCP_PROBE_STEPS to those steps.";
        s.Echo(s.RerunHint);
        s.Recorder.Row("setup-run", "Setup", "-", null, null, new { stamp, root = s.RunProbeRoot, reused = !string.IsNullOrWhiteSpace(reused) });
        return new AppStateContext(s, stamp, cloud);
    }

    /// <summary>Records b's and c's fixed folders, then adopts whichever of b, c and the cloud throwaway are open, recording their live paths.</summary>
    public static async Task<bool> AdoptAsync(AppStateContext c)
    {
        var s = c.S;
        foreach (var name in new[] { NameB, NameC })
        {
            s.RecordedPaths.Add(Path.Combine(ProbeSession.ThrowawayRoot, name));
        }

        foreach (var (key, id) in new[] { (NameB, await c.AdoptedAsync(NameB)), (NameC, await c.AdoptedAsync(NameC)), (TestNotebook.CloudThrowawayName, await c.CloudThrowawayAsync()) })
        {
            if (id is not null && await s.NotebookInfoAsync(id) is { } info)
            {
                c.OpenAtStart[key] = info; // cleanup reopens only these
            }
        }

        s.Recorder.Row("setup-adopt", "Setup", "GetHierarchy", null, null, new
        {
            b = c.OpenAtStart.GetValueOrDefault(NameB),
            c = c.OpenAtStart.GetValueOrDefault(NameC),
            cloudThrowaway = c.OpenAtStart.GetValueOrDefault(TestNotebook.CloudThrowawayName),
        });
        return true;
    }

    /// <summary>A step's target is missing (a failed or filtered earlier step): a row says so.</summary>
    public static void Missing(AppStateContext c, string probe, string question, string why, string setupStep) =>
        c.S.Recorder.Row(probe, question, "-", null, null, new { notProbed = $"{why} (step {setupStep} didn't run or failed; include it in ONENOTE_MCP_PROBE_STEPS, with ONENOTE_MCP_PROBE_STAMP for an earlier run)" });

    /// <summary>S1/S2: one <c>SyncHierarchy</c>, with the focus-and-view snapshot before and after.</summary>
    public static async Task SyncRowAsync(AppStateContext c, string probe, string question, string id, string what)
    {
        var s = c.S;
        var before = await s.ViewAsync();
        var attempt = await s.SyncHierarchyAsync(id);
        var after = await s.ViewAsync();
        s.Recorder.Row(probe, question, "SyncHierarchy", new { target = what }, attempt, new { fv = new { before, after } });
    }

    /// <summary>
    /// One guarded close with its evidence: the shown check (unless <paramref name="shownCheck"/> is false), the notebook,
    /// the other notebooks' IDs and the focus-and-view snapshot before; then <paramref name="justBefore"/> (C2's edit), the
    /// close at once, and the snapshot, the notebook list and the other notebooks' IDs after. A closed run notebook's old
    /// ID joins <see cref="ProbeSession.SyncGone"/> (S2). On the cloud, the settle follows.
    /// </summary>
    public static async Task<CloseResult> CloseRowAsync(AppStateContext c, string probe, string question, string notebookId, bool shownCheck = true, object? args = null, Func<Task<object?>>? justBefore = null)
    {
        var s = c.S;
        if (shownCheck && !await s.EnsureNothingShownAsync(notebookId, "CloseNotebook"))
        {
            s.Recorder.Row(probe, question, "CloseNotebook", args, ProbeSession.NotShownRefusal, null);
            return new CloseResult(ProbeSession.NotShownRefusal, null, false);
        }

        var before = await s.NotebookInfoAsync(notebookId);
        var ids = await s.TestIdsAsync(notebookId);
        var viewBefore = await s.ViewAsync();
        var preceding = justBefore is null ? null : await justBefore();
        var attempt = await s.CloseNotebookAsync(notebookId, shownCheck: false);
        var viewAfter = await s.ViewAsync();
        var after = await s.NotebookInfoAsync(notebookId);
        if (attempt.Ok && after is null && TestNotebookGuard.IsUnderRunProbeRoot(before?.Path, c.Root))
        {
            s.SyncGone.Add(notebookId);
        }

        s.Recorder.Row(probe, question, "CloseNotebook", new { force = false, args }, attempt, new
        {
            justBefore = preceding,
            before,
            closed = after is null,
            stillOpen = after,
            fv = new { before = viewBefore, after = viewAfter },
            otherIdsKept = await s.OtherIdsKeptAsync(ids, notebookId),
            disk = ProbeSession.Disk(before?.Path),
        });
        if (c.Cloud)
        {
            await s.SettleAsync(probe, question);
        }

        return new CloseResult(attempt, before, after is null);
    }

    /// <summary>
    /// One guarded <c>OpenHierarchy</c> <c>cftNone</c> with its evidence: the focus-and-view snapshot before and after,
    /// the ID returned and what it is (an open notebook, or a node elsewhere), any notebook that appeared, any open
    /// notebook at that path, the disk (local), and, when a notebook opened, its fill series. With
    /// <paramref name="beforeClose"/> (the notebook as it was before its close), whether the ID, name and path changed.
    /// On the cloud, the settle follows.
    /// </summary>
    public static async Task<OpenResult> OpenRowAsync(
        AppStateContext c,
        string probe,
        string question,
        string path,
        string shape,
        NodeInfo? beforeClose = null,
        bool allowOpenTestNotebooks = false,
        (int Sections, int Pages)? countsBefore = null,
        object? extra = null)
    {
        var s = c.S;
        var openBefore = (await s.NotebooksAsync()).Elements(One + "Notebook").Select(n => (string?)n.Attribute("ID")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var viewBefore = await s.ViewAsync();
        var (attempt, id) = await s.OpenExistingAsync(path, allowOpenTestNotebooks);
        var viewAfter = await s.ViewAsync();
        var notebooks = (await s.NotebooksAsync()).Elements(One + "Notebook").Select(NodeInfo.From).ToList();
        var opened = id is null ? null : notebooks.FirstOrDefault(n => n.Id == id);
        var node = id is not null && opened is null ? await FindAnywhereAsync(c, id) : null;
        var appeared = notebooks.Where(n => n.Id is not null && !openBefore.Contains(n.Id)).Select(n => ProbeView.Notebook(n, c.Root)).ToList();
        var atPath = notebooks.Where(n => SamePath(n.Path, path) || SamePath(TestNotebookGuard.MapLocalhostAdminShare(n.Path), TestNotebookGuard.MapLocalhostAdminShare(path) ?? path)).Select(n => ProbeView.Notebook(n, c.Root)).ToList();
        var local = TestNotebookGuard.MapLocalhostAdminShare(path) ?? path;
        object? disk = Path.IsPathFullyQualified(local) && !local.StartsWith(@"\\", StringComparison.Ordinal)
            ? Directory.Exists(local) ? new { kind = "folder", files = ProbeSession.Snapshot(local) } : ProbeSession.Disk(local)
            : null;
        var fill = opened is not null ? await s.FillSeriesAsync(opened.Id!, c.FillLimit, countsBefore) : null;

        s.Recorder.Row(probe, question, "OpenHierarchy(cftNone)", new { shape, path }, attempt, new
        {
            id,
            opened = opened is null ? null : ProbeView.Notebook(opened, c.Root),
            node,
            appeared,
            atPath,
            vsBeforeClose = beforeClose is null || opened is null ? null : new
            {
                sameId = opened.Id == beforeClose.Id,
                sameName = opened.Name == beforeClose.Name,
                samePath = SamePath(opened.Path, beforeClose.Path),
                nameBefore = beforeClose.Name,
                pathBefore = beforeClose.Path,
            },
            countsBefore = countsBefore is { } k ? new { sections = k.Sections, pages = k.Pages } : null,
            fill,
            fv = new { before = viewBefore, after = viewAfter },
            disk,
            extra,
        });
        if (c.Cloud)
        {
            await s.SettleAsync(probe, question);
        }

        return new OpenResult(attempt, id, opened);
    }

    /// <summary>
    /// The node with <paramref name="id"/> in a full sections-scope read (a section group's ID, for O8), logged only
    /// inside a loggable notebook.
    /// </summary>
    private static async Task<object?> FindAnywhereAsync(AppStateContext c, string id)
    {
        var all = await c.S.TreeAsync(string.Empty, CoreScope.Sections);
        var e = ById(all, id);
        if (e is null)
        {
            return new { found = false };
        }

        var notebook = e.AncestorsAndSelf(One + "Notebook").FirstOrDefault();
        return notebook is not null && ProbeView.IsLoggable((string?)notebook.Attribute("name"), (string?)notebook.Attribute("path"), c.Root)
            ? new { found = true, node = NodeInfo.From(e), notebook = (string?)notebook.Attribute("name") }
            : new { found = true, node = (object)new { kind = e.Name.LocalName }, notebook = ProbeView.OtherNotebook };
    }

    /// <summary>C5's grants: <paramref name="notebookId"/>'s, and the cloud throwaway's (or b's, on the cloud) when it is open.</summary>
    public static async Task<IReadOnlyList<(string Path, string Name)>> GrantTargetsAsync(AppStateContext c, string notebookId)
    {
        var s = c.S;
        var grants = new List<(string Path, string Name)>();
        foreach (var id in new[] { notebookId, c.Cloud ? await s.AdoptThrowawayAsync(NameB) : await c.CloudThrowawayAsync() })
        {
            if (id is not null && await s.NotebookInfoAsync(id) is { Path: { } path, Name: { } name } && !grants.Any(g => g.Path == path))
            {
                grants.Add((path, name));
            }
        }

        return grants;
    }

    /// <summary>C2's setup: a section and a probe page in the notebook (they stay there: Left behind).</summary>
    public static async Task<PendingPage> PendingPageAsync(AppStateContext c, string notebookId, string tag)
    {
        var s = c.S;
        var sectionName = $"probe pend {tag} {c.Stamp}";
        var section = await s.MustCreateChildAsync(notebookId, sectionName + ".one", CreateFileType.cftSection);
        var title = $"probe pend {tag} p {c.Stamp}";
        await s.CreateProbePageAsync(section, title);
        return new PendingPage(sectionName, title, $"probe pending edit {tag} {c.Stamp}");
    }

    /// <summary>C2's edit: one new outline holding the marker, sent with the page's own date (never <c>DateTime.MinValue</c>).</summary>
    public static async Task<object?> EditAsync(AppStateContext c, string notebookId, PendingPage pending)
    {
        var s = c.S;
        var page = (await s.FindByNameAsync(notebookId, pending.Title)).FirstOrDefault(n => !n.InRecycleBin)?.Id
            ?? throw new InvalidOperationException($"The page {pending.Title} isn't in the notebook.");
        var date = ParseUtc(Lmt(await s.PageAsync(page)) ?? throw new InvalidOperationException("The page has no lastModifiedTime."));
        var edit = await s.TryWriteAnyAsync(page, "UpdatePageContent(pending edit)", app => app.UpdatePageContent(Page(page, Outline(null, Oe(null, pending.Marker))), date, XMLSchema.xs2013, force: false));
        await s.CheckTimeoutAsync("UpdatePageContent-pending", edit);
        return new { edit.Outcome, edit.HResult, edit.HResultName, edit.Ms };
    }

    /// <summary>After a reopen: whether the page with the pending edit is there, and whether its text still holds the marker.</summary>
    public static async Task<object> EditSurvivedAsync(AppStateContext c, string notebookId, PendingPage pending)
    {
        var s = c.S;
        var page = (await s.FindByNameAsync(notebookId, pending.Title)).FirstOrDefault(n => !n.InRecycleBin)?.Id;
        if (page is null)
        {
            return new { pageFound = false };
        }

        var xml = await s.PageXmlAsync(page);
        return new { pageFound = true, editSurvived = xml.Contains(pending.Marker, StringComparison.Ordinal) };
    }

    /// <summary>
    /// C2 (and C3): the edit and the close at once, then the result (closes, refuses, or Misplaced Sections; a blocked
    /// close stops the scenario at its timeout instead), and, if it closed, the reopen by its recorded path and whether
    /// the edit survived. Local only: the cloud reopen is its own step (O2).
    /// </summary>
    public static async Task<CloseResult> PendingCloseAsync(AppStateContext c, string probe, string question, string notebookId, NodeInfo info, PendingPage pending, string? adoptedName)
    {
        var s = c.S;
        var close = await CloseRowAsync(c, $"{probe}-close", question, notebookId, shownCheck: false, args: new { notebook = adoptedName is null ? "cloud throwaway" : "c", pending = "an edit made just before" }, justBefore: () => EditAsync(c, notebookId, pending));
        var misplaced = ProbeView.Misplaced(await s.TreeAsync(string.Empty, CoreScope.Sections), c.Root);
        var result = close.Attempt.Outcome is "refused" or "not-run" ? "guardRefused" // our guard, before any COM call: not OneNote refusing
            : close.Attempt.Ok && close.Closed ? "closes"
            : !close.Attempt.Ok && !close.Closed ? "refuses"
            : close.Attempt.Ok ? "ok but still open" : "error, yet closed";
        s.Recorder.Row(probe, question, "CloseNotebook", null, close.Attempt, new { result, misplaced });

        if (adoptedName is not null && close.Closed)
        {
            await OpenRowAsync(c, $"{probe}-reopen", question, info.Path!, "the notebook's folder, closed with an edit pending", info);
            if (await c.AdoptedAsync(adoptedName) is { } reopened)
            {
                s.Recorder.Row($"{probe}-edit-survived", question, "GetPageContent", null, null, await EditSurvivedAsync(c, reopened, pending));
            }
        }

        return close;
    }

    /// <summary>
    /// The plan's Sync Now check after a cloud step, worded from <c>CloudJamRepro.CheckSyncAsync</c> and widened to the
    /// cloud throwaway. <c>0xE0000644</c> is reported at once.
    /// </summary>
    public static Task<bool> CheckSyncAsync(ProbeSession s, string step, string what) => s.PauseAsync(
        $"{step}-check-sync",
        $"In OneNote open File > Info > View Sync Status, press Sync Now on {TestNotebook.CloudNotebookName} and on {TestNotebook.CloudThrowawayName} (if it is open), "
        + $"wait about 30 seconds and expand both. Tell the team lead whether each says \"Up to date\" or shows an error (with its code; report 0xE0000644 at once), "
        + $"and whether {what} shows in OneNote for the web. Then continue.");

    /// <summary>
    /// <c>cleanup</c>: closes every notebook open from the run's probe folder (after the pause saying which), removes
    /// the junctions in it without following them, deletes the folder only once a live read shows nothing open under
    /// it, and makes sure b and c (local) and the cloud throwaway are open again, as they were found.
    /// </summary>
    public static async Task CleanupAsync(AppStateContext c)
    {
        const string Q = "Cleanup";
        var s = c.S;
        var open = TestNotebookGuard.NotebooksUnder((await s.NotebooksAsync()).ToString(), c.Root);
        if (open.Count > 0)
        {
            await s.ObservePauseAsync("cleanup", $"these notebooks disappear from the notebook list: {string.Join(", ", open.Select(n => ProbeSession.Label(n.Name, n.Path)))}.");
            foreach (var (id, _, _) in open)
            {
                await CloseRowAsync(c, "cleanup-close", Q, id);
            }
        }

        var removed = new List<string>();
        if (Directory.Exists(c.Root))
        {
            foreach (var dir in Directory.EnumerateDirectories(c.Root).Where(Junctions.IsJunction).ToList())
            {
                if (s.RemoveJunction(dir))
                {
                    removed.Add(Path.GetFileName(dir));
                }
            }
        }

        var stillOpen = TestNotebookGuard.NotebooksUnder((await s.NotebooksAsync()).ToString(), c.Root);
        string folder;
        if (stillOpen.Count > 0)
        {
            folder = "kept: notebooks are still open under it";
        }
        else if (!Directory.Exists(c.Root))
        {
            folder = "already gone";
        }
        else
        {
            try
            {
                Directory.Delete(c.Root, recursive: true);
                folder = "deleted";
            }
            catch (IOException ex)
            {
                folder = $"kept: {ex.GetType().Name}";
            }
            catch (UnauthorizedAccessException ex)
            {
                folder = $"kept: {ex.GetType().Name}";
            }
        }

        s.Recorder.Row("cleanup-folder", Q, "-", null, null, new
        {
            junctionsRemoved = removed.Select(r => r.Replace(c.Stamp, "<stamp>", StringComparison.Ordinal)).ToList(),
            o1FolderAfterJunctionRemoved = removed.Count == 0 ? null : (bool?)Directory.Exists(c.RunPath(c.RunName("o1"))),
            stillOpen = stillOpen.Select(n => n.Name).ToList(),
            folder,
        });

        // Reopen only what was open when the run began, named as it was read then. The local scenario never touches the
        // cloud throwaway (no settle, no Sync Now check, a 60 s limit there): if it is closed, that is logged and left.
        if (!c.Cloud)
        {
            foreach (var name in new[] { NameB, NameC })
            {
                if (c.OpenAtStart.TryGetValue(name, out var was) && await c.AdoptedAsync(name) is null && was.Path is { } path)
                {
                    await s.ObservePauseAsync($"cleanup-reopen-{name[^1]}", $"the notebook {ProbeSession.Label(was.Name, path)} comes back to the notebook list (it was open when this run began).");
                    await OpenRowAsync(c, $"cleanup-reopen-{name[^1]}", Q, path, $"throwaway {name[^1]}'s folder", was);
                    await c.AdoptedAsync(name);
                }
            }

            if (c.OpenAtStart.ContainsKey(TestNotebook.CloudThrowawayName) && await c.CloudThrowawayAsync() is null)
            {
                s.Recorder.Row("cleanup-cloud-throwaway", Q, "-", null, null, new { closed = true, note = "left closed: app-state-local never opens the cloud throwaway" });
            }
        }
        else if (c.OpenAtStart.TryGetValue(TestNotebook.CloudThrowawayName, out var cloudWas) && await c.CloudThrowawayAsync() is null)
        {
            if (cloudWas.Path is { } url)
            {
                await s.ObservePauseAsync("cleanup-reopen-cloud", $"the notebook {ProbeSession.Label(cloudWas.Name, url)} comes back to the notebook list (opened from its OneDrive URL; it was open when this run began).");
                await OpenRowAsync(c, "cleanup-reopen-cloud", Q, url, "the cloud throwaway's URL", cloudWas);
            }

            if (await c.CloudThrowawayAsync() is null)
            {
                await s.PauseAsync(
                    "cleanup-reopen-cloud-by-hand",
                    $"The notebook \"{cloudWas.Name}\" isn't open. In OneNote, open it: File > Open > OneDrive > Documents > \"{cloudWas.Name}\", and wait until it has synced. Then continue.");
            }
        }

        // An earlier run's folder with notebooks still open: this run can't close them (the guard keys on this run's folder).
        var others = new List<string>();
        if (Directory.Exists(TestNotebook.AppStateProbesDir))
        {
            var notebooks = (await s.NotebooksAsync()).ToString();
            foreach (var dir in Directory.EnumerateDirectories(TestNotebook.AppStateProbesDir).Where(d => !string.Equals(d, c.Root, StringComparison.OrdinalIgnoreCase)))
            {
                if (TestNotebookGuard.IsRunProbeRoot(dir, ProbeSession.ThrowawayRoot) && TestNotebookGuard.NotebooksUnder(notebooks, dir).Count > 0)
                {
                    others.Add(Path.GetFileName(dir));
                }
            }
        }

        s.Recorder.Row("cleanup-state", Q, "GetHierarchy", null, null, new
        {
            runNotebooksOpen = TestNotebookGuard.NotebooksUnder((await s.NotebooksAsync()).ToString(), c.Root).Count,
            b = c.Cloud ? null : (bool?)(await s.AdoptThrowawayAsync(NameB) is not null),
            c = c.Cloud ? null : (bool?)(await s.AdoptThrowawayAsync(NameC) is not null),
            cloudThrowaway = await c.CloudThrowawayAsync() is not null,
            folderExists = Directory.Exists(c.Root),
            earlierRunsWithOpenNotebooks = others.Count == 0 ? null : new { stamps = others, rerun = "set ONENOTE_MCP_PROBE_STAMP=<stamp> and ONENOTE_MCP_PROBE_STEPS=cleanup" },
        });
        await s.ReportPauseAsync("cleanup", c.Cloud
            ? $"confirm that \"{TestNotebook.CloudThrowawayName}\" is open and that no notebook of this run is left in the notebook list."
            : $"confirm that \"{NameB}\" and \"{NameC}\" are open, and that no notebook named \"{TestNotebook.ThrowawayNamePrefix}{c.Stamp} …\" is left in the notebook list.");
        if (c.Cloud)
        {
            await CheckSyncAsync(s, "cleanup", $"{TestNotebook.CloudThrowawayName} and its sections");
        }
    }

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
