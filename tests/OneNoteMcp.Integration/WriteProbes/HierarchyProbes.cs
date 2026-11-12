using System.Globalization;
using System.Xml.Linq;
using OneNoteMcp.Com.Interop;
using Xunit;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>The notebook a story 5.1 probe step runs in.</summary>
internal sealed record ProbeTarget(string Label, string NotebookId, string NotebookName, bool Cloud);

/// <summary>
/// Story 5.1, scenario <c>write-probes-hierarchy-local</c>: hierarchy writes, deletes and backups on MCP Test. Every
/// object a step touches is one this run made, inside the run's own section group <c>probe run &lt;stamp&gt;</c>; deletes
/// and <c>UpdateHierarchy</c> refuse anything else (<see cref="TestNotebookGuard.CheckDelete"/>,
/// <see cref="TestNotebookGuard.CheckHierarchyXml"/>). Hitl: the displayed-page date probes and File &gt; Options.
/// Matrix rows: Create, HierarchyWrite, Soft delete, Permanent delete, Backup, Dates, Modal dialog, HRESULTs.
/// </summary>
public sealed class HierarchyLocalProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "write-probes-hierarchy-local";

    [Fact]
    public async Task WriteProbesHierarchyLocal()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.ProbeCallSeconds);
        var t = new ProbeTarget("local", await s.SetupAsync("setup", s.McpTestIdAsync), TestNotebook.NotebookName, Cloud: false);
        var stamp = HierarchyProbes.Stamp();
        s.Recorder.Row("previous-run-note", "Failure modes", "-", null, null, HierarchyProbes.Run1Note);
        var run = await s.SetupAsync("run-group", () => HierarchyProbes.RunGroupAsync(s, t, stamp));

        await HierarchyProbes.CommonStepsAsync(s, t, run, stamp);
        await s.StepAsync("hresults", () => HierarchyProbes.HResultsAsync(s, t, run, stamp));
        await s.StepAsync("modal-options", () => HierarchyProbes.ModalAsync(
            s,
            t,
            run,
            stamp,
            "options",
            "In OneNote, open File > Options and leave the Options dialog open. Don't change anything in it. Then continue.",
            "In OneNote, close the Options dialog with Cancel. Then continue."));

        s.Finish();
    }
}

/// <summary>
/// Story 5.1, scenario <c>write-probes-hierarchy-cloud</c>: the same create, hierarchy-write, delete, date and backup
/// probes on MCP Test Cloud (found, never created), inside the run's own <c>probe run &lt;stamp&gt;</c> section group.
/// Hitl: one pause to make the date probe pages' two dates differ, and the displayed-page date probes.
/// </summary>
public sealed class HierarchyCloudProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "write-probes-hierarchy-cloud";

    [Fact]
    public async Task WriteProbesHierarchyCloud()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.CloudProbeCallSeconds);
        var t = new ProbeTarget("cloud", await s.SetupAsync("setup", s.CloudIdAsync), TestNotebook.CloudNotebookName, Cloud: true);
        var stamp = HierarchyProbes.Stamp();
        var run = await s.SetupAsync("run-group", () => HierarchyProbes.RunGroupAsync(s, t, stamp));

        await HierarchyProbes.CommonStepsAsync(s, t, run, stamp);

        // Review finding 6: the Matrix's "from OneDrive (cloud, read-only check by Phill)" for the section and section
        // group deletes above. Pages aren't files, so only sections (.one files) and groups (folders) can be checked.
        await s.StepAsync("onedrive-check", async () =>
        {
            var checkedIt = await s.PauseAsync(
                "onedrive-check",
                $"In a browser, open OneDrive > Documents > {TestNotebook.CloudNotebookName} > probe run {stamp}, its subfolders, and the OneDrive recycle bin. "
                + $"For each of these, tell the team lead whether it is still in that folder, in the OneDrive recycle bin, or gone: "
                + $"probe del a {stamp}.one, probe del b {stamp}.one, the folder probe del c {stamp}, probe bak s {stamp}.one, the folder probe bak {stamp}. "
                + "Don't restore or delete anything. Then continue.");
            s.Recorder.Row("onedrive-check-cloud", "Permanent delete", "-", null, null, new { phillChecked = checkedIt, answer = "relayed by the team lead into the findings doc" });
        });

        s.Finish();
    }
}

/// <summary>Story 5.1's probe steps shared by the local and cloud hierarchy scenarios, and the backup helpers.</summary>
internal static class HierarchyProbes
{
    public static string Stamp() => DateTime.UtcNow.ToString("MMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>
    /// What run 1 showed, as a row the findings doc can cite from the re-run's log (the original evidence is
    /// <c>write-probes-hierarchy-local-20261111-033107.jsonl</c>, row <c>dates-local-section-stale</c> at 03:39:51 UTC).
    /// </summary>
    internal static readonly object Run1Note = new
    {
        run = "write-probes-hierarchy-local-20261111-033107",
        observed = "after Phill showed two pages in 'probe dates s 1111-033107' (pauses dates-local-show-stale and -pageXml), the next section-level date row "
            + "blocked about 3 minutes behind OneNote's 'OneNote is syncing changes' dialog until Phill pressed Cancel; the following one raised it again and froze OneNote's UI; "
            + "the team lead stopped the test process; OneNote recovered after Cancel",
        timing = "the row dates-local-section-stale (03:39:51 UTC) records ms=104 for the DeleteHierarchy attempt itself (guard reads included); the previous row was 03:36:32, "
            + "so the block was in the setup before the delete: creating the empty section 'probe dates sec stale' with OpenHierarchy, or the hierarchy reads around it, "
            + "while a page in a sibling section of the same group was on screen",
        fixes = "section date rows now run first, in their own group, before any page is shown. Before an OpenHierarchy create under a probe object, an UpdateHierarchy, "
            + "a DeleteHierarchy of a section, section group or notebook, and a Publish, the probe checks that nothing in the probe area (the run's group, a throwaway "
            + "notebook, or a recycled probe section) is on screen, pausing if it is; each check writes a shown-check row. Not checked: creating the run group in a test "
            + "notebook's root, creating or reopening a throwaway notebook, opening a backup, page-level deletes. Calls time out at 60 s; a Timeout, or a ComBusy that "
            + "took the whole timeout, trips the breaker and stops the scenario with a call-timeout row",
    };

    /// <summary>The steps both hierarchy scenarios run, in order.</summary>
    public static async Task CommonStepsAsync(ProbeSession s, ProbeTarget t, string run, string stamp)
    {
        await s.StepAsync("create", SyncChecked(s, t, "create", stamp, () => CreateAsync(s, t, run, stamp)));
        await s.StepAsync("update", SyncChecked(s, t, "update", stamp, () => UpdateAsync(s, t, run, stamp)));
        await s.StepAsync("soft-delete", SyncChecked(s, t, "soft-delete", stamp, () => DeleteAsync(s, t, run, stamp)));
        await s.StepAsync("dates", SyncChecked(s, t, "dates", stamp, () => DatesAsync(s, t, run, stamp)));
        await s.StepAsync("backup", SyncChecked(s, t, "backup", stamp, () => BackupAsync(s, t, run, stamp)));
    }

    /// <summary>
    /// On the cloud, a step ends with Phill's Sync Now check (Phill, 2026-11-11: "keep an eye out for it reoccurring").
    /// MCP Test Cloud's sync jam (0xE0000644, run write-probes-hierarchy-cloud-20261111-165147) didn't reproduce in
    /// the cloud-jam repro's R0-R5, so run 2 doubles as a wider repro: if it comes back, the first failing check names
    /// the step.
    /// </summary>
    private static Func<Task> SyncChecked(ProbeSession s, ProbeTarget t, string step, string stamp, Func<Task> body) => async () =>
    {
        await body();
        if (t.Cloud)
        {
            await CloudJamRepro.CheckSyncAsync(s, step, $"what the {step} step left in {TestNotebook.CloudNotebookName} > probe run {stamp}");
        }
    };

    /// <summary>
    /// The run's own section group, <c>probe run &lt;stamp&gt;</c>, directly in the notebook: the Create row's
    /// section-group-by-parent-ID probe. Everything else in the scenario goes inside it.
    /// </summary>
    public static async Task<string> RunGroupAsync(ProbeSession s, ProbeTarget t, string stamp)
    {
        var focus = ProbeSession.Foreground();
        var (attempt, id) = await s.CreateChildAsync(t.NotebookId, $"probe run {stamp}", CreateFileType.cftFolder);
        s.Recorder.Row($"create-{t.Label}-section-group", "Create", "OpenHierarchy(cftFolder)", new { parent = "notebook", relativeName = "probe run <stamp>" }, attempt, new
        {
            id,
            node = id is null ? null : await s.NodeAsync(t.NotebookId, id),
            focusKept = focus == ProbeSession.Foreground(),
            createdByThisCall = id is not null && s.Created.Contains(id),
        });
        if (!attempt.Ok || id is null || !s.Created.Contains(id))
        {
            throw new InvalidOperationException("The run's section group couldn't be created; no other step can run.");
        }

        return id;
    }

    // --- Create ---------------------------------------------------------------------------------------------------

    private static async Task CreateAsync(ProbeSession s, ProbeTarget t, string run, string stamp)
    {
        const string Q = "Create";
        var p = $"create-{t.Label}";

        var focus = ProbeSession.Foreground();
        var (a, section) = await s.CreateChildAsync(run, $"probe sec {stamp}.one", CreateFileType.cftSection);
        s.Recorder.Row($"{p}-section", Q, "OpenHierarchy(cftSection)", new { parent = "run group", relativeName = "probe sec <stamp>.one" }, a, new
        {
            id = section,
            node = section is null ? null : await s.NodeAsync(t.NotebookId, section),
            focusKept = focus == ProbeSession.Foreground(),
        });

        // On the cloud too: briefly suspected of MCP Test Cloud's sync jam (run write-probes-hierarchy-cloud-20261111-165147),
        // but cleared by the cloud-jam repro's R2 (write-probes-cloud-jam-repro-20261111-193209: same ID, one section, synced).
        var (again, sameName) = await s.CreateChildAsync(run, $"probe sec {stamp}.one", CreateFileType.cftSection);
        s.Recorder.Row($"{p}-section-existing-name", Q, "OpenHierarchy(cftSection)", new { parent = "run group", relativeName = "the same name again" }, again, new
        {
            sameId = sameName is not null && sameName == section,
            withThatName = (await s.FindByNameAsync(t.NotebookId, $"probe sec {stamp}")).Count,
        });

        var (noExt, noExtId) = await s.CreateChildAsync(run, $"probe noext {stamp}", CreateFileType.cftSection);
        s.Recorder.Row($"{p}-section-no-extension", Q, "OpenHierarchy(cftSection)", new { parent = "run group", relativeName = "probe noext <stamp> (no .one)" }, noExt, new
        {
            node = noExtId is null ? null : await s.NodeAsync(t.NotebookId, noExtId),
        });

        var (subAttempt, sub) = await s.CreateChildAsync(run, $"probe sub {stamp}", CreateFileType.cftFolder);
        s.Recorder.Row($"{p}-nested-section-group", Q, "OpenHierarchy(cftFolder)", new { parent = "run group" }, subAttempt, new
        {
            node = sub is null ? null : await s.NodeAsync(t.NotebookId, sub),
        });

        if (sub is not null)
        {
            var (nested, nestedId) = await s.CreateChildAsync(sub, $"probe nested {stamp}.one", CreateFileType.cftSection);
            s.Recorder.Row($"{p}-section-in-nested-group", Q, "OpenHierarchy(cftSection)", new { parent = "nested section group" }, nested, new
            {
                node = nestedId is null ? null : await s.NodeAsync(t.NotebookId, nestedId),
            });

            // Not re-run: the relative name "..\probe up <stamp>.one" was probed in run write-probes-hierarchy-local-20261111-033107.
            // OneNote returned ok and listed a section under a section group named "..", but never wrote a file for it, and
            // from then on MCP Test stayed "syncing" with error 0xE0000644 9zu13 (View Sync Status, 2026-11-11 10:18 local):
            // every later hierarchy create in the notebook blocked behind "OneNote is syncing changes".
            s.Recorder.Row($"{p}-relative-name-up", Q, "OpenHierarchy(cftSection)", new { parent = "nested section group", relativeName = @"..\probe up <stamp>.one" }, null, new
            {
                notProbed = "settled by run write-probes-hierarchy-local-20261111-033107: it jams the notebook's sync (0xE0000644); not repeated",
            });
        }

        if (section is not null)
        {
            var (underSection, underSectionId) = await s.CreateChildAsync(section, $"probe child {stamp}.one", CreateFileType.cftSection);
            s.Recorder.Row($"{p}-under-section", Q, "OpenHierarchy(cftSection)", new { parent = "a section (not a container)" }, underSection, new
            {
                id = underSectionId,
                found = await s.FindByNameAsync(t.NotebookId, $"probe child {stamp}"),
            });

            var page = await s.CreateProbePageAsync(section, $"probe create page {stamp}");
            var (underPage, underPageId) = await s.CreateChildAsync(page, $"probe pagechild {stamp}.one", CreateFileType.cftSection);
            s.Recorder.Row($"{p}-under-page", Q, "OpenHierarchy(cftSection)", new { parent = "a page" }, underPage, new
            {
                id = underPageId,
                found = await s.FindByNameAsync(t.NotebookId, $"probe pagechild {stamp}"),
            });
        }

        // Not re-run either: run write-probes-hierarchy-local-20261111-033107 got hrFileDoesNotExist for it, and it may also
        // have left something OneNote can't write (the same run's sync jam), so it isn't repeated on a live notebook.
        s.Recorder.Row($"{p}-invalid-name", Q, "OpenHierarchy(cftSection)", new { relativeName = "probe bad*? <stamp>.one" }, null, new
        {
            notProbed = "settled by run write-probes-hierarchy-local-20261111-033107 (hrFileDoesNotExist); not repeated",
        });

        // Not probed (review finding 7, Phill 2026-11-10): OpenHierarchy with a parent that no longer exists. If OneNote
        // fell back to a default or current location, the new section could land in a real notebook, which no guard
        // can prevent. The findings doc lists it under Not probed.
        s.Recorder.Row($"{p}-parent-gone", Q, "OpenHierarchy(cftSection)", new { parent = "a deleted section group" }, null, new
        {
            notProbed = "could create a section outside the test notebooks if OneNote falls back to another location (review finding 7)",
        });
    }

    // --- HierarchyWrite -------------------------------------------------------------------------------------------

    private static async Task UpdateAsync(ProbeSession s, ProbeTarget t, string run, string stamp)
    {
        const string Q = "HierarchyWrite";
        var p = $"update-{t.Label}";
        var group = await s.MustCreateChildAsync(run, $"probe upd {stamp}", CreateFileType.cftFolder);
        var s1 = await s.MustCreateChildAsync(group, $"probe s1 {stamp}.one", CreateFileType.cftSection);
        var s2 = await s.MustCreateChildAsync(group, $"probe s2 {stamp}.one", CreateFileType.cftSection);
        var s3 = await s.MustCreateChildAsync(group, $"probe s3 {stamp}.one", CreateFileType.cftSection);
        var sub = await s.MustCreateChildAsync(group, $"probe upd sub {stamp}", CreateFileType.cftFolder);
        var p1 = await s.CreateProbePageAsync(s1, $"probe p1 {stamp}");
        var p2 = await s.CreateProbePageAsync(s1, $"probe p2 {stamp}");
        var p3 = await s.CreateProbePageAsync(s1, $"probe p3 {stamp}");

        // Add a section, then a section group, by name without an ID.
        await WriteRowAsync(s, t, $"{p}-add-section", Q, group, "SectionGroup root; a new Section with a name and no ID",
            Hierarchy("SectionGroup", group, string.Empty, Node("Section", null, A("name", $"probe add {stamp}"))),
            async () => new { added = await s.FindByNameAsync(t.NotebookId, $"probe add {stamp}") });
        await WriteRowAsync(s, t, $"{p}-add-section-group", Q, group, "SectionGroup root; a new SectionGroup with a name and no ID",
            Hierarchy("SectionGroup", group, string.Empty, Node("SectionGroup", null, A("name", $"probe add sg {stamp}"))),
            async () => new { added = await s.FindByNameAsync(t.NotebookId, $"probe add sg {stamp}") });

        // Renames, with the node itself as the root.
        var s1Before = await s.NodeAsync(t.NotebookId, s1);
        await WriteRowAsync(s, t, $"{p}-rename-section", Q, group, "Section root with a new name",
            Hierarchy("Section", s1, A("name", $"probe s1 renamed {stamp}")),
            async () => new { before = s1Before, after = await s.NodeAsync(t.NotebookId, s1) });
        var subBefore = await s.NodeAsync(t.NotebookId, sub);
        await WriteRowAsync(s, t, $"{p}-rename-section-group", Q, group, "SectionGroup root with a new name",
            Hierarchy("SectionGroup", sub, A("name", $"probe upd sub renamed {stamp}")),
            async () => new { before = subBefore, after = await s.NodeAsync(t.NotebookId, sub) });

        // Reorder: every child of the group, reversed.
        var children = Children(await NodeElementAsync(s, t, group));
        var reversed = children.AsEnumerable().Reverse().ToList();
        await WriteRowAsync(s, t, $"{p}-reorder-sections", Q, group, "SectionGroup root listing every child, by ID, in reverse order",
            Hierarchy("SectionGroup", group, string.Empty, reversed.Select(c => Node(c.Kind, c.Id)).ToArray()),
            async () => new { sent = reversed.Select(c => c.Name).ToList(), after = Children(await NodeElementAsync(s, t, group)).Select(c => c.Name).ToList() });

        // Reorder pages: p3, p1, p2.
        var s1Element = await NodeElementAsync(s, t, s1);
        var pagesBefore = PageNames(s1Element);
        var s1LmtBefore = Lmt(s1Element);
        await WriteRowAsync(s, t, $"{p}-reorder-pages", Q, s1, "Section root listing its pages by ID: p3, p1, p2",
            Hierarchy("Section", s1, string.Empty, Node("Page", p3), Node("Page", p1), Node("Page", p2)),
            async () =>
            {
                var after = await NodeElementAsync(s, t, s1);
                return new { before = pagesBefore, after = PageNames(after), sectionLmtBefore = s1LmtBefore, sectionLmtAfter = Lmt(after) };
            });

        // A known section placed under another parent: moved, ignored or refused?
        await WriteRowAsync(s, t, $"{p}-accidental-move", Q, group, "SectionGroup root = the nested group, listing s3 (a sibling of the group) by ID",
            Hierarchy("SectionGroup", sub, string.Empty, Node("Section", s3)),
            async () => new { s3 = await s.NodeAsync(t.NotebookId, s3) });

        // Another writer: does a section's or its group's lastModifiedTime move when a page is added to it?
        var s2Before = await s.NodeAsync(t.NotebookId, s2);
        var groupBefore = await s.NodeAsync(t.NotebookId, group);
        await s.CreateProbePageAsync(s2, $"probe other writer {stamp}");
        s.Recorder.Row($"{p}-other-writer-lmt", Q, "CreateNewPage", new { shape = "a page added to s2" }, null, new
        {
            sectionLmtBefore = s2Before?.LastModifiedTime,
            sectionLmtAfter = (await s.NodeAsync(t.NotebookId, s2))?.LastModifiedTime,
            groupLmtBefore = groupBefore?.LastModifiedTime,
            groupLmtAfter = (await s.NodeAsync(t.NotebookId, group))?.LastModifiedTime,
        });

        // Rename to a sibling's current name.
        var s1Name = (await s.NodeAsync(t.NotebookId, s1))?.Name ?? $"probe s1 renamed {stamp}";
        await WriteRowAsync(s, t, $"{p}-rename-existing-name", Q, group, "Section root renamed to a sibling's name",
            Hierarchy("Section", s2, A("name", s1Name)),
            async () => new { s2 = await s.NodeAsync(t.NotebookId, s2), withThatName = (await s.FindByNameAsync(t.NotebookId, s1Name)).Count });

        // Last (review finding 1): a partial child list, only s2 then s1. Are the others kept? It may delete the
        // siblings it leaves out, so every probe that needs them runs before it.
        var beforePartial = Children(await NodeElementAsync(s, t, group));
        await WriteRowAsync(s, t, $"{p}-partial-children", Q, group, "SectionGroup root listing only s2 then s1, by ID",
            Hierarchy("SectionGroup", group, string.Empty, Node("Section", s2), Node("Section", s1)),
            async () =>
            {
                var after = Children(await NodeElementAsync(s, t, group));
                var omitted = new List<object>();
                foreach (var c in beforePartial.Where(c => c.Id != s1 && c.Id != s2))
                {
                    omitted.Add(new { c.Name, kept = after.Any(x => x.Id == c.Id), now = await s.NodeAsync(t.NotebookId, c.Id!) });
                }

                return new
                {
                    before = beforePartial.Select(c => c.Name).ToList(),
                    after = after.Select(c => c.Name).ToList(),
                    omitted,
                };
            });
    }

    private static string RootIdOrParent(string xml, string parentId) => ProbeSession.RootId(xml) ?? parentId;

    /// <summary>
    /// One <c>UpdateHierarchy</c> probe row: the attempt, the parent's <c>lastModifiedTime</c> before and after, and
    /// <paramref name="readBack"/>.
    /// </summary>
    private static async Task WriteRowAsync(ProbeSession s, ProbeTarget t, string probe, string question, string parentId, string shape, string xml, Func<Task<object>> readBack)
    {
        await s.EnsureNothingShownAsync(RootIdOrParent(xml, parentId), "UpdateHierarchy"); // before the reads, so a pause can't come between them and the call
        var parentBefore = (await s.NodeAsync(t.NotebookId, parentId))?.LastModifiedTime;
        var attempt = await s.UpdateHierarchyAsync(xml);
        s.Recorder.Row(probe, question, "UpdateHierarchy", new { shape }, attempt, new
        {
            parentLmtBefore = parentBefore,
            parentLmtAfter = (await s.NodeAsync(t.NotebookId, parentId))?.LastModifiedTime,
            readBack = await readBack(),
        });
    }

    // --- Soft and permanent delete --------------------------------------------------------------------------------

    private static async Task DeleteAsync(ProbeSession s, ProbeTarget t, string run, string stamp)
    {
        var p = t.Label;
        var group = await s.MustCreateChildAsync(run, $"probe del {stamp}", CreateFileType.cftFolder);
        var a = await s.MustCreateChildAsync(group, $"probe del a {stamp}.one", CreateFileType.cftSection);
        var p1Title = $"probe del p1 {stamp}";
        var p2Title = $"probe del p2 {stamp}";
        var p1 = await s.CreateProbePageAsync(a, p1Title);
        var p2 = await s.CreateProbePageAsync(a, p2Title);
        var b = await s.MustCreateChildAsync(group, $"probe del b {stamp}.one", CreateFileType.cftSection);
        await s.CreateProbePageAsync(b, $"probe del bp {stamp}");
        var c = await s.MustCreateChildAsync(group, $"probe del c {stamp}", CreateFileType.cftFolder);
        var c1 = await s.MustCreateChildAsync(c, $"probe del c1 {stamp}.one", CreateFileType.cftSection);
        await s.CreateProbePageAsync(c1, $"probe del cp {stamp}");

        // Soft deletes: page, section, section group.
        await DeleteRowAsync(s, t, $"soft-delete-{p}-page", "Soft delete", p1, p1Title, permanently: false, pageDate: true);
        await DeleteRowAsync(s, t, $"soft-delete-{p}-section", "Soft delete", b, $"probe del b {stamp}", permanently: false, pageDate: false);
        await DeleteRowAsync(s, t, $"soft-delete-{p}-section-group", "Soft delete", c, $"probe del c {stamp}", permanently: false, pageDate: false, alsoFind: $"probe del c1 {stamp}");

        // Permanent deletes: a live page and section, then the soft-deleted items, now in the recycle bin.
        await DeleteRowAsync(s, t, $"permanent-delete-{p}-page", "Permanent delete", p2, p2Title, permanently: true, pageDate: true);
        await DeleteRecycledAsync(s, t, $"permanent-delete-{p}-recycled-page", p1Title);
        await DeleteRecycledAsync(s, t, $"permanent-delete-{p}-recycled-section", $"probe del b {stamp}");
        await DeleteRecycledAsync(s, t, $"permanent-delete-{p}-recycled-section-group", $"probe del c {stamp}");
        await DeleteRowAsync(s, t, $"permanent-delete-{p}-section", "Permanent delete", a, $"probe del a {stamp}", permanently: true, pageDate: false);
    }

    /// <summary>
    /// One delete row: the date sent (the page XML's for a page when <paramref name="pageDate"/>, else the hierarchy's),
    /// then where the object is afterwards (same ID, recycle bin, gone) and, locally, what's on disk.
    /// </summary>
    internal static async Task<Attempt> DeleteRowAsync(ProbeSession s, ProbeTarget t, string probe, string question, string id, string name, bool permanently, bool pageDate, string? alsoFind = null)
    {
        await s.EnsureNothingShownAsync(id, "DeleteHierarchy", pageTargetsExempt: true); // before the reads, so a pause can't come between them and the call
        var before = await s.NodeAsync(t.NotebookId, id);
        var diskBefore = ProbeSession.Disk(before?.Path);
        var date = pageDate ? ParseUtc(Lmt(await s.PageAsync(id)) ?? throw new InvalidOperationException("No page lastModifiedTime.")) : await NodeDateAsync(s, t, id);
        var attempt = await s.DeleteHierarchyAsync(id, date, permanently);
        var after = await s.NodeAsync(t.NotebookId, id);
        var found = await s.FindByNameAsync(t.NotebookId, name);
        s.Recorder.Row(probe, question, permanently ? "DeleteHierarchy(permanent)" : "DeleteHierarchy(soft)", new { kind = before?.Kind, date = pageDate ? "page XML" : "hierarchy", dateSent = FormatUtc(date) }, attempt, new
        {
            before,
            sameIdAfter = after,
            existsAnywhere = await s.ExistsAnywhereAsync(id),
            byName = found.Select(n => new { n.Kind, n.Id, newId = n.Id != id, n.ParentKind, n.ParentName, n.InRecycleBin, n.Path, n.Pages }).ToList(),
            children = alsoFind is null ? null : await s.FindByNameAsync(t.NotebookId, alsoFind),
            diskBefore,
            diskAfterOldPath = ProbeSession.Disk(before?.Path),
            diskNewPaths = found.Where(n => n.Path is not null && n.Path != before?.Path).Select(n => ProbeSession.Disk(n.Path)).ToList(),
        });
        return attempt;
    }

    /// <summary>A permanent delete of the recycle-bin copy of the object named <paramref name="name"/>.</summary>
    private static async Task DeleteRecycledAsync(ProbeSession s, ProbeTarget t, string probe, string name)
    {
        var recycled = (await s.FindByNameAsync(t.NotebookId, name)).FirstOrDefault(n => n.InRecycleBin && n.Id is not null);
        if (recycled is null)
        {
            s.Recorder.Row(probe, "Permanent delete", "DeleteHierarchy(permanent)", null, null, new { notProbed = "no recycle-bin copy found by name" });
            return;
        }

        await DeleteRowAsync(s, t, probe, "Permanent delete", recycled.Id!, name, permanently: true, pageDate: recycled.Kind == "Page");
    }

    // --- Dates ----------------------------------------------------------------------------------------------------

    private static async Task DatesAsync(ProbeSession s, ProbeTarget t, string run, string stamp)
    {
        var group = await s.MustCreateChildAsync(run, $"probe dates {stamp}", CreateFileType.cftFolder);

        // Sections first (fix after run 1): the hierarchy date and a stale one, on empty sections in their own group,
        // before Phill shows any page of this run. Nothing needs to be on screen for them.
        var sectionGroup = await s.MustCreateChildAsync(group, $"probe dates secs {stamp}", CreateFileType.cftFolder);
        foreach (var candidate in new[] { "stale", "hierarchy" })
        {
            var name = $"probe dates sec {candidate} {stamp}";
            var id = await s.MustCreateChildAsync(sectionGroup, name + ".one", CreateFileType.cftSection);
            await s.EnsureNothingShownAsync(id, "DeleteHierarchy");
            var h = await NodeDateAsync(s, t, id);
            var date = candidate == "stale" ? h.AddHours(-1) : h;
            var attempt = await s.DeleteHierarchyAsync(id, date, permanently: false);
            var after = await s.NodeAsync(t.NotebookId, id);
            s.Recorder.Row($"dates-{t.Label}-section-{candidate}", "Dates", "DeleteHierarchy(soft)", new { candidate, dateSent = FormatUtc(date) }, attempt, new
            {
                hierarchyLmt = FormatUtc(h),
                applied = after is null || after.InRecycleBin,
                after,
            });
        }

        var section = await s.MustCreateChildAsync(group, $"probe dates s {stamp}.one", CreateFileType.cftSection);
        string[] notShown = ["stale", "minValue", "hierarchy", "pageXml"];
        string[] shown = ["stale", "pageXml"];
        var pages = new Dictionary<string, (string Id, string Title)>(StringComparer.Ordinal);
        foreach (var candidate in notShown)
        {
            var title = $"probe date {candidate} {stamp}";
            pages["n-" + candidate] = (await s.CreateProbePageAsync(section, title), title);
        }

        foreach (var candidate in shown)
        {
            var title = $"probe date shown {candidate} {stamp}";
            pages["d-" + candidate] = (await s.CreateProbePageAsync(section, title), title);
        }

        var where = $"{t.NotebookName} > probe run {stamp} > probe dates {stamp} > probe dates s {stamp}";
        if (t.Cloud)
        {
            // 2.2 saw a page's two dates differ only on cloud pages edited in OneNote.
            await s.PauseAsync(
                $"dates-{t.Label}-make-differ",
                $"In OneNote, open {where}. Click into each page whose title starts with \"probe date\" ({pages.Count} pages), and type one letter at the end of its text line. "
                + $"Then click the page {ProbeSession.SafePage(cloud: true)}, and wait about 30 seconds for OneDrive to sync. Then continue.");
        }

        foreach (var candidate in notShown)
        {
            var (id, title) = pages["n-" + candidate];
            if (await IsViewedAsync(s, section, id))
            {
                await s.PauseAsync($"dates-{t.Label}-hide-{candidate}", $"In OneNote, click the page {ProbeSession.SafePage(t.Cloud)}, so no page of {where} is shown. Don't type anywhere. Then continue.");
            }

            await DateRowAsync(s, t, $"dates-{t.Label}-page-{candidate}", section, id, title, candidate);
        }

        foreach (var candidate in shown)
        {
            var (id, title) = pages["d-" + candidate];
            await s.PauseAsync($"dates-{t.Label}-show-{candidate}", $"In OneNote, open {where} and click the page \"{title}\", so it is the page shown. Don't type in it. Then continue.");
            await DateRowAsync(s, t, $"dates-{t.Label}-page-shown-{candidate}", section, id, title, candidate);
        }

        // Leave nothing of this run on screen for the next step (run 1 met "OneNote is syncing changes" here).
        await s.EnsureNothingShownAsync(group, "next-step");
    }

    private static async Task DateRowAsync(ProbeSession s, ProbeTarget t, string probe, string sectionId, string pageId, string title, string candidate)
    {
        var page = await s.PageAsync(pageId);
        var node = await s.HierarchyPageAsync(sectionId, pageId);
        var pageLmt = Lmt(page);
        var hierarchyLmt = Lmt(node);
        var p = pageLmt is null ? (DateTime?)null : ParseUtc(pageLmt);
        var h = hierarchyLmt is null ? (DateTime?)null : ParseUtc(hierarchyLmt);
        DateTime? date = candidate switch
        {
            "stale" when p is not null && h is not null => (p < h ? p.Value : h.Value).AddHours(-1),
            "minValue" => DateTime.MinValue,
            "hierarchy" => h,
            "pageXml" => p,
            _ => null,
        };
        if (date is null)
        {
            s.Recorder.Row(probe, "Dates", "DeleteHierarchy(soft)", new { candidate }, null, new { notProbed = "the page or hierarchy lastModifiedTime is missing", pageLmt, hierarchyLmt });
            return;
        }

        var attempt = await s.DeleteHierarchyAsync(pageId, date.Value, permanently: false);
        var after = await s.NodeAsync(t.NotebookId, pageId);
        s.Recorder.Row(probe, "Dates", "DeleteHierarchy(soft)", new { candidate, dateSent = date.Value == DateTime.MinValue ? "DateTime.MinValue" : FormatUtc(date.Value) }, attempt, new
        {
            pageXmlLmt = pageLmt,
            hierarchyLmt,
            differ = p != h,
            displayed = (string?)node.Attribute("isCurrentlyViewed") == "true",
            applied = after is null || after.InRecycleBin,
            inRecycleBinByTitle = (await s.FindByNameAsync(t.NotebookId, title)).Any(n => n.InRecycleBin),
        });
    }

    private static async Task<bool> IsViewedAsync(ProbeSession s, string sectionId, string pageId) =>
        (string?)(await s.HierarchyPageAsync(sectionId, pageId)).Attribute("isCurrentlyViewed") == "true";

    // --- Backup ---------------------------------------------------------------------------------------------------

    private static async Task BackupAsync(ProbeSession s, ProbeTarget t, string run, string stamp)
    {
        var p = $"backup-{t.Label}";
        var group = await s.MustCreateChildAsync(run, $"probe bak {stamp}", CreateFileType.cftFolder);
        var section = await s.MustCreateChildAsync(group, $"probe bak s {stamp}.one", CreateFileType.cftSection);
        await s.CreateProbePageAsync(section, $"probe bak p1 {stamp}");
        await s.CreateProbePageAsync(section, $"probe bak p2 {stamp}");
        var sub = await s.MustCreateChildAsync(group, $"probe bak sub {stamp}", CreateFileType.cftFolder);
        var subSection = await s.MustCreateChildAsync(sub, $"probe bak sub s {stamp}.one", CreateFileType.cftSection);
        var subPage = await s.CreateProbePageAsync(subSection, $"probe bak p3 {stamp}");

        // A page in pfOneNote, for the record (AD-7 backs a page up with a page snapshot, not Publish).
        var (pageTries, _, _) = await s.PublishAsync($"backup-{t.Label}-page", subPage, [(PublishFormat.pfOneNote, $"{t.Label}-page-{stamp}.one")]);
        s.Recorder.Row($"{p}-page", "Backup", "Publish", new { kind = "page" }, null, new { tries = pageTries });

        // The section: back up, the HRESULT rows, delete, open the backup.
        await BackupDeleteOpenAsync(s, t, $"{p}-section", section, $"probe bak s {stamp}", SectionTries($"{t.Label}-section-{stamp}"), $"{t.NotebookName} > probe run {stamp} > probe bak {stamp}");

        var existing = Path.Combine(ProbeSession.PublishDir, $"{t.Label}-section-{stamp}.one");
        if (File.Exists(existing))
        {
            var again = await s.PublishOneAsync(subSection, existing, PublishFormat.pfOneNote);
            s.Recorder.Row($"{p}-existing-file", "Backup", "Publish(pfOneNote)", new { target = "a file that already exists" }, again, null);
        }
        else
        {
            // Review finding 10.
            s.Recorder.Row($"{p}-existing-file", "Backup", "Publish(pfOneNote)", new { target = "a file that already exists" }, null, new { notProbed = "the section's pfOneNote backup wasn't written, so there was no existing .one file to publish over" });
        }

        // Not re-run: in run write-probes-hierarchy-local-20261111-152447 (16:21:39 UTC) Publish into a folder that doesn't
        // exist raised OneNote's modal "We couldn't find that file." with an OK button, and the COM call didn't return
        // until it was dismissed (probe timeout at 60 s). So a backup must create its folder first.
        s.Recorder.Row($"{p}-missing-folder", "Backup", "Publish(pfOneNote)", new { target = "a file in a folder that doesn't exist" }, null, new
        {
            notProbed = "settled by run write-probes-hierarchy-local-20261111-152447: a modal 'We couldn't find that file.' blocks the call; not repeated",
        });

        // The section group, holding a nested group and its section.
        await BackupDeleteOpenAsync(s, t, $"{p}-section-group", group, $"probe bak {stamp}", GroupTries($"{t.Label}-sg-{stamp}"), $"{t.NotebookName} > probe run {stamp}");
    }

    /// <summary>Q5: <c>pfOneNote</c> to a <c>.one</c> file, then <c>pfOneNotePackage</c> if that fails.</summary>
    internal static (PublishFormat, string)[] SectionTries(string stem) =>
        [(PublishFormat.pfOneNote, stem + ".one"), (PublishFormat.pfOneNotePackage, stem + ".onepkg")];

    /// <summary>Q5 for a section group or notebook: <c>pfOneNote</c> to a <c>.one</c> name, then to a folder name, then <c>pfOneNotePackage</c>.</summary>
    internal static (PublishFormat, string)[] GroupTries(string stem) =>
        [(PublishFormat.pfOneNote, stem + ".one"), (PublishFormat.pfOneNote, stem), (PublishFormat.pfOneNotePackage, stem + ".onepkg")];

    /// <summary>
    /// Q4's backup check: publish <paramref name="id"/>; only if that worked, permanently delete it; then open the backup
    /// and compare its page names, page count and <c>encrypted</c> with the read before the delete; then Phill closes it.
    /// </summary>
    internal static async Task BackupDeleteOpenAsync(ProbeSession s, ProbeTarget t, string probe, string id, string name, (PublishFormat, string)[] tries, string where)
    {
        var tree = await NodeElementAsync(s, t, id);
        var before = Summary(tree);
        var (attempts, path, last) = await s.PublishAsync(probe, id, tries);
        s.Recorder.Row(probe + "-publish", "Backup", "Publish", new { kind = tree.Name.LocalName }, last, new { tries = attempts, published = path is null ? null : Path.GetFileName(path), disk = ProbeSession.Disk(path) });
        if (path is null)
        {
            s.Recorder.Row(probe + "-delete", "Permanent delete", "DeleteHierarchy(permanent)", null, null, new { notProbed = "no backup succeeded, so the delete wasn't run (AD-7: a failed backup refuses the delete)" });
            return;
        }

        await DeleteRowAsync(s, t, probe + "-delete", "Permanent delete", id, name, permanently: true, pageDate: false);
        await OpenAndCompareAsync(s, probe, path, before, where);
    }

    /// <summary>Opens a backup, compares it with <paramref name="before"/>, and pauses for Phill to close it.</summary>
    internal static async Task OpenAndCompareAsync(ProbeSession s, string probe, string path, BackupSummary before, string where)
    {
        var (attempt, openedId, opened) = await s.OpenBackupAsync(path);
        BackupSummary? after = null;
        if (attempt.Ok && openedId is not null)
        {
            after = Summary(XDocument.Parse(await s.Gateway.GetHierarchyAsync(openedId, OneNoteMcp.Core.HierarchyScope.Pages, ProbeSession.Ct)).Root!);
        }

        s.Recorder.Row(probe + "-open-backup", "Backup", "OpenHierarchy(backup)", new { opened = opened is null ? null : Path.GetFileName(opened) }, attempt, new
        {
            before,
            after,
            samePageNames = after is not null && before.PageNames.SequenceEqual(after.PageNames),
            samePageCount = after is not null && before.PageNames.Count == after.PageNames.Count,
        });

        if (attempt.Ok && openedId is not null)
        {
            var label = after?.Name ?? before.Name;
            await s.PauseAsync(
                probe + "-close-backup",
                $"A backup copy named \"{label}\" just opened in OneNote (it was published from {where}; it may show outside any notebook, under Open Sections or Misc, or as a notebook). "
                + "Close that copy: right-click it > Close This Section (or Close This Notebook). Don't delete it, and don't close anything else. Then continue.");
        }
    }

    /// <summary>The backup comparison: node kind and name, <c>encrypted</c>, <c>locked</c>, and every page name below it, in order.</summary>
    internal static BackupSummary Summary(XElement node) =>
        new(
            node.Name.LocalName,
            (string?)node.Attribute("name"),
            (string?)node.Attribute("encrypted"),
            (string?)node.Attribute("locked"),
            node.DescendantsAndSelf(node.Name.Namespace + "Page")
                .Where(pg => !pg.Ancestors().Any(a => (string?)a.Attribute("isRecycleBin") == "true"))
                .Select(pg => (string?)pg.Attribute("name") ?? string.Empty)
                .ToList());

    // --- HRESULTs -------------------------------------------------------------------------------------------------

    internal static async Task HResultsAsync(ProbeSession s, ProbeTarget t, string run, string stamp)
    {
        const string Q = "HRESULTs";
        var p = $"hr-{t.Label}";
        var group = await s.MustCreateChildAsync(run, $"probe hr {stamp}", CreateFileType.cftFolder);
        var section = await s.MustCreateChildAsync(group, $"probe hr s {stamp}.one", CreateFileType.cftSection);

        var malformed = $"""<?xml version="1.0"?><one:Section xmlns:one="{OneNoteMcp.Xml.OneNoteXml.Namespace2013}" ID="{Attr(section)}" name="x">""";
        s.Recorder.Row($"{p}-update-malformed", Q, "UpdateHierarchy", new { shape = "unclosed Section root" }, await s.UpdateHierarchyAsync(malformed), null);
        s.Recorder.Row($"{p}-update-unknown-element", Q, "UpdateHierarchy", new { shape = "Section root with a one:Bogus child" }, await s.UpdateHierarchyAsync(Hierarchy("Section", section, string.Empty, "<one:Bogus />")), null);
        s.Recorder.Row($"{p}-update-invalid-name", Q, "UpdateHierarchy", new { shape = "Section renamed to a name with * and ?" }, await s.UpdateHierarchyAsync(Hierarchy("Section", section, A("name", $"probe bad*? {stamp}"))), new
        {
            after = await s.NodeAsync(t.NotebookId, section),
        });

        // A section this run made and permanently deleted: a stale ID for each method.
        var gone = await s.MustCreateChildAsync(group, $"probe hr gone {stamp}.one", CreateFileType.cftSection);
        await s.EnsureNothingShownAsync(gone, "DeleteHierarchy"); // before the read, so a pause can't come between it and the call
        var goneDate = await NodeDateAsync(s, t, gone);
        var deleted = await s.DeleteHierarchyAsync(gone, goneDate, permanently: true);
        if (!deleted.Ok || await s.ExistsAnywhereAsync(gone))
        {
            s.Recorder.Row($"{p}-gone-setup", Q, "DeleteHierarchy(permanent)", null, deleted, new { notProbed = "the stale-ID section couldn't be deleted" });
        }
        else
        {
            s.Recorder.Row($"{p}-update-gone-id", Q, "UpdateHierarchy", new { shape = "rename of a permanently deleted section" }, await s.UpdateHierarchyAsync(Hierarchy("Section", gone, A("name", $"probe hr renamed {stamp}"))), null);
            s.Recorder.Row($"{p}-delete-gone-soft", Q, "DeleteHierarchy(soft)", new { target = "a permanently deleted section" }, await s.DeleteHierarchyAsync(gone, goneDate, permanently: false), null);
            s.Recorder.Row($"{p}-delete-gone-permanent", Q, "DeleteHierarchy(permanent)", new { target = "a permanently deleted section" }, await s.DeleteHierarchyAsync(gone, goneDate, permanently: true), null);
            var publishGone = await s.TryGuardedAsync("Publish-gone", () => TestNotebook.WriteToGoneAsync(
                s.Gateway,
                gone,
                app =>
                {
                    app.Publish(gone, Path.Combine(ProbeSession.PublishDir, $"gone-{t.Label}-{stamp}.one"), PublishFormat.pfOneNote, string.Empty);
                    return true;
                },
                "Publish(gone)",
                s.Created,
                ProbeSession.Ct));
            s.Recorder.Row($"{p}-publish-gone", Q, "Publish(pfOneNote)", new { source = "a permanently deleted section" }, publishGone, null);
        }

        var (missing, _, _) = await s.OpenBackupAsync(Path.Combine(ProbeSession.PublishDir, $"missing-{t.Label}-{stamp}.one"));
        s.Recorder.Row($"{p}-open-missing-file", Q, "OpenHierarchy(cftNone)", new { path = "a .one file that doesn't exist, in the publish folder" }, missing, null);
    }

    // --- Modal dialog ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Q6: with a dialog open (Phill opens it at a pause), one call of each hierarchy method on objects this run made,
    /// each read back; then Phill closes the dialog.
    /// </summary>
    internal static async Task ModalAsync(ProbeSession s, ProbeTarget t, string run, string stamp, string dialog, string open, string close)
    {
        const string Q = "Modal dialog";
        var group = await s.MustCreateChildAsync(run, $"probe modal {dialog} {stamp}", CreateFileType.cftFolder);
        var section = await s.MustCreateChildAsync(group, $"probe modal s {dialog} {stamp}.one", CreateFileType.cftSection);
        var other = await s.MustCreateChildAsync(group, $"probe modal t {dialog} {stamp}.one", CreateFileType.cftSection);
        var soft = await s.CreateProbePageAsync(section, $"probe modal soft {dialog} {stamp}");
        var hard = await s.CreateProbePageAsync(section, $"probe modal hard {dialog} {stamp}");
        var softDate = ParseUtc(Lmt(await s.PageAsync(soft))!);
        var hardDate = ParseUtc(Lmt(await s.PageAsync(hard))!);

        if (!await s.PauseAsync($"modal-{dialog}-open", open))
        {
            s.Recorder.Row($"modal-{dialog}", Q, "-", null, null, new { notProbed = "pause skipped" });
            return;
        }

        var p = $"modal-{dialog}";
        s.ShownCheckOff = true; // the dialog may sit on a shown section, and Phill can't click away until it closes
        try
        {
            // Review finding 2: a plain read first, so a read the dialog blocks shows as its own row. The writes'
            // guard reads run inside their attempts, so a write row's HRESULT may come from that read; this row tells.
            var read = await s.TryReadAsync("GetHierarchy", app => app.GetHierarchy(t.NotebookId, HierarchyScope.hsSections, out _, XMLSchema.xs2013));
            s.Recorder.Row($"{p}-get-hierarchy", Q, "GetHierarchy", new { scope = "sections" }, read, null);
            await s.CheckTimeoutAsync("GetHierarchy", read);

            var (create, createdId) = await s.CreateChildAsync(group, $"probe modal new {dialog} {stamp}.one", CreateFileType.cftSection);
            s.Recorder.Row($"{p}-open-hierarchy", Q, "OpenHierarchy(cftSection)", null, create, new { applied = await SafeAsync(async () => createdId is not null && await s.NodeAsync(t.NotebookId, createdId) is not null) });

            var rename = await s.UpdateHierarchyAsync(Hierarchy("Section", other, A("name", $"probe modal renamed {dialog} {stamp}")));
            s.Recorder.Row($"{p}-update-hierarchy", Q, "UpdateHierarchy", null, rename, new { applied = await SafeAsync(async () => (await s.NodeAsync(t.NotebookId, other))?.Name == $"probe modal renamed {dialog} {stamp}") });

            var softAttempt = await s.DeleteHierarchyAsync(soft, softDate, permanently: false);
            s.Recorder.Row($"{p}-delete-hierarchy-soft", Q, "DeleteHierarchy(soft)", null, softAttempt, new { applied = await SafeAsync(async () => (await s.NodeAsync(t.NotebookId, soft)) is not { InRecycleBin: false }) });

            var hardAttempt = await s.DeleteHierarchyAsync(hard, hardDate, permanently: true);
            s.Recorder.Row($"{p}-delete-hierarchy-permanent", Q, "DeleteHierarchy(permanent)", null, hardAttempt, new { applied = await SafeAsync(async () => !await s.ExistsAnywhereAsync(hard)) });

            var target = Path.Combine(ProbeSession.PublishDir, $"modal-{dialog}-{t.Label}-{stamp}.one");
            var publish = await s.PublishOneAsync(section, target, PublishFormat.pfOneNote);
            s.Recorder.Row($"{p}-publish", Q, "Publish(pfOneNote)", null, publish, new { disk = ProbeSession.Disk(target) });
        }
        finally
        {
            // Review finding 2: whatever happened above, Phill is told to close the dialog.
            s.ShownCheckOff = false;
            await s.PauseAsync($"modal-{dialog}-close", close);
        }
    }

    /// <summary>A read-back that never throws: its value, or what failed (a dialog may block reads).</summary>
    private static async Task<object?> SafeAsync<T>(Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (OneNoteMcp.Core.OneNoteException ex) when (ex.Code is not (OneNoteMcp.Core.ErrorCode.Timeout or OneNoteMcp.Core.ErrorCode.ComBusy))
        {
            return new { readFailed = ex.Code.ToString(), ex.Message };
        }
    }

    // --- Helpers --------------------------------------------------------------------------------------------------

    /// <summary>The node's element in a fresh pages-scope read of the notebook.</summary>
    internal static async Task<XElement> NodeElementAsync(ProbeSession s, ProbeTarget t, string id) =>
        ById(await s.TreeAsync(t.NotebookId), id) ?? throw new InvalidOperationException($"{id} is not in {t.Label}.");

    /// <summary>A section's, section group's or notebook's hierarchy <c>lastModifiedTime</c> (AD-17's basis for it), UTC.</summary>
    internal static async Task<DateTime> NodeDateAsync(ProbeSession s, ProbeTarget t, string id)
    {
        var node = ById(await s.TreeAsync(t.NotebookId), id)
            ?? (await s.NotebooksAsync()).Elements(One + "Notebook").FirstOrDefault(n => (string?)n.Attribute("ID") == id);
        var lmt = Lmt(node);
        return lmt is null ? DateTime.MinValue : ParseUtc(lmt);
    }
}

/// <summary>What a backup is compared on (Q4).</summary>
internal sealed record BackupSummary(string Kind, string? Name, string? Encrypted, string? Locked, IReadOnlyList<string> PageNames);
