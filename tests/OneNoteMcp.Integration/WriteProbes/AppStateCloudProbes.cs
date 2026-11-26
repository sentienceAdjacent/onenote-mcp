using Xunit;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Spike 4.1, scenario <c>app-state-cloud</c> (sitting B): <c>SyncHierarchy</c> on MCP Test Cloud and the cloud throwaway
/// (S1 C), <c>OpenHierarchy</c> <c>cftNone</c> on open MCP Test Cloud (O4 C) and on run-stamped OneDrive URLs (O3), and
/// <c>CloseNotebook</c> (never <c>force</c>) on <c>MCP Test Throwaway Cloud</c> with an edit pending (C2 C, C5), its reopen
/// by URL (O2), and the same close offline (C3). A Sync Now check follows each step, and a settle follows every cloud
/// close and open (spike 3.1, Implication 16). Calls time out at 180 s.
/// </summary>
[Collection(ThrowawayOpenState.Name)]
public sealed class AppStateCloudProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "app-state-cloud";

    [Fact]
    public async Task AppStateCloud()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.CloudProbeCallSeconds);
        var c = AppState.Begin(s, cloud: true);
        await s.SetupAsync("setup", s.CloudIdAsync);
        await s.SetupAsync("setup", () => AppState.AdoptAsync(c));

        await s.StepAsync("sync-cloud", () => SyncCloudAsync(c));
        await s.StepAsync("already-open-cloud", () => AlreadyOpenCloudAsync(c));
        await s.StepAsync("url-create", () => UrlCreateAsync(c));
        await s.StepAsync("pending-cloud", () => PendingCloudAsync(c));
        await s.StepAsync("reopen-url", () => ReopenUrlAsync(c));
        await s.StepAsync("offline", () => OfflineAsync(c));
        await s.StepAsync("cleanup", () => AppState.CleanupAsync(c));

        s.Finish();
    }

    // --- S1 C -----------------------------------------------------------------------------------------------------

    private static async Task SyncCloudAsync(AppStateContext c)
    {
        var s = c.S;
        await AppState.SyncRowAsync(c, "s1-cloud-mcp-test-cloud", "S1", await s.CloudIdAsync(), "the MCP Test Cloud notebook");
        if (await c.CloudThrowawayAsync() is { } throwaway)
        {
            await AppState.SyncRowAsync(c, "s1-cloud-throwaway", "S1", throwaway, "the cloud throwaway notebook");
        }
        else
        {
            AppState.Missing(c, "s1-cloud-throwaway", "S1", "the cloud throwaway isn't open", "setup");
        }

        await s.ReportPauseAsync("sync-cloud", "any sync icon, progress bar, or dialog while the two cloud notebooks were synced.");
        await AppState.CheckSyncAsync(s, "sync-cloud", $"{TestNotebook.CloudNotebookName} > Probes");
    }

    // --- O4 C -----------------------------------------------------------------------------------------------------

    private static async Task AlreadyOpenCloudAsync(AppStateContext c)
    {
        var s = c.S;
        var cloud = await s.CloudIdAsync();
        var info = await s.NotebookInfoAsync(cloud);
        if (info?.Path is not { } url)
        {
            AppState.Missing(c, "o4-cloud", "O4", "MCP Test Cloud has no path", "setup");
            return;
        }

        await s.PauseAsync("already-open-cloud-show", $"In OneNote, click the page {ProbeSession.SafePage(cloud: true)}, so that it is the page shown. Don't type. Then continue.");
        await AppState.OpenRowAsync(c, "o4-cloud-mcp-test-cloud", "O4", url, "MCP Test Cloud's URL, while it is open", info, allowOpenTestNotebooks: true);
        await s.ReportPauseAsync("already-open-cloud", $"whether OneNote switched to the notebook {TestNotebook.CloudNotebookName} (and which page it shows), or stayed on {ProbeSession.SafePage(cloud: true)}.");
        await AppState.CheckSyncAsync(s, "already-open-cloud", $"{TestNotebook.CloudNotebookName} > Probes");
    }

    // --- O3 -------------------------------------------------------------------------------------------------------

    private static async Task UrlCreateAsync(AppStateContext c)
    {
        var s = c.S;
        var cid = TestNotebookGuard.CloudCid((await s.NotebooksAsync()).ToString());
        if (cid is null)
        {
            AppState.Missing(c, "o3", "O3", "MCP Test Cloud isn't open, so its account ID is unknown", "setup");
            return;
        }

        var missing = $"probe missing {c.Stamp}";
        var empty = $"probe empty {c.Stamp}";
        var missingRow = await AppState.OpenRowAsync(c, "o3-missing", "O3", Url(cid, missing), "a OneDrive URL with no folder behind it");

        var made = await s.PauseAsync(
            "url-create-make-empty",
            $"In a browser, open OneDrive > Documents and create a new, empty folder named exactly \"{empty}\". Put nothing in it. Then continue. "
            + "The probe then opens that folder's URL in OneNote; watch the notebook list, since a notebook may appear.");
        OpenResult? emptyRow = null;
        if (made)
        {
            emptyRow = await AppState.OpenRowAsync(c, "o3-empty", "O3", Url(cid, empty), "a OneDrive URL of an empty folder Phill made");
        }
        else
        {
            s.Recorder.Row("o3-empty", "O3", "OpenHierarchy(cftNone)", null, null, new { notProbed = "pause skipped: the empty OneDrive folder wasn't made" });
        }

        // Phill 2026-11-16: a notebook opened from this run's stamped probe URL is closed by the guarded close
        // (TestNotebookGuard.CheckClose); a pause asks Phill to close it by hand only if that close fails.
        var opened = new[] { (Probe: "o3-missing", Row: missingRow), (Probe: "o3-empty", Row: emptyRow) }
            .Where(o => o.Row?.Notebook is not null)
            .Select(o => (o.Probe, Notebook: o.Row!.Notebook!))
            .ToList();
        var stillOpen = new List<NodeInfo>();
        foreach (var (probe, notebook) in opened)
        {
            var close = await AppState.CloseRowAsync(c, $"{probe}-close", "O3", notebook.Id!, args: new { notebook = "opened from a probe URL" });
            if (!close.Closed)
            {
                stillOpen.Add(notebook);
            }
        }

        if (stillOpen.Count > 0)
        {
            var names = string.Join(", ", stillOpen.Select(n => ProbeSession.Label(n.Name, n.Path)));
            await s.PauseAsync(
                "url-create-close-by-hand",
                $"The probe couldn't close the notebook OneNote opened from a probe URL: {names}. In OneNote, right-click it in the notebook list and choose Close This Notebook. "
                + "Close nothing else. Then continue.");
            s.Recorder.Row("o3-closed-by-hand", "O3", "-", null, null, new { stillOpen = (await s.NotebooksAsync()).Elements(ProbeSession.One + "Notebook").Count(n => stillOpen.Any(o => o.Id == (string?)n.Attribute("ID"))) });
        }

        await s.PauseAsync(
            "url-create-web-check",
            $"In a browser, open OneDrive > Documents. Tell the team lead whether a folder \"{missing}\" now exists, and what \"{empty}\" holds (any files, such as a .onetoc2 file). "
            + $"Then delete the folder \"{empty}\", and \"{missing}\" if it appeared, so they go to the OneDrive recycle bin. Then continue.");
        await AppState.CheckSyncAsync(s, "url-create", $"{TestNotebook.CloudNotebookName} > Probes");
    }

    /// <summary>O3's URL: <c>https://d.docs.live.net/&lt;cid&gt;/Documents/&lt;folder&gt;/</c>, spelled as OneNote spells cloud paths.</summary>
    internal static string Url(string cid, string folder) => $"https://d.docs.live.net/{cid}/Documents/{folder}/";

    // --- C2 C, C5 -------------------------------------------------------------------------------------------------

    private static async Task PendingCloudAsync(AppStateContext c)
    {
        var s = c.S;
        if (await c.CloudThrowawayAsync() is not { } id || await s.NotebookInfoAsync(id) is not { } info)
        {
            AppState.Missing(c, "c2-cloud", "C2", "the cloud throwaway isn't open", "setup");
            return;
        }

        var pending = await AppState.PendingPageAsync(c, id, "cloud");
        c.PendingCloud = pending;
        c.CloudBeforeClose = info;
        c.CloudCountsBeforeClose = FillSchedule.Count(await s.TreeAsync(id));
        var grants = await AppState.GrantTargetsAsync(c, id);
        c.CloudGrantTargets = grants; // reused after the reopen, so a path change shows
        c.CloudGrantsBefore = await s.ProbeGrantsAsync(grants);
        if (!await s.EnsureNothingShownAsync(id, "CloseNotebook"))
        {
            s.Recorder.Row("c2-cloud", "C2", "CloseNotebook", null, ProbeSession.NotShownRefusal, null);
            await AppState.CheckSyncAsync(s, "pending-cloud", $"the section \"{pending.SectionName}\" in \"{info.Name}\""); // the setup wrote to the cloud
            return;
        }

        await s.ObservePauseAsync(
            "pending-cloud",
            $"the notebook \"{info.Name}\" (on OneDrive) disappears from the notebook list. If a dialog appears and stays, tell the team lead its title and text, then press Cancel.");
        await AppState.PendingCloseAsync(c, "c2-cloud", "C2", id, info, pending, adoptedName: null);
        c.CloudGrantsAfterClose = await s.ProbeGrantsAsync(grants);
        await s.ReportPauseAsync(
            "pending-cloud",
            "any dialog (its title and text, and whether you pressed Cancel), and whether OneNote now shows a \"Misplaced Sections\" entry (in the notebook list, or under File > Info). Look, don't open it.");
        await AppState.CheckSyncAsync(s, "pending-cloud", $"the section \"{pending.SectionName}\" in \"{info.Name}\"");
    }

    // --- O2 (and C2 C's reopen, C5's last read) -------------------------------------------------------------------

    private static async Task ReopenUrlAsync(AppStateContext c)
    {
        var s = c.S;
        if (await c.CloudThrowawayAsync() is { } already)
        {
            s.Recorder.Row("o2", "O2", "-", null, null, new { notProbed = "the cloud throwaway is already open (pending-cloud didn't close it)", id = already });
            return;
        }

        // Named as recorded before its close (never from the constant): 3.1's "name the exact notebook" lesson.
        var was = c.CloudBeforeClose ?? c.OpenAtStart.GetValueOrDefault(TestNotebook.CloudThrowawayName);
        if (was?.Path is not { } url)
        {
            AppState.Missing(c, "o2", "O2", "the cloud throwaway's URL wasn't recorded", "setup, pending-cloud");
            return;
        }

        await s.ObservePauseAsync("reopen-url", $"the notebook {ProbeSession.Label(was.Name, url)} comes back to the notebook list, opened from its OneDrive URL. It may take up to 3 minutes to fill.");
        var open = await AppState.OpenRowAsync(c, "o2-reopen-url", "O2", url, "the cloud throwaway's URL, closed moments ago", c.CloudBeforeClose, countsBefore: c.CloudCountsBeforeClose);
        var byHand = false;
        if (open.Notebook is null)
        {
            byHand = await s.PauseAsync(
                "reopen-url-by-hand",
                $"The probe couldn't open \"{was.Name}\" from its URL. In OneNote, open it: File > Open > OneDrive > Documents > \"{was.Name}\", and wait until it has synced. Then continue.");
        }

        var reopened = await c.CloudThrowawayAsync();
        s.Recorder.Row("o2-result", "O2", "-", null, null, new { openedByUrl = open.Notebook is not null, reopenedByPhill = byHand && open.Notebook is null, open = reopened is not null });

        if (reopened is not null && c.PendingCloud is { } pending)
        {
            s.Recorder.Row("c2-cloud-edit-survived", "C2", "GetPageContent", null, null, await AppState.EditSurvivedAsync(c, reopened, pending));
        }

        // The grants made before the close, keyed by the old path: a reopen that changed the path shows as notebookNotOpen.
        var grants = c.CloudGrantTargets ?? [];
        s.Recorder.Row("c5-cloud", "C5", "list_grants", new { grants = grants.Count }, null, new
        {
            before = c.CloudGrantsBefore,
            afterClose = c.CloudGrantsAfterClose,
            afterReopen = grants.Count == 0 ? null : await s.ProbeGrantsAsync(grants),
            note = grants.Count == 0 ? "no grant list: step pending-cloud didn't run in this process" : null,
        });
        await s.ReportPauseAsync("reopen-url", "whether the notebook came back on its own, how long it took to show its sections and pages, and any dialog.");
        await AppState.CheckSyncAsync(s, "reopen-url", $"\"{was.Name}\" and its sections");
    }

    // --- C3 -------------------------------------------------------------------------------------------------------

    private static async Task OfflineAsync(AppStateContext c)
    {
        var s = c.S;
        if (await c.CloudThrowawayAsync() is not { } id || await s.NotebookInfoAsync(id) is not { } info)
        {
            AppState.Missing(c, "c3", "C3", "the cloud throwaway isn't open", "reopen-url");
            return;
        }

        var pending = await AppState.PendingPageAsync(c, id, "offline");
        await s.SettleAsync("c3-setup", "C3");
        var what = $"the section \"{pending.SectionName}\" in \"{info.Name}\", with its page's edit";
        if (!await s.EnsureNothingShownAsync(id, "CloseNotebook"))
        {
            s.Recorder.Row("c3", "C3", "CloseNotebook", null, ProbeSession.NotShownRefusal, null);
            await AppState.CheckSyncAsync(s, "offline", what); // the setup wrote to the cloud
            return;
        }

        if (!await s.PauseAsync(
                "offline-network-off",
                "Turn this computer's network off now (Wi-Fi off, or unplug the cable), for about 2 minutes. Leave OneNote running. "
                + $"Then continue, and watch OneNote without clicking in it: the probe edits a page and closes the notebook \"{info.Name}\" (on OneDrive), so it should disappear from the notebook list. "
                + "If a dialog appears and stays, tell the team lead its title and text, then press Cancel."))
        {
            s.Recorder.Row("c3", "C3", "-", null, null, new { notProbed = "pause skipped: the network wasn't turned off" });
            await AppState.CheckSyncAsync(s, "offline", what); // the setup wrote to the cloud
            return;
        }

        var close = await AppState.PendingCloseAsync(c, "c3-offline", "C3", id, info, pending, adoptedName: null);
        await s.PauseAsync(
            "offline-network-on",
            "Tell the team lead what OneNote showed while offline (any dialog, error or sync icon). Then turn the network back on, and wait until this computer is online again. "
            + (close.Closed ? $"After this pause, the notebook \"{info.Name}\" comes back to the notebook list, opened from its OneDrive URL. " : string.Empty)
            + "Then continue.");

        if (close.Closed && info.Path is { } url)
        {
            var open = await AppState.OpenRowAsync(c, "c3-reopen", "C3", url, "the cloud throwaway's URL, closed while offline", info);
            if (open.Notebook is null)
            {
                await s.PauseAsync(
                    "offline-reopen-by-hand",
                    $"The probe couldn't open \"{info.Name}\" from its URL. In OneNote, open it: File > Open > OneDrive > Documents > \"{info.Name}\", and wait until it has synced. Then continue.");
            }
        }

        if (await c.CloudThrowawayAsync() is { } reopened)
        {
            s.Recorder.Row("c3-edit-survived", "C3", "GetPageContent", null, null, await AppState.EditSurvivedAsync(c, reopened, pending));
        }

        await s.ReportPauseAsync("offline", "whether the notebook came back, and any dialog after the network came back.");
        await AppState.CheckSyncAsync(s, "offline", what);
    }
}
