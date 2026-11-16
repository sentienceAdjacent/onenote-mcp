using OneNoteMcp.Com.Interop;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Spike 3.1's move probes: M1–M4 (pages, sections and section groups by <c>UpdateHierarchy</c>), the cross-storage
/// moves and merges (Q1 a), and D (the modal dialog, local only).
/// </summary>
internal static class Round2MoveProbes
{
    /// <summary>
    /// M1: a page moved to another section of the same notebook with three XML shapes, (a) the target's XML with the page
    /// added first, (b) the source's XML without the page, (c) (a) then (b), only if (a) left the page where it was.
    /// (b) runs on its own page, and its answer (<see cref="Round2Context.OmittedPagesKept"/>) gates R1's second call and
    /// R5's reorder. M1d: a parent page with a subpage. M2: a page to another notebook. M3: a section to another notebook.
    /// M4: a section group within the notebook and to another notebook.
    /// </summary>
    public static async Task MoveAsync(Round2Context c)
    {
        var s = c.S;
        var label = c.T.Label;
        var st = c.Stamp;

        // M1.
        var (src, srcPages) = await Round2.SectionAsync(c, c.Run, $"probe mv src {st}", [$"probe mv a {st}", $"probe mv b {st}", $"probe mv k {st}"]);
        var (dst, _) = await Round2.SectionAsync(c, c.Run, $"probe mv dst {st}", [$"probe mv t1 {st}"]);
        var movedA = await MovePageAsync(c, $"m1a-{label}", "a", c.T, src, c.T, dst, srcPages[0], $"probe mv a {st}");

        var omitted = await OmitPageAsync(c, $"m1b-{label}", c.T, src, srcPages[1], $"probe mv b {st}");
        c.OmittedPagesKept = omitted;

        var movedC = false;
        if (movedA)
        {
            s.Recorder.Row($"m1c-{label}", "Move", "UpdateHierarchy", new { shape = "c" }, null, new { notProbed = "shape (a) moved the page on its own, so OneMore's second call isn't needed" });
        }
        else if ((await Round2.FindPagesAsync(s, [c.T], $"probe mv a {st}")).FirstOrDefault(p => p.SectionId == src && !p.InRecycleBin)?.Id is { } stillThere)
        {
            // (a) was already sent in m1a and left the page in place: only (b) is sent here, never (a) again.
            movedC = await MovePageAsync(c, $"m1c-{label}", "c", c.T, src, c.T, dst, stillThere, $"probe mv a {st}", aAlreadySent: true);
        }
        else
        {
            s.Recorder.Row($"m1c-{label}", "Move", "UpdateHierarchy", new { shape = "c" }, null, new { notProbed = "after shape (a) the page is neither in the destination nor still in the source (see the m1a row)" });
        }

        c.MoveShape = movedA ? "a" : movedC ? "c" : null;
        s.Recorder.Row($"m1-{label}-shape", "Move", "-", null, null, new { workingShape = c.MoveShape, omittedPagesKept = c.OmittedPagesKept });

        // M1d: a parent with a subpage, moved by the working shape.
        var (dSrc, dPages) = await Round2.SectionAsync(c, c.Run, $"probe mv d src {st}", [$"probe mv parent {st}", $"probe mv child {st}"], lastIsSubpage: true);
        var (dDst, _) = await Round2.SectionAsync(c, c.Run, $"probe mv d dst {st}", [$"probe mv d t1 {st}"]);
        var childBefore = await Round2.FindPagesAsync(s, [c.T], $"probe mv child {st}");
        await MovePageAsync(c, $"m1d-{label}", c.MoveShape ?? "c", c.T, dSrc, c.T, dDst, dPages[0], $"probe mv parent {st}");
        s.Recorder.Row($"m1d-{label}-child", "Move", "GetHierarchy", null, null, new
        {
            childBefore,
            childAfter = await Round2.FindPagesAsync(s, [c.T], $"probe mv child {st}"),
            shapeUsed = c.MoveShape ?? "c (no working shape known this run)",
        });

        if (c.Other is null)
        {
            foreach (var row in new[] { "m2", "m3", "m4-notebook" })
            {
                s.Recorder.Row($"{row}-{label}", "Move", "UpdateHierarchy", null, null, new { notProbed = "no throwaway notebook is open" });
            }
        }
        else
        {
            // M2: a page to a section of another notebook.
            var (xSrc, xPages) = await Round2.SectionAsync(c, c.Run, $"probe mv x src {st}", [$"probe mv x {st}"]);
            var (xDst, _) = await Round2.SectionAsync(c, c.Other.NotebookId, $"probe mv x dst {st}", [$"probe mv x t1 {st}"]);
            await MovePageAsync(c, $"m2-{label}", c.MoveShape ?? "c", c.T, xSrc, c.Other, xDst, xPages[0], $"probe mv x {st}");

            // M3: a section into a run group of the other notebook, and another to its root.
            var group = await s.MustCreateChildAsync(c.Other.NotebookId, $"probe mv grp {st}", CreateFileType.cftFolder);
            var (sec1, _) = await Round2.SectionAsync(c, c.Run, $"probe mv sec1 {st}", [$"probe mv sec1 p1 {st}"]);
            await MoveNodeAsync(c, $"m3-{label}-into-group", "Section", c.T, sec1, c.Run, c.Other, "SectionGroup", group);
            var (sec2, _) = await Round2.SectionAsync(c, c.Run, $"probe mv sec2 {st}", [$"probe mv sec2 p1 {st}"]);
            await MoveNodeAsync(c, $"m3-{label}-to-root", "Section", c.T, sec2, c.Run, c.Other, "Notebook", c.Other.NotebookId);
        }

        // M4: a section group (one section inside) to another group of the notebook, then another to the other notebook.
        var g1 = await GroupWithSectionAsync(c, c.Run, "probe mv g1");
        var g2 = await s.MustCreateChildAsync(c.Run, $"probe mv g2 {st}", CreateFileType.cftFolder);
        await MoveNodeAsync(c, $"m4-{label}-group", "SectionGroup", c.T, g1, c.Run, c.T, "SectionGroup", g2);
        if (c.Other is not null)
        {
            var g3 = await GroupWithSectionAsync(c, c.Run, "probe mv g3");
            await MoveNodeAsync(c, $"m4-{label}-notebook", "SectionGroup", c.T, g3, c.Run, c.Other, "Notebook", c.Other.NotebookId);
        }

        if (c.T.Cloud)
        {
            var checkedIt = await s.PauseAsync(
                $"m4-{label}-web-check",
                $"First, in OneNote desktop, press Sync Now (Shift+F9) on {TestNotebook.CloudNotebookName} and on {TestNotebook.CloudThrowawayName}, and wait until File > Info > View Sync Status shows each as up to date "
                + $"(tell the team lead about any error, with its code). Then, in a browser, open them in OneNote for the web. Tell the team lead: is \"probe mv g1 {st}\" (with its section \"probe mv g1 s {st}\") now inside "
                + $"\"probe mv g2 {st}\" in \"probe run {st}\"? And in \"{TestNotebook.CloudThrowawayName}\", is \"probe mv g3 {st}\" (with its section) at the notebook's top level? "
                + "Don't change anything. Then continue.");
            s.Recorder.Row($"m4-{label}-web-check", "Move", "-", null, null, new { phillChecked = checkedIt, answer = "relayed by the team lead into the findings doc" });
        }
    }

    /// <summary>
    /// A section group <c>&lt;stem&gt; &lt;stamp&gt;</c> under <paramref name="parentId"/>, holding one section
    /// <c>&lt;stem&gt; s &lt;stamp&gt;</c> with one page; returns the group's ID.
    /// </summary>
    private static async Task<string> GroupWithSectionAsync(Round2Context c, string parentId, string stem)
    {
        var group = await c.S.MustCreateChildAsync(parentId, $"{stem} {c.Stamp}", CreateFileType.cftFolder);
        await Round2.SectionAsync(c, group, $"{stem} s {c.Stamp}", [$"{stem} p1 {c.Stamp}"]);
        return group;
    }

    /// <summary>
    /// Moves page <paramref name="pageId"/> by shape "a" (the destination's XML with the page first, then its own
    /// pages) or "c" ("a", then the source's XML without the page if "a" left it there). Records the page wherever it is
    /// before and after (ID, name, dates, level, position, recycle bin), both sections' dates and pages, and returns
    /// whether it ended up in the destination. With <paramref name="aAlreadySent"/> (M1(c) after M1(a)), the "a" call isn't
    /// sent again: only "b" is, so no write is ever resent. If the source stays on screen, nothing is sent.
    /// </summary>
    internal static async Task<bool> MovePageAsync(Round2Context c, string probe, string shape, ProbeTarget from, string sourceId, ProbeTarget to, string destinationId, string pageId, string title, bool aAlreadySent = false)
    {
        var s = c.S;
        var notebooks = new[] { from, to };
        if (!await s.EnsureNothingShownAsync(sourceId, "UpdateHierarchy(move)")) // the source changes too; the destination is checked by the call
        {
            s.Recorder.Row(probe, "Move", "UpdateHierarchy", new { shape, from = from.Label, to = to.Label }, ProbeSession.NotShownRefusal, null);
            return false;
        }

        var spotBefore = await Round2.FindPagesAsync(s, notebooks, title);
        var srcBefore = await Round2MergeProbes.NodeAndPagesAsync(s, from, sourceId);
        var dstBefore = await Round2MergeProbes.NodeAndPagesAsync(s, to, destinationId);

        Attempt? a = null;
        if (!aAlreadySent)
        {
            var dstIds = dstBefore.Pages.Select(p => p.Id).OfType<string>().Where(id => id != pageId).Select(id => Node("Page", id));
            a = await s.UpdateHierarchyAsync(Hierarchy("Section", destinationId, string.Empty, [Node("Page", pageId), .. dstIds]));
        }

        var afterA = a is null ? spotBefore : await Round2.FindPagesAsync(s, notebooks, title);
        var moved = InSection(afterA, destinationId);
        Attempt? b = null;
        if (shape == "c" && !moved)
        {
            var rest = (await Round2.PagesAsync(s, from.Label, sourceId)).Where(p => p.Id is not null && p.Id != pageId && p.Name != title).Select(p => Node("Page", p.Id!)).ToArray();
            b = await s.UpdateHierarchyAsync(Hierarchy("Section", sourceId, string.Empty, rest));
        }

        var spotAfter = b is null ? afterA : await Round2.FindPagesAsync(s, notebooks, title);
        moved = InSection(spotAfter, destinationId);
        s.Recorder.Row(probe, "Move", "UpdateHierarchy", new { shape, from = from.Label, to = to.Label, aAlreadySent }, b ?? a, new
        {
            a = aAlreadySent ? (object)"not resent: sent in the m1a row" : a,
            b,
            moved,
            newId = spotAfter.Any(p => p.Id is not null && p.Id != pageId),
            pageBefore = spotBefore,
            pageAfterA = b is null ? null : afterA,
            pageAfter = spotAfter,
            inDeletedPages = spotAfter.Any(p => p.InRecycleBin),
            sourceBefore = srcBefore,
            sourceAfter = await Round2MergeProbes.NodeAndPagesAsync(s, from, sourceId),
            destinationBefore = dstBefore,
            destinationAfter = await Round2MergeProbes.NodeAndPagesAsync(s, to, destinationId),
        });
        return moved;
    }

    private static bool InSection(IEnumerable<PageSpot> spots, string sectionId) => spots.Any(p => p.SectionId == sectionId && !p.InRecycleBin);

    /// <summary>
    /// M1(b) on its own: the source section's XML listing every page but <paramref name="pageId"/>. Returns true when the
    /// left-out page is still in the source (kept), false when it went anywhere else or is gone.
    /// </summary>
    private static async Task<bool?> OmitPageAsync(Round2Context c, string probe, ProbeTarget nb, string sourceId, string pageId, string title)
    {
        var s = c.S;
        var before = await Round2MergeProbes.NodeAndPagesAsync(s, nb, sourceId);
        var rest = before.Pages.Where(p => p.Id is not null && p.Id != pageId).Select(p => Node("Page", p.Id!)).ToArray();
        var attempt = await s.UpdateHierarchyAsync(Hierarchy("Section", sourceId, string.Empty, rest));
        var spots = await Round2.FindPagesAsync(s, [nb], title);
        bool? kept = attempt.Ok ? InSection(spots, sourceId) : null;
        s.Recorder.Row(probe, "Move", "UpdateHierarchy", new { shape = "b: the source section's XML without the page" }, attempt, new
        {
            omittedPageKept = kept,
            page = spots,
            inDeletedPages = spots.Any(p => p.InRecycleBin),
            sourceBefore = before,
            sourceAfter = await Round2MergeProbes.NodeAndPagesAsync(s, nb, sourceId),
        });
        return kept;
    }

    /// <summary>
    /// Moves a section or section group (<paramref name="kind"/>) by listing it under <paramref name="targetId"/> (a
    /// <paramref name="targetKind"/> root). Records its ID, path and dates before and after, where it is by name, both
    /// parents' dates, and, for a group, the section inside it.
    /// </summary>
    internal static async Task<Attempt> MoveNodeAsync(Round2Context c, string probe, string kind, ProbeTarget from, string id, string fromParentId, ProbeTarget to, string targetKind, string targetId)
    {
        var s = c.S;
        if (!await s.EnsureNothingShownAsync(id, "UpdateHierarchy(move)")) // the source changes too; the target is checked by the call
        {
            s.Recorder.Row(probe, "Move", "UpdateHierarchy", new { kind, target = targetKind, from = from.Label, to = to.Label }, ProbeSession.NotShownRefusal, null);
            return ProbeSession.NotShownRefusal;
        }

        var before = await s.NodeAsync(from.NotebookId, id);
        var inner = kind == "SectionGroup" ? ProbeXml.Children(await HierarchyProbes.NodeElementAsync(s, from, id)) : [];
        var fromParentBefore = await s.NodeAsync(from.NotebookId, fromParentId);
        var targetBefore = await s.NodeAsync(to.NotebookId, targetId);
        var attempt = await s.UpdateHierarchyAsync(Hierarchy(targetKind, targetId, string.Empty, Node(kind, id)));
        var afterTo = await s.NodeAsync(to.NotebookId, id);
        var afterFrom = await s.NodeAsync(from.NotebookId, id);
        var innerAfter = new List<object>();
        foreach (var child in inner.Where(x => x.Name is not null))
        {
            innerAfter.Add(new { before = child, after = (await s.FindByNameAsync(to.NotebookId, child.Name!)).Concat(await s.FindByNameAsync(from.NotebookId, child.Name!)).Distinct().ToList() });
        }

        s.Recorder.Row(probe, "Move", "UpdateHierarchy", new { kind, target = targetKind, from = from.Label, to = to.Label }, attempt, new
        {
            before,
            afterInTarget = afterTo,
            afterInSource = from.NotebookId == to.NotebookId ? null : afterFrom,
            sameId = afterTo is not null,
            byName = before?.Name is null ? null : (await s.FindByNameAsync(to.NotebookId, before.Name)).Concat(from.NotebookId == to.NotebookId ? [] : await s.FindByNameAsync(from.NotebookId, before.Name)).ToList(),
            inner = innerAfter,
            fromParentLmtBefore = fromParentBefore?.LastModifiedTime,
            fromParentLmtAfter = (await s.NodeAsync(from.NotebookId, fromParentId))?.LastModifiedTime,
            targetLmtBefore = targetBefore?.LastModifiedTime,
            targetLmtAfter = (await s.NodeAsync(to.NotebookId, targetId))?.LastModifiedTime,
        });
        return attempt;
    }

    /// <summary>
    /// Q1 a, last in the cloud run: between MCP Test and MCP Test Cloud, a page move, a section move and a merge in each
    /// direction. MCP Test gets its own run group for them.
    /// </summary>
    public static async Task CrossStorageAsync(Round2Context c)
    {
        var s = c.S;
        var st = c.Stamp;
        var folder = TestNotebook.ResolveFolder(Environment.GetEnvironmentVariable);
        var localId = TestNotebookGuard.FindNotebookId((await s.NotebooksAsync()).ToString(), folder);
        if (localId is null)
        {
            s.Recorder.Row("q1", "Move", "-", null, null, new { notProbed = "MCP Test is not open" });
            return;
        }

        var local = new ProbeTarget("local", localId, TestNotebook.NotebookName, Cloud: false);
        var cloud = c.T;
        await s.PauseAsync(
            "q1-show-elsewhere",
            $"In OneNote, click a page in a notebook other than {TestNotebook.NotebookName} and {TestNotebook.CloudNotebookName} (for example {TestNotebook.CloudThrowawayName}), "
            + "so that neither test notebook is on screen: the next probes change both. Don't type. Then continue.");
        var localRun = await HierarchyProbes.RunGroupAsync(s, local, st);
        var runs = new Dictionary<string, string>(StringComparer.Ordinal) { ["local"] = localRun, ["cloud"] = c.Run };

        // Sitting B (2026-11-16): ONENOTE.EXE crashed (mso20win32client.dll) about 2 s after the local-to-cloud section
        // move, while the cross-storage merge was being set up. So cloud-to-local runs first, each move is followed by a
        // settle and a read (a crash then trips the breaker on that read, pinning it to the move), and the cross-storage
        // merge runs only with ONENOTE_MCP_PROBE_CROSS_MERGE=1.
        var crossMerge = Environment.GetEnvironmentVariable("ONENOTE_MCP_PROBE_CROSS_MERGE") == "1";
        var crossMoves = Environment.GetEnvironmentVariable("ONENOTE_MCP_PROBE_CROSS_MOVES") != "0"; // "0": the merge alone
        foreach (var (from, to) in new[] { (cloud, local), (local, cloud) })
        {
            var dir = $"{from.Label[0]}{to.Label[0]}"; // "lc" or "cl"
            if (!crossMoves)
            {
                s.Recorder.Row($"q1-moves-{from.Label}-to-{to.Label}", "Move", "UpdateHierarchy", null, null, new { notProbed = "ONENOTE_MCP_PROBE_CROSS_MOVES=0: the merge runs alone" });
                goto Merge;
            }

            var (pSrc, pPages) = await Round2.SectionAsync(c, runs[from.Label], $"probe xs {dir} src {st}", [$"probe xs {dir} p {st}"]);
            var (pDst, _) = await Round2.SectionAsync(c, runs[to.Label], $"probe xs {dir} dst {st}", [$"probe xs {dir} t1 {st}"]);
            await MovePageAsync(c, $"q1-page-{from.Label}-to-{to.Label}", c.MoveShape ?? "c", from, pSrc, to, pDst, pPages[0], $"probe xs {dir} p {st}");
            await SettleAsync(s, $"q1-page-{from.Label}-to-{to.Label}");

            var (sec, _) = await Round2.SectionAsync(c, runs[from.Label], $"probe xs {dir} sec {st}", [$"probe xs {dir} sec p1 {st}"]);
            await MoveNodeAsync(c, $"q1-section-{from.Label}-to-{to.Label}", "Section", from, sec, runs[from.Label], to, "SectionGroup", runs[to.Label]);
            await SettleAsync(s, $"q1-section-{from.Label}-to-{to.Label}");

        Merge:
            if (!crossMerge)
            {
                s.Recorder.Row($"q1-merge-{from.Label}-to-{to.Label}", "MergeSections", "MergeSections", new { shape = "across storage", from = from.Label, to = to.Label }, null, new { notProbed = "OneNote crashed during this case's setup in sitting B; set ONENOTE_MCP_PROBE_CROSS_MERGE=1 to run it" });
                continue;
            }

            await Round2MergeProbes.MergeRowAsync(c, $"q1-merge-{from.Label}-to-{to.Label}", "across storage", from, runs[from.Label], to, runs[to.Label], $"x{dir}");
            await SettleAsync(s, $"q1-merge-{from.Label}-to-{to.Label}");
        }
    }

    /// <summary>Waits 10 s after a cross-storage move, then reads the notebook list: a crash the move set off trips the breaker here.</summary>
    private static async Task SettleAsync(ProbeSession s, string after)
    {
        await Task.Delay(TimeSpan.FromSeconds(10));
        _ = await s.NotebooksAsync();
        s.Recorder.Row($"{after}-settled", "Move", "GetHierarchy", null, null, new { after, settledSeconds = 10, oneNoteAlive = true });
    }

    /// <summary>
    /// D (local only): with File &gt; Options open, a plain <c>GetHierarchy</c>, <c>Publish</c> pfPDF, <c>OpenPackage</c>,
    /// <c>MergeSections</c>, <c>MergeFiles</c> and an M1-shape move, each prepared before the dialog opens. The close
    /// pause is in a <c>finally</c> (5.1 review finding 2).
    /// </summary>
    public static async Task ModalAsync(Round2Context c)
    {
        const string Q = "Modal dialog";
        var s = c.S;
        var st = c.Stamp;
        var p = $"d-{c.T.Label}";
        var (section, _) = await Round2.SectionAsync(c, c.Run, $"probe md s {st}", [$"probe md p1 {st}"]);
        var (mergeSrc, _) = await Round2.SectionAsync(c, c.Run, $"probe md src {st}", [$"probe md src p1 {st}"]);
        var (mergeDst, _) = await Round2.SectionAsync(c, c.Run, $"probe md dst {st}", [$"probe md dst p1 {st}"]);
        var (mvSrc, mvPages) = await Round2.SectionAsync(c, c.Run, $"probe md mv src {st}", [$"probe md mv p {st}"]);
        var (mvDst, _) = await Round2.SectionAsync(c, c.Run, $"probe md mv dst {st}", [$"probe md mv t1 {st}"]);

        var package = c.Other is null ? null : await s.PublishFreshAsync($"{p}-package-{st}", c.Other.NotebookId, PublishFormat.pfOneNotePackage, $"probe-d-{st}.onepkg");
        var baseFile = await s.PublishFreshAsync($"{p}-base-{st}", section, PublishFormat.pfOneNote, $"probe md s {st}.one");
        string? client = null;
        string? server = null;
        if (baseFile.Landed is { } b)
        {
            client = Path.Combine(ProbeSession.FreshFolder(ProbeSession.PublishDir, $"{p}-client-{st}"), Path.GetFileName(b));
            server = Path.Combine(ProbeSession.FreshFolder(ProbeSession.PublishDir, $"{p}-server-{st}"), Path.GetFileName(b));
            File.Copy(b, client);
            File.Copy(b, server);
        }

        s.Recorder.Row($"{p}-setup", Q, "-", null, null, new { package = package?.Summary, baseFile = baseFile.Summary, mergeFilesInputs = client is not null });

        if (!await s.PauseAsync($"{p}-open", "In OneNote, open File > Options and leave the Options dialog open. Don't change anything in it. Then continue."))
        {
            s.Recorder.Row(p, Q, "-", null, null, new { notProbed = "pause skipped" });
            return;
        }

        s.ShownCheckOff = true;
        try
        {
            var read = await s.TryReadAsync("GetHierarchy", app => app.GetHierarchy(c.T.NotebookId, HierarchyScope.hsSections, out _, XMLSchema.xs2013));
            s.Recorder.Row($"{p}-get-hierarchy", Q, "GetHierarchy", new { scope = "sections" }, read, null);
            await s.CheckTimeoutAsync("GetHierarchy", read);

            var pdf = await s.PublishFreshAsync($"{p}-pdf-{st}", section, PublishFormat.pfPDF, "probe-d.pdf");
            s.Recorder.Row($"{p}-publish", Q, "Publish(pfPDF)", null, pdf.Attempt, pdf.Summary);

            if (package?.Landed is { } pkg)
            {
                await Round2ExportProbes.OpenOneAsync(c, $"{p}-open-package", pkg, ProbeSession.FreshFolder(ProbeSession.PublishDir, $"{p}-unpack-{st}"), "an existing empty folder, with File > Options open");
            }
            else
            {
                s.Recorder.Row($"{p}-open-package", Q, "OpenPackage", null, null, new { notProbed = "no package was written before the dialog opened" });
            }

            var merge = await s.MergeSectionsAsync(mergeSrc, mergeDst);
            s.Recorder.Row($"{p}-merge-sections", Q, "MergeSections", null, merge, new { destination = await Round2MergeProbes.NodeAndPagesAsync(s, c.T, mergeDst) });

            // Sitting A (2026-11-16): MergeFiles ended ONENOTE.EXE every time (F1, its replay, and here with Options open),
            // and the crashes brought up OneNote's safe-start prompt. Never sent again (Round2MergeProbes.MergeFilesAsync too).
            _ = (baseFile, client, server);
            s.Recorder.Row($"{p}-merge-files", Q, "MergeFiles", null, null, new { notProbed = "MergeFiles crashes OneNote (sitting A, three times); not re-sent" });

            await MovePageAsync(c, $"{p}-move", c.MoveShape ?? "a", c.T, mvSrc, c.T, mvDst, mvPages[0], $"probe md mv p {st}");
        }
        finally
        {
            s.ShownCheckOff = false;
            await s.PauseAsync($"{p}-close", "In OneNote, close the Options dialog with Cancel. Then continue.");
        }
    }
}
