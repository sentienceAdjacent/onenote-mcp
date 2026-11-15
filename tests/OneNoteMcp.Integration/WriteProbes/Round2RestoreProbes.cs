using OneNoteMcp.Com.Interop;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>Spike 3.1's recycle-bin restore probes: R1–R3 and R5 (what triggers <c>0x8004202E</c> hrRecycleBinEditFailed).</summary>
internal static class Round2RestoreProbes
{
    private const string Q = "Restore";

    /// <summary>
    /// R1: a recycled page listed in its original section's XML; if that doesn't restore it, OneMore's second call (the
    /// Deleted Pages XML without it), only when M1(b) showed a partial list keeps what it leaves out. R2: a recycled page
    /// sent to a section the caller names, in the same notebook and in another. R3: a recycled section listed in its run
    /// group's XML. R5: the four edits that might return <c>0x8004202E</c>.
    /// </summary>
    public static async Task RestoreAsync(Round2Context c)
    {
        var s = c.S;
        var label = c.T.Label;
        var st = c.Stamp;
        var (section, pages) = await Round2.SectionAsync(c, c.Run, $"probe rs {st}", [$"probe rs r1 {st}", $"probe rs r2s {st}", $"probe rs r2x {st}", $"probe rs keep {st}"]);

        // R1.
        var r1 = await RecycleAsync(c, $"r1-{label}", pages[0], $"probe rs r1 {st}");
        if (r1 is not null)
        {
            var restored = await RestorePageAsync(c, $"r1-{label}", "the original section's XML listing the recycled page", c.T, section, r1, $"probe rs r1 {st}");
            await SecondCallAsync(c, $"r1-{label}-second-call", restored, r1, $"probe rs r1 {st}");
        }

        // R2: to another section of the same notebook, then to another notebook.
        var (other, _) = await Round2.SectionAsync(c, c.Run, $"probe rs other {st}", [$"probe rs other p1 {st}"]);
        if (await RecycleAsync(c, $"r2-{label}-same", pages[1], $"probe rs r2s {st}") is { } r2s)
        {
            await RestorePageAsync(c, $"r2-{label}-same", "another section's XML (same notebook) listing the recycled page", c.T, other, r2s, $"probe rs r2s {st}");
        }

        if (c.Other is null)
        {
            s.Recorder.Row($"r2-{label}-other", Q, "UpdateHierarchy", null, null, new { notProbed = "no throwaway notebook is open" });
        }
        else
        {
            var (far, _) = await Round2.SectionAsync(c, c.Other.NotebookId, $"probe rs far {st}", [$"probe rs far p1 {st}"]);
            if (await RecycleAsync(c, $"r2-{label}-other", pages[2], $"probe rs r2x {st}") is { } r2x)
            {
                await RestorePageAsync(c, $"r2-{label}-other", "a section's XML in another notebook listing the recycled page", c.Other, far, r2x, $"probe rs r2x {st}");
            }
        }

        // R3: a recycled section back into its run group.
        var group = await s.MustCreateChildAsync(c.Run, $"probe rs grp {st}", CreateFileType.cftFolder);
        var (sec, _) = await Round2.SectionAsync(c, group, $"probe rs sec {st}", [$"probe rs sec p1 {st}"]);
        var secBefore = await s.NodeAsync(c.T.NotebookId, sec);
        var deleted = await Round2.SoftDeleteAsync(c, c.T.NotebookId, sec);
        var recycled = await s.NodeAsync(c.T.NotebookId, sec);
        s.Recorder.Row($"r3-{label}-soft-delete", Q, "DeleteHierarchy(soft)", new { date = "hierarchy" }, deleted, new { before = secBefore, after = recycled });
        if (deleted.Ok && recycled is { InRecycleBin: true })
        {
            var restore = await s.UpdateHierarchyAsync(Hierarchy("SectionGroup", group, string.Empty, Node("Section", sec)));
            s.Recorder.Row($"r3-{label}", Q, "UpdateHierarchy", new { shape = "the run group's XML listing the recycled section by its ID" }, restore, new
            {
                recycled,
                sameIdAfter = await s.NodeAsync(c.T.NotebookId, sec),
                byName = await s.FindByNameAsync(c.T.NotebookId, $"probe rs sec {st}"),
                groupChildren = ProbeXml.Children(await HierarchyProbes.NodeElementAsync(s, c.T, group)),
            });
        }
        else
        {
            s.Recorder.Row($"r3-{label}", Q, "UpdateHierarchy", null, null, new { notProbed = "the section didn't reach the recycle bin" });
        }

        await R5Async(c);
    }

    /// <summary>Soft-deletes a page (its hierarchy date) and returns its recycle-bin copy's ID, or null (with a row saying why).</summary>
    private static async Task<string?> RecycleAsync(Round2Context c, string probe, string pageId, string title)
    {
        var attempt = await Round2.SoftDeleteAsync(c, c.T.NotebookId, pageId);
        var spots = await Round2.FindPagesAsync(c.S, [c.T], title);
        var copy = spots.FirstOrDefault(p => p.InRecycleBin);
        c.S.Recorder.Row($"{probe}-soft-delete", Q, "DeleteHierarchy(soft)", new { date = "hierarchy" }, attempt, new { spots, newId = copy?.Id != pageId });
        return attempt.Ok ? copy?.Id : null;
    }

    /// <summary>Lists the recycled page first in <paramref name="sectionId"/>'s XML, then that section's pages. Returns true when the page left the recycle bin for that section.</summary>
    private static async Task<bool> RestorePageAsync(Round2Context c, string probe, string shape, ProbeTarget to, string sectionId, string recycledId, string title)
    {
        var s = c.S;
        var notebooks = new[] { c.T, to };
        var before = await Round2.FindPagesAsync(s, notebooks, title);
        var target = await Round2.PagesAsync(s, to.Label, sectionId);
        var attempt = await s.UpdateHierarchyAsync(Hierarchy("Section", sectionId, string.Empty, [Node("Page", recycledId), .. target.Select(p => p.Id).OfType<string>().Select(id => Node("Page", id))]));
        var after = await Round2.FindPagesAsync(s, notebooks, title);
        var restored = after.Any(p => p.SectionId == sectionId && !p.InRecycleBin);
        s.Recorder.Row(probe, Q, "UpdateHierarchy", new { shape, to = to.Label }, attempt, new
        {
            restored,
            leftDeletedPages = !after.Any(p => p.InRecycleBin),
            idKept = after.Any(p => p.Id == recycledId && !p.InRecycleBin),
            before,
            after,
            section = await Round2.PagesAsync(s, to.Label, sectionId),
        });
        return restored;
    }

    /// <summary>
    /// R1's second call (OneMore's): the Deleted Pages section's XML without the page, listing only this run's other
    /// recycled pages there. Only when the first call didn't restore it and M1(b) showed a partial list keeps the pages it
    /// leaves out (plan, Order hazards); else a <c>notProbed</c> row.
    /// </summary>
    private static async Task SecondCallAsync(Round2Context c, string probe, bool restored, string recycledId, string title)
    {
        var s = c.S;
        if (restored)
        {
            s.Recorder.Row(probe, Q, "UpdateHierarchy", null, null, new { notProbed = "the first call restored the page" });
            return;
        }

        if (c.OmittedPagesKept is not true)
        {
            s.Recorder.Row(probe, Q, "UpdateHierarchy", null, null, new
            {
                notProbed = c.OmittedPagesKept is null
                    ? "M1(b) didn't run in this run (rerun the move step with it), so a partial Deleted Pages list could remove other recycled pages"
                    : "M1(b) showed a partial page list removes the pages it leaves out, so this call could remove other recycled pages",
            });
            return;
        }

        var spot = (await Round2.FindPagesAsync(s, [c.T], title)).FirstOrDefault(p => p.InRecycleBin);
        if (spot?.SectionId is not { } deletedPages)
        {
            s.Recorder.Row(probe, Q, "UpdateHierarchy", null, null, new { notProbed = "the page is no longer in the recycle bin" });
            return;
        }

        var ours = (await Round2.PagesAsync(s, c.T.Label, deletedPages)).Where(p => p.Id is not null && p.Id != spot.Id && s.Created.Contains(p.Id)).Select(p => Node("Page", p.Id!)).ToArray();
        if (ours.Length == 0)
        {
            s.Recorder.Row(probe, Q, "UpdateHierarchy", null, null, new { notProbed = "no other run page in Deleted Pages; an empty list was never tested" });
            return;
        }

        var attempt = await s.UpdateHierarchyAsync(Hierarchy("Section", deletedPages, string.Empty, ours), allowRecycleBins: true);
        s.Recorder.Row(probe, Q, "UpdateHierarchy", new { shape = "Deleted Pages' XML without the page (only this run's other recycled pages listed)", listed = ours.Length }, attempt, new
        {
            recycledId,
            after = await Round2.FindPagesAsync(s, [c.T], title),
        });
    }

    /// <summary>
    /// R5: renaming a recycled section; moving a live run section into <c>OneNote_RecycleBin</c>; <c>UpdatePageContent</c>
    /// on a recycled page; reordering this run's pages in Deleted Pages (only when M1(b) showed a partial list keeps the
    /// rest). Each records its HRESULT and where things are after.
    /// </summary>
    private static async Task R5Async(Round2Context c)
    {
        var s = c.S;
        var label = c.T.Label;
        var st = c.Stamp;
        var p = $"r5-{label}";

        // A recycled section, renamed.
        var (rename, _) = await Round2.SectionAsync(c, c.Run, $"probe rs r5a {st}", [$"probe rs r5a p1 {st}"]);
        var deleted = await Round2.SoftDeleteAsync(c, c.T.NotebookId, rename);
        if (deleted.Ok && (await s.NodeAsync(c.T.NotebookId, rename)) is { InRecycleBin: true })
        {
            var attempt = await s.UpdateHierarchyAsync(Hierarchy("Section", rename, A("name", $"probe rs r5a renamed {st}")));
            s.Recorder.Row($"{p}-rename-recycled-section", Q, "UpdateHierarchy", new { shape = "Section root (in the recycle bin) with a new name" }, attempt, new { after = await s.NodeAsync(c.T.NotebookId, rename) });
        }
        else
        {
            s.Recorder.Row($"{p}-rename-recycled-section", Q, "UpdateHierarchy", null, deleted, new { notProbed = "the section didn't reach the recycle bin" });
        }

        // A live run section listed under the notebook's OneNote_RecycleBin group.
        var (live, _) = await Round2.SectionAsync(c, c.Run, $"probe rs r5b {st}", [$"probe rs r5b p1 {st}"]);
        var (bin, deletedPagesIds) = await s.RecycleBinAsync(c.T.NotebookId);
        if (bin is null)
        {
            s.Recorder.Row($"{p}-move-into-recycle-bin", Q, "UpdateHierarchy", null, null, new { notProbed = "the notebook has no OneNote_RecycleBin group" });
        }
        else
        {
            var before = await s.NodeAsync(c.T.NotebookId, live);
            var attempt = await s.UpdateHierarchyAsync(Hierarchy("SectionGroup", bin, string.Empty, Node("Section", live)), allowRecycleBins: true);
            s.Recorder.Row($"{p}-move-into-recycle-bin", Q, "UpdateHierarchy", new { shape = "OneNote_RecycleBin's XML listing a live run section" }, attempt, new { before, after = await s.NodeAsync(c.T.NotebookId, live) });
        }

        // UpdatePageContent on a recycled page, with that page's own page-XML date.
        var (_, pagePages) = await Round2.SectionAsync(c, c.Run, $"probe rs r5c {st}", [$"probe rs r5c p1 {st}", $"probe rs r5c p2 {st}"]);
        var copy = await RecycleAsync(c, $"{p}-update-recycled-page", pagePages[0], $"probe rs r5c p1 {st}");
        if (copy is null)
        {
            s.Recorder.Row($"{p}-update-recycled-page", Q, "UpdatePageContent", null, null, new { notProbed = "the page didn't reach the recycle bin" });
        }
        else
        {
            var date = ParseUtc(Lmt(await s.PageAsync(copy)) ?? throw new InvalidOperationException("No page lastModifiedTime."));
            var attempt = await s.UpdateAsync(copy, Page(copy, Outline(null, Oe(null, "probe edit in the recycle bin"))), date);
            await s.CheckTimeoutAsync("UpdatePageContent", attempt);
            s.Recorder.Row($"{p}-update-recycled-page", Q, "UpdatePageContent", new { shape = "a new Outline on a page in Deleted Pages" }, attempt, new
            {
                after = await Round2.FindPagesAsync(s, [c.T], $"probe rs r5c p1 {st}"),
            });
        }

        // Reorder this run's pages in Deleted Pages: only with M1(b)'s go-ahead.
        if (c.OmittedPagesKept is not true)
        {
            s.Recorder.Row($"{p}-reorder-deleted-pages", Q, "UpdateHierarchy", null, null, new
            {
                notProbed = c.OmittedPagesKept is null
                    ? "M1(b) didn't run in this run, so a partial Deleted Pages list could remove other recycled pages"
                    : "M1(b) showed a partial page list removes the pages it leaves out",
            });
            return;
        }

        var d1 = await RecycleAsync(c, $"{p}-reorder-d1", pagePages[1], $"probe rs r5c p2 {st}");
        var (_, d2Pages) = await Round2.SectionAsync(c, c.Run, $"probe rs r5d {st}", [$"probe rs r5d p1 {st}"]);
        var d2 = await RecycleAsync(c, $"{p}-reorder-d2", d2Pages[0], $"probe rs r5d p1 {st}");
        var spot = d1 is null ? null : (await Round2.FindPagesAsync(s, [c.T], $"probe rs r5c p2 {st}")).FirstOrDefault(x => x.InRecycleBin);
        if (d1 is null || d2 is null || spot?.SectionId is not { } dp || !deletedPagesIds.Contains(dp))
        {
            s.Recorder.Row($"{p}-reorder-deleted-pages", Q, "UpdateHierarchy", null, null, new { notProbed = "two run pages didn't land in the notebook's Deleted Pages", deletedPages = deletedPagesIds.Count });
            return;
        }

        var before2 = await Round2.PagesAsync(s, c.T.Label, dp);
        var reorder = await s.UpdateHierarchyAsync(Hierarchy("Section", dp, string.Empty, Node("Page", d2), Node("Page", d1)), allowRecycleBins: true);
        var after2 = await Round2.PagesAsync(s, c.T.Label, dp);
        s.Recorder.Row($"{p}-reorder-deleted-pages", Q, "UpdateHierarchy", new { shape = "Deleted Pages' XML listing two of this run's recycled pages, swapped" }, reorder, new
        {
            ourBefore = before2.Where(x => x.Id == d1 || x.Id == d2).ToList(),
            ourAfter = after2.Where(x => x.Id == d1 || x.Id == d2).ToList(),
            countBefore = before2.Count,
            countAfter = after2.Count,
        });
    }
}
