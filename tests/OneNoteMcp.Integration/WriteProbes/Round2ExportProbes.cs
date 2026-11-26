using System.Globalization;
using System.Xml.Linq;
using OneNoteMcp.Com.Interop;
using Xunit;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Spike 3.1, scenario <c>write-probes-r2-local</c> (sitting A): exports, packages, <c>MergeFiles</c>,
/// <c>MergeSections</c>, moves and recycle-bin restores on MCP Test, inside the run's own <c>probe run &lt;stamp&gt;</c>
/// group, with <c>MCP Test Throwaway 1112-034716 a</c> (Q3 a, adopted by its exact name) as the other notebook. Matrix
/// rows X1, X2, O1–O4, F1–F3, S1–S3, M1–M4, R1–R3, R5 and D. Hitl: File &gt; Options for D, then closing what the
/// probes opened.
/// </summary>
[Collection(ThrowawayOpenState.Name)]
public sealed class Round2LocalProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "write-probes-r2-local";

    [Fact]
    public async Task WriteProbesR2Local()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.ProbeCallSeconds);
        var t = new ProbeTarget("local", await s.SetupAsync("setup", s.McpTestIdAsync), TestNotebook.NotebookName, Cloud: false);
        var c = await Round2.StartAsync(s, t);

        // Steps in the plan's order: export, open-package, merge-files, merge-sections, move, restore, then modal.
        await s.StepAsync("export", () => Round2ExportProbes.ExportAsync(c));
        await s.StepAsync("open-package", () => Round2ExportProbes.OpenPackageAsync(c));
        await s.StepAsync("merge-files", () => Round2MergeProbes.MergeFilesAsync(c));
        await s.StepAsync("merge-sections", () => Round2MergeProbes.MergeSectionsAsync(c));
        await s.StepAsync("move", () => Round2MoveProbes.MoveAsync(c));
        await s.StepAsync("restore", () => Round2RestoreProbes.RestoreAsync(c));
        await s.StepAsync("modal-options", () => Round2MoveProbes.ModalAsync(c));
        await s.StepAsync("close-opened", () => Round2.CloseOpenedAsync(c));

        s.Finish();
    }
}

/// <summary>
/// Spike 3.1, scenario <c>write-probes-r2-cloud</c> (sitting B): the same probes on MCP Test Cloud, with
/// <c>MCP Test Throwaway Cloud</c> (Q2 a) as the other notebook, then the cross-storage moves and merges between MCP Test
/// and MCP Test Cloud (Q1 a) last. Matrix rows X1, X2, F1, S1–S3, M1–M4, R1–R3 and R5. A Sync Now check follows every
/// step, and a OneNote-for-the-web check follows M4.
/// </summary>
[Collection(ThrowawayOpenState.Name)]
public sealed class Round2CloudProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "write-probes-r2-cloud";

    [Fact]
    public async Task WriteProbesR2Cloud()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.CloudProbeCallSeconds);
        var t = new ProbeTarget("cloud", await s.SetupAsync("setup", s.CloudIdAsync), TestNotebook.CloudNotebookName, Cloud: true);
        var c = await Round2.StartAsync(s, t);

        await s.StepAsync("export", HierarchyProbes.SyncChecked(s, t, "export", c.Stamp, () => Round2ExportProbes.ExportAsync(c)));
        await s.StepAsync("merge-files", HierarchyProbes.SyncChecked(s, t, "merge-files", c.Stamp, () => Round2MergeProbes.MergeFilesAsync(c)));
        await s.StepAsync("merge-sections", HierarchyProbes.SyncChecked(s, t, "merge-sections", c.Stamp, () => Round2MergeProbes.MergeSectionsAsync(c)));
        await s.StepAsync("move", HierarchyProbes.SyncChecked(s, t, "move", c.Stamp, () => Round2MoveProbes.MoveAsync(c)));
        await s.StepAsync("restore", HierarchyProbes.SyncChecked(s, t, "restore", c.Stamp, () => Round2RestoreProbes.RestoreAsync(c)));
        await s.StepAsync("cross-storage", HierarchyProbes.SyncChecked(s, t, "cross-storage", c.Stamp, () => Round2MoveProbes.CrossStorageAsync(c)));
        await s.StepAsync("close-opened", () => Round2.CloseOpenedAsync(c));

        s.Finish();
    }
}

/// <summary>One Round2 pass: the session, its notebook and run group, the other notebook, and what earlier steps learnt.</summary>
internal sealed class Round2Context(ProbeSession s, ProbeTarget t, string run, string stamp)
{
    public ProbeSession S { get; } = s;

    public ProbeTarget T { get; } = t;

    /// <summary>The run's <c>probe run &lt;stamp&gt;</c> section group in <see cref="T"/>.</summary>
    public string Run { get; } = run;

    public string Stamp { get; } = stamp;

    /// <summary>The other notebook (the local throwaway, the cloud throwaway, or the protected pass's own throwaway), or null.</summary>
    public ProbeTarget? Other { get; set; }

    /// <summary>M1(b)'s answer: true when a partial page list kept the page it left out. Null until M1(b) ran this run.</summary>
    public bool? OmittedPagesKept { get; set; }

    /// <summary>M1's working shape: "a" (one call) or "c" (OneMore's two calls). Null until M1 ran this run, or when neither moved the page.</summary>
    public string? MoveShape { get; set; }

    /// <summary>What the probes opened in OneNote (backups, unpacked packages), for the closing pause.</summary>
    public List<string> ToClose { get; } = [];
}

/// <summary>Spike 3.1's shared steps and helpers.</summary>
internal static class Round2
{
    /// <summary>Q3 a: the local throwaway from run 4 (spike 5.1, <c>write-probes-notebook-delete-20261112-034716</c>), adopted by exact name.</summary>
    public const string LocalThrowawayName = "MCP Test Throwaway 1112-034716 a";

    /// <summary>The run group, then the other notebook (Q2, Q3): adopted if open, else Phill opens it at a pause.</summary>
    public static async Task<Round2Context> StartAsync(ProbeSession s, ProbeTarget t)
    {
        var stamp = HierarchyProbes.Stamp();
        var run = await s.SetupAsync("run-group", () => HierarchyProbes.RunGroupAsync(s, t, stamp));
        var c = new Round2Context(s, t, run, stamp);
        c.Other = await s.SetupAsync("throwaway", () => AdoptOtherAsync(s, t.Cloud));
        s.Recorder.Row("throwaway-adopt", "Setup", "GetHierarchy", null, null, new { found = c.Other is not null, c.Other?.NotebookName });
        return c;
    }

    internal static async Task<ProbeTarget?> AdoptOtherAsync(ProbeSession s, bool cloud)
    {
        var name = cloud ? TestNotebook.CloudThrowawayName : LocalThrowawayName;
        async Task<string?> AdoptAsync() => cloud ? await s.AdoptCloudThrowawayAsync() : await s.AdoptThrowawayAsync(name);
        var id = await AdoptAsync();
        if (id is null)
        {
            var where = cloud
                ? $"File > Open > OneDrive > Documents > \"{name}\""
                : $"File > Open > Browse, the folder \"{name}\" in %LOCALAPPDATA%\\OneNoteMcp-Test, then its \"Open Notebook\" file";
            if (await s.PauseAsync(
                $"open-{(cloud ? "cloud" : "local")}-throwaway",
                $"The notebook \"{name}\" isn't open in OneNote. Open it ({where}) and wait until it shows. Don't change anything in it. Then continue. "
                + "If you can't find it, replace this file's content with skip: the cross-notebook probes then log notProbed."))
            {
                id = await AdoptAsync();
            }
        }

        return id is null ? null : new ProbeTarget(cloud ? "cloud-throwaway" : "local-throwaway", id, name, cloud);
    }

    /// <summary>One pause naming everything the probes opened (sitting script: close the -unpacked notebooks and the opened backups).</summary>
    public static async Task CloseOpenedAsync(Round2Context c)
    {
        if (c.ToClose.Count == 0)
        {
            c.S.Recorder.Row("close-opened", "Setup", "-", null, null, new { nothingOpened = true });
            return;
        }

        await c.S.PauseAsync(
            "close-opened",
            $"The probes opened these copies in OneNote from the publish folder (%LOCALAPPDATA%\\OneNoteMcp-Test\\probes\\publish): {string.Join("; ", c.ToClose.Distinct().Select(n => $"\"{n}\""))}. "
            + $"Each copy shows outside the test notebooks, under Open Sections or Misc, or as a separate notebook. A copied section has the same name as a live section inside \"probe run {c.Stamp}\": "
            + "close only the copy, never the live section of the same name in a test notebook. Close each copy: right-click it > Close This Section (or Close This Notebook). "
            + "Don't delete them, and don't close anything else. Then continue.");
    }

    // --- Setup ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A section of this run under <paramref name="parentId"/> with a page per title (title only plus one line of
    /// "probe text"). With <paramref name="lastIsSubpage"/>, the last page becomes a subpage of the page before it (<see cref="MakeSubpageAsync"/>).
    /// </summary>
    public static async Task<(string Section, List<string> Pages)> SectionAsync(Round2Context c, string parentId, string name, IReadOnlyList<string> titles, bool lastIsSubpage = false)
    {
        var section = await c.S.MustCreateChildAsync(parentId, name + ".one", CreateFileType.cftSection);
        var pages = new List<string>();
        foreach (var title in titles)
        {
            pages.Add(await c.S.CreateProbePageAsync(section, title));
        }

        if (lastIsSubpage && pages.Count >= 2)
        {
            await MakeSubpageAsync(c, section, pages, pages.Count - 1, $"setup-subpage-{name}");
        }

        return (section, pages);
    }

    /// <summary>
    /// Makes <paramref name="pages"/>[<paramref name="index"/>] a subpage (<c>pageLevel</c> 2) of the page before it:
    /// first by <c>UpdatePageContent</c> with the attribute on the page root (the page XML's own date), then, if that
    /// didn't take, by <c>UpdateHierarchy</c> listing the section's pages in order. Logs which worked.
    /// </summary>
    public static async Task MakeSubpageAsync(Round2Context c, string sectionId, IReadOnlyList<string> pages, int index, string probe)
    {
        var s = c.S;
        var pageId = pages[index];
        var date = ParseUtc(Lmt(await s.PageAsync(pageId)) ?? throw new InvalidOperationException("No page lastModifiedTime."));
        var viaPage = await s.TryWriteAnyAsync(
            pageId,
            "UpdatePageContent(pageLevel)",
            app => app.UpdatePageContent(PageLevelXml(pageId, 2), date, XMLSchema.xs2013, force: false));
        await s.CheckTimeoutAsync("UpdatePageContent-pageLevel", viaPage);
        var level = await PageLevelAsync(s, sectionId, pageId);
        Attempt? viaHierarchy = null;
        if (level != "2")
        {
            viaHierarchy = await s.UpdateHierarchyAsync(Hierarchy("Section", sectionId, string.Empty, pages.Select((id, i) => Node("Page", id, i == index ? A("pageLevel", "2") : string.Empty)).ToArray()));
            level = await PageLevelAsync(s, sectionId, pageId);
        }

        s.Recorder.Row(probe, "Setup", "UpdatePageContent / UpdateHierarchy", new { shape = "pageLevel=2 on the page root, then the section's page list if needed" }, viaHierarchy ?? viaPage, new { viaPage, viaHierarchy, pageLevelAfter = level });
    }

    /// <summary>A partial page document that only sets <c>pageLevel</c>.</summary>
    internal static string PageLevelXml(string pageId, int level) =>
        string.Create(CultureInfo.InvariantCulture, $"""<?xml version="1.0"?><one:Page xmlns:one="{OneNoteMcp.Xml.OneNoteXml.Namespace2013}" ID="{Attr(pageId)}" pageLevel="{level}" />""");

    private static async Task<string?> PageLevelAsync(ProbeSession s, string sectionId, string pageId) =>
        (string?)ById(await s.TreeAsync(sectionId), pageId)?.Attribute("pageLevel");

    /// <summary>
    /// A soft delete for setup, sending the target's hierarchy date (5.1 constraint 1), after a 2 s settle so the date of
    /// our own write just before has caught up (5.1 constraint 2). Never <c>DateTime.MinValue</c>; never retried.
    /// </summary>
    public static async Task<Attempt> SoftDeleteAsync(Round2Context c, string notebookId, string id)
    {
        await c.S.EnsureNothingShownAsync(id, "DeleteHierarchy");
        await Task.Delay(TimeSpan.FromSeconds(2), ProbeSession.Ct);
        return await c.S.DeleteHierarchyAsync(id, await HierarchyDateAsync(c.S, notebookId, id), permanently: false);
    }

    /// <summary>The node's hierarchy <c>lastModifiedTime</c>, UTC. Throws when it has none: a probe never sends <c>DateTime.MinValue</c>.</summary>
    public static async Task<DateTime> HierarchyDateAsync(ProbeSession s, string notebookId, string id)
    {
        var lmt = Lmt(ById(await s.TreeAsync(notebookId), id))
            ?? throw new InvalidOperationException($"{id} has no hierarchy lastModifiedTime.");
        return ParseUtc(lmt);
    }

    // --- Where pages are ------------------------------------------------------------------------------------------

    /// <summary>
    /// Every page titled <paramref name="title"/> in the given notebooks, recycle bin included, with its section,
    /// position, level and dates. A probe-named page found here joins <see cref="ProbeSession.Created"/>, by
    /// <see cref="ProbeSession.FindByNameAsync"/>'s rule (a move or a soft delete gives a page a new ID).
    /// </summary>
    public static async Task<List<PageSpot>> FindPagesAsync(ProbeSession s, IEnumerable<ProbeTarget?> notebooks, string title)
    {
        var spots = new List<PageSpot>();
        foreach (var nb in notebooks.OfType<ProbeTarget>().DistinctBy(n => n.NotebookId))
        {
            foreach (var page in (await s.TreeAsync(nb.NotebookId)).Descendants(One + "Page").Where(p => (string?)p.Attribute("name") == title))
            {
                var spot = PageSpot.From(nb.Label, page);
                if (spot.Id is not null && title.StartsWith(ProbeSession.ProbeNamePrefix, StringComparison.Ordinal))
                {
                    s.Created.Add(spot.Id);
                }

                spots.Add(spot);
            }
        }

        return spots;
    }

    /// <summary>The pages directly in a section, in order.</summary>
    public static async Task<List<PageSpot>> PagesAsync(ProbeSession s, string label, string sectionId) =>
        (await s.TreeAsync(sectionId)).Elements(One + "Page").Select(p => PageSpot.From(label, p)).ToList();

    /// <summary>Visible pages (title, any level) under <paramref name="nodeId"/>, recycle bin left out; null when the read fails.</summary>
    public static async Task<int?> PageCountAsync(ProbeSession s, string nodeId)
    {
        try
        {
            return (await s.TreeAsync(nodeId)).DescendantsAndSelf(One + "Page")
                .Count(p => (string?)p.Attribute("isInRecycleBin") != "true" && !p.Ancestors().Any(a => (string?)a.Attribute("isRecycleBin") == "true"));
        }
        catch (OneNoteMcp.Core.OneNoteException ex) when (ex.Code is not (OneNoteMcp.Core.ErrorCode.Timeout or OneNoteMcp.Core.ErrorCode.ComBusy))
        {
            return null;
        }
    }

    /// <summary>The page counts at once and 5 s later (O4, S3: an opened file's pages aren't always listed straight away; 5.1 constraint 7).</summary>
    public static async Task<object> PagesNowAndLaterAsync(ProbeSession s, string nodeId)
    {
        var now = await PageCountAsync(s, nodeId);
        await Task.Delay(TimeSpan.FromSeconds(5), ProbeSession.Ct);
        return new { pagesAt0s = now, pagesAt5s = await PageCountAsync(s, nodeId) };
    }

    /// <summary>The open notebooks' IDs and names.</summary>
    public static async Task<Dictionary<string, NodeInfo>> OpenNotebooksAsync(ProbeSession s) =>
        (await s.NotebooksAsync()).Elements(One + "Notebook")
            .Select(NodeInfo.From)
            .Where(n => n.Id is not null)
            .ToDictionary(n => n.Id!, StringComparer.Ordinal);
}

/// <summary>Where one page is: notebook label, ID, name, section, recycle bin, level, position among its section's pages, and dates.</summary>
internal sealed record PageSpot(string Notebook, string? Id, string? Name, string? SectionId, string? SectionName, bool InRecycleBin, string? PageLevel, int Position, string? LastModifiedTime, string? DateTime)
{
    public static PageSpot From(string notebook, XElement page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var section = page.Parent;
        var inBin = (string?)page.Attribute("isInRecycleBin") == "true" || page.Ancestors().Any(a => (string?)a.Attribute("isRecycleBin") == "true");
        var position = section is null ? -1 : section.Elements(page.Name).ToList().IndexOf(page) + 1;
        return new PageSpot(
            notebook,
            (string?)page.Attribute("ID"),
            (string?)page.Attribute("name"),
            (string?)section?.Attribute("ID"),
            (string?)section?.Attribute("name"),
            inBin,
            (string?)page.Attribute("pageLevel"),
            position,
            (string?)page.Attribute("lastModifiedTime"),
            (string?)page.Attribute("dateTime"));
    }
}

/// <summary>Spike 3.1's export probes: X1 and X2 (every <c>Publish</c> format per scope) and O1–O4 (<c>OpenPackage</c> targets).</summary>
internal static class Round2ExportProbes
{
    /// <summary>X1's formats: every <c>PublishFormat</c> but <c>pfOneNote</c>, which 5.1 already settled (constraints 6, 18).</summary>
    internal static readonly PublishFormat[] X1Formats =
    [
        PublishFormat.pfPDF,
        PublishFormat.pfXPS,
        PublishFormat.pfWord,
        PublishFormat.pfMHTML,
        PublishFormat.pfHTML,
        PublishFormat.pfEMF,
        PublishFormat.pfOneNote2007,
        PublishFormat.pfOneNotePackage,
    ];

    /// <summary>All nine formats (the label probe, LB).</summary>
    internal static readonly PublishFormat[] AllFormats = [PublishFormat.pfOneNote, .. X1Formats];

    /// <summary>The file extension a probe gives each format's target.</summary>
    internal static string Extension(PublishFormat format) => format switch
    {
        PublishFormat.pfOneNote or PublishFormat.pfOneNote2007 => ".one",
        PublishFormat.pfOneNotePackage => ".onepkg",
        PublishFormat.pfMHTML => ".mht",
        PublishFormat.pfPDF => ".pdf",
        PublishFormat.pfXPS => ".xps",
        PublishFormat.pfWord => ".docx",
        PublishFormat.pfEMF => ".emf",
        PublishFormat.pfHTML => ".htm",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// X1: a page, a 3-page section (one subpage) and a notebook (the throwaway: never a test notebook, which holds
    /// Ballast Vault) in each of <see cref="X1Formats"/>, one fresh folder each. X2: whether any <c>.onepkg</c> that
    /// returned ok never landed within 120 s.
    /// </summary>
    public static async Task ExportAsync(Round2Context c)
    {
        var s = c.S;
        var label = c.T.Label;
        var (section, pages) = await Round2.SectionAsync(c, c.Run, $"probe exp {c.Stamp}", [$"probe exp p1 {c.Stamp}", $"probe exp p2 {c.Stamp}", $"probe exp p3 {c.Stamp}"], lastIsSubpage: true);
        var scopes = new List<(string Scope, string? Id)> { ("page", pages[0]), ("section", section), ("notebook", c.Other?.NotebookId) };
        var packages = new List<object>();
        foreach (var (scope, id) in scopes)
        {
            var only = Environment.GetEnvironmentVariable("ONENOTE_MCP_PROBE_FORMATS")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var format in X1Formats.Where(f => only is null || only.Contains(f.ToString())))
            {
                var probe = $"x1-{label}-{scope}-{format}";
                if (id is null)
                {
                    s.Recorder.Row(probe, "Export", $"Publish({format})", new { scope }, null, new { notProbed = "no throwaway notebook is open (a test notebook is never published whole: it holds Ballast Vault)" });
                    continue;
                }

                if (format == PublishFormat.pfOneNote2007 && only is null)
                {
                    // Sitting A (2026-11-16): Publish(pfOneNote2007) of a page in MCP Test ended ONENOTE.EXE (RPC_S_CALL_FAILED
                    // after about 3 s; the breaker stopped the run). Not sent again, so the rest of the sitting can run.
                    s.Recorder.Row(probe, "Export", $"Publish({format})", new { scope }, null, new { notProbed = "crashed OneNote on a page at sitting A's first run (0x800706BE); not re-sent" });
                    continue;
                }

                var landing = await s.PublishFreshAsync($"x1-{label}-{scope}-{format}-{c.Stamp}", id, format, $"probe-x1-{scope}{Extension(format)}");
                s.Recorder.Row(probe, "Export", $"Publish({format})", new { scope }, landing.Attempt, landing.Summary);
                if (format == PublishFormat.pfOneNotePackage)
                {
                    packages.Add(PackageLanding(scope, landing));
                }
            }
        }

        s.Recorder.Row($"x2-{label}", "Export", "Publish(pfOneNotePackage)", null, null, new { packages, note = "XP1-2's packages are in the protected scenario's xp rows" });
    }

    /// <summary>X2's view of one package: did a <c>Publish</c> that returned ok land within the poll?</summary>
    internal static object PackageLanding(string scope, PublishLanding landing) => new
    {
        scope,
        ok = landing.Attempt.Ok,
        landed = landing.Landing.TargetExists,
        neverLanded = landing.Attempt.Ok && !landing.Landing.TargetExists,
        landing.Landing.FirstSeenMs,
        landing.Landing.StableMs,
        landing.Landing.PolledMs,
    };

    /// <summary>
    /// O1–O4: one notebook package (the throwaway's) opened with <c>OpenPackage</c> into an existing empty folder (O1), a
    /// folder holding <c>probe-marker.txt</c> (O2), and O1's folder again, now filled (O3). O4: for each, focus, the path
    /// out, the notebook opened, and its pages at 0 s and 5 s.
    /// </summary>
    public static async Task OpenPackageAsync(Round2Context c)
    {
        var s = c.S;
        var label = c.T.Label;
        if (c.Other is null)
        {
            s.Recorder.Row($"o-{label}", "OpenPackage", "OpenPackage", null, null, new { notProbed = "no throwaway notebook to package" });
            return;
        }

        var package = await s.PublishFreshAsync($"o-{label}-package-{c.Stamp}", c.Other.NotebookId, PublishFormat.pfOneNotePackage, $"probe-o-{c.Stamp}.onepkg");
        s.Recorder.Row($"o-{label}-package", "OpenPackage", "Publish(pfOneNotePackage)", new { scope = "notebook" }, package.Attempt, package.Summary);
        if (package.Landed is not { } pkg)
        {
            s.Recorder.Row($"o-{label}", "OpenPackage", "OpenPackage", null, null, new { notProbed = "the package didn't land" });
            return;
        }

        var empty = ProbeSession.FreshFolder(ProbeSession.PublishDir, $"o1-{label}-{c.Stamp}");
        var marked = ProbeSession.FreshFolder(ProbeSession.PublishDir, $"o2-{label}-{c.Stamp}");
        await File.WriteAllTextAsync(Path.Combine(marked, TestNotebook.ProbeMarkerFile), "spike 3.1 probe marker", ProbeSession.Ct);

        var o4 = new List<object>
        {
            (await OpenOneAsync(c, $"o1-{label}", pkg, empty, "an existing empty folder")).View,
            (await OpenOneAsync(c, $"o2-{label}", pkg, marked, "a folder holding probe-marker.txt")).View,
            (await OpenOneAsync(c, $"o3-{label}", pkg, empty, "the folder O1 already filled from this package")).View,
        };
        s.Recorder.Row($"o4-{label}", "OpenPackage", "OpenPackage", null, null, new { rows = o4 });
    }

    /// <summary>
    /// One <c>OpenPackage</c> row; returns O4's view of it and the notebook it opened. If <c>OpenPackage</c> opened no
    /// notebook, the path it reports is opened with <c>OpenHierarchy</c>, as 5.1 did (the only way to read it back).
    /// </summary>
    internal static async Task<(object View, string? NotebookId)> OpenOneAsync(Round2Context c, string probe, string package, string destination, string what)
    {
        var s = c.S;
        var before = await Round2.OpenNotebooksAsync(s);
        var contentsBefore = ProbeSession.Snapshot(destination);
        var (attempt, pathOut, focusKept) = await s.OpenPackageIntoAsync(package, destination);
        var after = await Round2.OpenNotebooksAsync(s);
        var added = after.Values.Where(n => !before.ContainsKey(n.Id!)).ToList();
        var atPath = pathOut is null ? null : after.Values.FirstOrDefault(n => n.Path is not null && string.Equals(TestNotebookGuard.TrimPath(n.Path), TestNotebookGuard.TrimPath(pathOut), StringComparison.OrdinalIgnoreCase));
        var notebook = atPath ?? added.FirstOrDefault();

        // 5.1 opened the unpacked folder with OpenHierarchy itself; here that runs only if OpenPackage opened nothing.
        Attempt? openHierarchy = null;
        if (attempt.Ok && notebook is null && pathOut is not null)
        {
            var (opened, openedId, _) = await s.OpenBackupAsync(pathOut);
            openHierarchy = opened;
            notebook = openedId is null ? null : (await Round2.OpenNotebooksAsync(s)).GetValueOrDefault(openedId);
        }

        var pages = notebook?.Id is null ? null : await Round2.PagesNowAndLaterAsync(s, notebook.Id);
        if (notebook?.Name is { } name)
        {
            c.ToClose.Add(name);
        }

        var view = new
        {
            destination = what,
            focusKept,
            pathOut = pathOut is null ? null : Path.GetRelativePath(ProbeSession.PublishDir, pathOut),
            openedByOpenPackage = notebook is not null && openHierarchy is null,
            openHierarchy,
            notebook = notebook is null ? null : new { notebook.Name, notebook.Nickname, path = notebook.Path is null ? null : Path.GetRelativePath(ProbeSession.PublishDir, notebook.Path), sameAsPathOut = atPath is not null },
            notebooksAdded = added.Count,
            pages,
            destinationBefore = contentsBefore.Count,
            destinationAfter = ProbeSession.Snapshot(destination).Take(50).ToList(),
        };
        s.Recorder.Row(probe, "OpenPackage", "OpenPackage", new { destination = what }, attempt, view);
        return (view, notebook?.Id);
    }
}
