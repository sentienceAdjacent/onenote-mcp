using OneNoteMcp.Com.Interop;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>Spike 3.1's merge probes: F1–F3 (<c>MergeFiles</c>) and S1–S3 (<c>MergeSections</c>).</summary>
internal static class Round2MergeProbes
{
    private const string EditA = "probe edit A";
    private const string EditB = "probe edit B";

    /// <summary>
    /// F1: three versions of one section (base; client with edit A; server with edit B, without A), published to
    /// <c>.one</c> files of the same name in fresh folders, then <c>MergeFiles</c> into a new target. Locally the target
    /// is then opened and merged into an empty run section (S3's route) to see whether both edits survive. F2 (local):
    /// three unrelated <c>.one</c> files. F3 (local): a target that already exists (a copy of F1's).
    /// </summary>
    public static async Task MergeFilesAsync(Round2Context c)
    {
        var s = c.S;
        var label = c.T.Label;
        if (Environment.GetEnvironmentVariable("ONENOTE_MCP_PROBE_MERGEFILES") != "1")
        {
            // Sitting A (2026-11-16): MergeFiles ended ONENOTE.EXE every time it was called (three times), and the
            // crashes brought up OneNote's safe-start prompt. Not sent again unless asked for explicitly.
            s.Recorder.Row($"f-{label}", "MergeFiles", "MergeFiles", null, null, new { notProbed = "MergeFiles crashes OneNote (sitting A, three times); not re-sent" });
            return;
        }

        var name = $"probe mf {c.Stamp}";
        var (section, pages) = await Round2.SectionAsync(c, c.Run, name, [$"probe mf p1 {c.Stamp}"]);
        var page = pages[0];
        var fileName = name + ".one";

        var baseFile = await PublishVersionAsync(c, $"f1-{label}-base", section, fileName);
        var client = await EditAsync(c, page, EditA) ? await PublishVersionAsync(c, $"f1-{label}-client", section, fileName) : null;
        var server = await EditAsync(c, page, EditB) ? await PublishVersionAsync(c, $"f1-{label}-server", section, fileName) : null;
        var texts = PageTexts(await s.PageAsync(page));
        s.Recorder.Row($"f1-{label}-setup", "MergeFiles", "Publish(pfOneNote)", null, null, new
        {
            versions = new { baseFile = baseFile is not null, client = client is not null, server = server is not null },
            liveHasA = texts.Contains(EditA),
            liveHasB = texts.Contains(EditB),
        });
        if (baseFile is null || client is null || server is null)
        {
            s.Recorder.Row($"f1-{label}", "MergeFiles", "MergeFiles", null, null, new { notProbed = "a version didn't publish (see the f1 setup rows)" });
            return;
        }

        var target = await MergeFilesRowAsync(c, $"f1-{label}", "base, client (edit A), server (edit B): three versions of one section", baseFile, client, server, fileName, [EditA, EditB]);
        if (c.T.Cloud)
        {
            return; // the Matrix asks for the open-and-merge check locally only
        }

        if (target is not null)
        {
            // F3 on a copy, so F1's target is still the file MergeFiles wrote when it is opened below.
            var existingFolder = ProbeSession.FreshFolder(ProbeSession.PublishDir, $"f3-{label}-target-{c.Stamp}");
            var existing = Path.Combine(existingFolder, fileName);
            File.Copy(target, existing);
            var before = ProbeSession.Snapshot(existingFolder);
            var again = await s.MergeFilesAsync(baseFile, client, server, existing);
            s.Recorder.Row($"f3-{label}", "MergeFiles", "MergeFiles", new { target = "a file that already exists (a copy of F1's target)" }, again, new
            {
                before,
                after = (await ProbeSession.PollLandingAsync(existingFolder, existing, TimeSpan.FromSeconds(ProbeSession.FailedLandingPollSeconds))).Files,
            });

            await OpenAndMergeAsync(c, $"f1-{label}-open-merge", target, $"probe mf dst {c.Stamp}", [EditA, EditB]);
        }
        else
        {
            s.Recorder.Row($"f1-{label}-open-merge", "MergeFiles", "OpenHierarchy, MergeSections", null, null, new { notProbed = "MergeFiles wrote no target" });
            s.Recorder.Row($"f3-{label}", "MergeFiles", "MergeFiles", null, null, new { notProbed = "MergeFiles wrote no target to copy" });
        }

        // F2: three unrelated sections' files.
        var unrelated = new List<string>();
        for (var i = 1; i <= 3; i++)
        {
            var (u, _) = await Round2.SectionAsync(c, c.Run, $"probe mf u{i} {c.Stamp}", [$"probe mf u{i} p1 {c.Stamp}"]);
            if (await PublishVersionAsync(c, $"f2-{label}-u{i}", u, $"probe mf u{i} {c.Stamp}.one") is { } file)
            {
                unrelated.Add(file);
            }
        }

        if (unrelated.Count == 3)
        {
            await MergeFilesRowAsync(c, $"f2-{label}", "three unrelated .one files", unrelated[0], unrelated[1], unrelated[2], $"probe mf u {c.Stamp}.one", []);
        }
        else
        {
            s.Recorder.Row($"f2-{label}", "MergeFiles", "MergeFiles", null, null, new { notProbed = "an unrelated section didn't publish" });
        }
    }

    /// <summary>One <c>MergeFiles</c> into a fresh folder; the target's landing and a byte count of the edits (never opened). Returns the target when it landed.</summary>
    private static async Task<string?> MergeFilesRowAsync(Round2Context c, string probe, string shape, string baseFile, string client, string server, string fileName, IReadOnlyList<string> edits)
    {
        var s = c.S;
        var folder = ProbeSession.FreshFolder(ProbeSession.PublishDir, $"{probe}-target-{c.Stamp}");
        var target = Path.Combine(folder, fileName);
        var attempt = await s.MergeFilesAsync(baseFile, client, server, target);
        var landing = await ProbeSession.PollLandingAsync(folder, target, TimeSpan.FromSeconds(attempt.Ok ? ProbeSession.LandingPollSeconds : ProbeSession.FailedLandingPollSeconds));
        s.Recorder.Row(probe, "MergeFiles", "MergeFiles", new { shape }, attempt, new
        {
            landing,
            scan = landing.TargetExists ? ProtectedPublishProbes.Scan(target, edits) : null,
        });
        return landing.TargetExists ? target : null;
    }

    private static async Task<string?> PublishVersionAsync(Round2Context c, string probe, string section, string fileName)
    {
        var landing = await c.S.PublishFreshAsync($"{probe}-{c.Stamp}", section, PublishFormat.pfOneNote, fileName);
        c.S.Recorder.Row(probe, "MergeFiles", "Publish(pfOneNote)", null, landing.Attempt, landing.Summary);
        return landing.Landed;
    }

    /// <summary>
    /// Gives the page's one Outline the original "probe text" line plus <paramref name="edit"/>, by objectID, with the page
    /// XML's own date; any other line (an earlier edit) is left out, so OneNote deletes it.
    /// </summary>
    private static async Task<bool> EditAsync(Round2Context c, string pageId, string edit)
    {
        var page = await c.S.PageAsync(pageId);
        var oe = OeByText(page, "probe text") ?? throw new InvalidOperationException("The probe page has no \"probe text\" line.");
        var outline = OutlineOf(oe) ?? throw new InvalidOperationException("The probe line has no Outline.");
        var date = ParseUtc(Lmt(page) ?? throw new InvalidOperationException("No page lastModifiedTime."));
        var attempt = await c.S.TryWriteAnyAsync(
            pageId,
            "UpdatePageContent(edit)",
            app => app.UpdatePageContent(Page(pageId, Outline(outline, Oe(ObjectId(oe), "probe text"), Oe(null, edit))), date, XMLSchema.xs2013, force: false));
        await c.S.CheckTimeoutAsync("UpdatePageContent-edit", attempt);
        c.S.Recorder.Row($"f1-{c.T.Label}-edit-{(edit == EditA ? "a" : "b")}", "MergeFiles", "UpdatePageContent", new { edit }, attempt, null);
        return attempt.Ok;
    }

    /// <summary>The texts of the page's lines (title left out).</summary>
    private static List<string> PageTexts(System.Xml.Linq.XElement page) => Oes(page).Select(Text).OfType<string>().ToList();

    /// <summary>
    /// S3's route: open a <c>.one</c> under the publish folder with <c>OpenHierarchy</c> (never a protected section's:
    /// 5.1 constraint 17), list its pages at 0 s and 5 s, merge it into an empty new run section, and record where the
    /// pages are, which texts they hold, and what is left of the opened section.
    /// </summary>
    internal static async Task OpenAndMergeAsync(Round2Context c, string probe, string file, string destinationName, IReadOnlyList<string> texts)
    {
        var s = c.S;
        var (open, openedId, _) = await s.OpenBackupAsync(file);
        if (!open.Ok || openedId is null)
        {
            s.Recorder.Row(probe, "MergeSections", "OpenHierarchy(cftNone)", null, open, new { notProbed = "the file didn't open" });
            return;
        }

        var opened = NodeInfo.From(await s.TreeAsync(openedId));
        if (opened.Name is { } openedName)
        {
            c.ToClose.Add(openedName);
        }

        var listed = await Round2.PagesNowAndLaterAsync(s, openedId);
        var destination = await s.MustCreateChildAsync(c.Run, destinationName + ".one", CreateFileType.cftSection);
        var merge = await s.MergeSectionsAsync(openedId, destination);
        var pages = await Round2.PagesAsync(s, c.T.Label, destination);
        var contents = new List<object>();
        foreach (var page in pages.Where(p => p.Id is not null))
        {
            var found = PageTexts(await s.PageAsync(page.Id!));
            contents.Add(new { page.Name, has = texts.ToDictionary(t => t, found.Contains) });
        }

        s.Recorder.Row(probe, "MergeSections", "OpenHierarchy(cftNone), MergeSections", new { source = "a .one opened from the publish folder", destination = "an empty run section" }, merge, new
        {
            open,
            opened = new { opened.Kind, opened.Name, sameIdAsALiveSection = s.Created.Contains(openedId), opened.Pages },
            listed,
            destinationPages = pages,
            contents,
            openedAfter = await Round2.PageCountAsync(s, openedId),
        });
    }

    /// <summary>
    /// S1: a source (two pages and a subpage) merged into a destination with one page, in the same notebook. S2: the
    /// same across notebooks (to the throwaway). S3: an unprotected section's <c>.one</c> backup opened, then merged into
    /// an empty run section.
    /// </summary>
    public static async Task MergeSectionsAsync(Round2Context c)
    {
        var label = c.T.Label;
        await MergeRowAsync(c, $"s1-{label}", "within the notebook", c.T, c.Run, c.T, c.Run, "s1");
        if (c.Other is null)
        {
            c.S.Recorder.Row($"s2-{label}", "MergeSections", "MergeSections", null, null, new { notProbed = "no throwaway notebook is open" });
        }
        else
        {
            await MergeRowAsync(c, $"s2-{label}", "across notebooks (to the throwaway)", c.T, c.Run, c.Other, c.Other.NotebookId, "s2");
        }

        // S3: an unprotected section, published to .one, opened and merged.
        var (section, _) = await Round2.SectionAsync(c, c.Run, $"probe s3 {c.Stamp}", [$"probe s3 p1 {c.Stamp}", $"probe s3 p2 {c.Stamp}"]);
        var backup = await c.S.PublishFreshAsync($"s3-{label}-{c.Stamp}", section, PublishFormat.pfOneNote, $"probe s3 {c.Stamp}.one");
        c.S.Recorder.Row($"s3-{label}-publish", "MergeSections", "Publish(pfOneNote)", null, backup.Attempt, backup.Summary);
        if (backup.Landed is { } file)
        {
            await OpenAndMergeAsync(c, $"s3-{label}", file, $"probe s3 dst {c.Stamp}", ["probe text"]);
        }
        else
        {
            c.S.Recorder.Row($"s3-{label}", "MergeSections", "MergeSections", null, null, new { notProbed = "the backup didn't land" });
        }
    }

    /// <summary>
    /// One <c>MergeSections</c> row. Source: two pages and a subpage, made under <paramref name="sourceParent"/> in
    /// <paramref name="from"/>. Destination: one page, under <paramref name="destinationParent"/> in <paramref name="to"/>.
    /// Records, before and after: both sections' pages (IDs, order, dates, levels), where each source page is now, and
    /// whether the source survives, empty or in the recycle bin.
    /// </summary>
    internal static async Task MergeRowAsync(Round2Context c, string probe, string shape, ProbeTarget from, string sourceParent, ProbeTarget to, string destinationParent, string tag)
    {
        var srcName = $"probe ms {tag} src {c.Stamp}";
        string[] titles = [$"probe ms {tag} p1 {c.Stamp}", $"probe ms {tag} p2 {c.Stamp}", $"probe ms {tag} p2a {c.Stamp}"];
        var (source, _) = await Round2.SectionAsync(c, sourceParent, srcName, titles, lastIsSubpage: true);
        var (destination, _) = await Round2.SectionAsync(c, destinationParent, $"probe ms {tag} dst {c.Stamp}", [$"probe ms {tag} d1 {c.Stamp}"]);
        await MergeExistingAsync(c, probe, shape, from, source, srcName, titles, to, destination);
    }

    /// <summary>The merge and its read-back, for sections that already exist (SP and the cross-storage merges use it too).</summary>
    internal static async Task MergeExistingAsync(Round2Context c, string probe, string shape, ProbeTarget from, string source, string sourceName, IReadOnlyList<string> titles, ProbeTarget to, string destination)
    {
        var s = c.S;
        var notebooks = new[] { from, to };
        var srcBefore = await NodeAndPagesAsync(s, from, source);
        var dstBefore = await NodeAndPagesAsync(s, to, destination);
        var attempt = await s.MergeSectionsAsync(source, destination);
        var where = new Dictionary<string, List<PageSpot>>(StringComparer.Ordinal);
        foreach (var title in titles)
        {
            where[title] = await Round2.FindPagesAsync(s, notebooks, title);
        }

        var dstAfter = await NodeAndPagesAsync(s, to, destination);
        var beforeIds = srcBefore.Pages.Concat(dstBefore.Pages).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        s.Recorder.Row(probe, "MergeSections", "MergeSections", new { shape, from = from.Label, to = to.Label }, attempt, new
        {
            sourceBefore = srcBefore,
            destinationBefore = dstBefore,
            sourceAfter = await NodeAndPagesAsync(s, from, source),
            sourceByName = (await s.FindByNameAsync(from.NotebookId, sourceName)).Select(n => new { n.Id, sameId = n.Id == source, n.InRecycleBin, n.ParentName, n.Pages, n.Encrypted, n.Locked }).ToList(),
            destinationAfter = dstAfter,
            newPageIds = dstAfter.Pages.Count(p => !beforeIds.Contains(p.Id)),
            where,
        });
    }

    /// <summary>A section's node (null when gone) and its pages.</summary>
    internal static async Task<SectionView> NodeAndPagesAsync(ProbeSession s, ProbeTarget nb, string sectionId)
    {
        var node = await s.NodeAsync(nb.NotebookId, sectionId);
        return new SectionView(node, node is null ? [] : await Round2.PagesAsync(s, nb.Label, sectionId));
    }
}

/// <summary>A section as read back: its node (null when gone from its notebook) and its pages.</summary>
internal sealed record SectionView(NodeInfo? Node, IReadOnlyList<PageSpot> Pages);
