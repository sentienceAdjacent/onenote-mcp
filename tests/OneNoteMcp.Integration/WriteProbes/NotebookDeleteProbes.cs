using System.Xml.Linq;
using OneNoteMcp.Com.Interop;
using Xunit;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Story 5.1, scenario <c>write-probes-notebook-delete</c> (Q1 a, Q2 b): notebook-level create, rename, delete and
/// backup, on throwaway notebooks only, never on MCP Test or MCP Test Cloud.
/// <list type="bullet">
/// <item>Local: the probe creates <c>MCP Test Throwaway &lt;stamp&gt; a</c>, <c>b</c> and <c>c</c> beside MCP Test's folder
/// (<see cref="TestNotebook.CreateThrowawayNotebookAsync"/>). <c>a</c>: the existing-path probe, renames, a soft delete
/// and a permanent delete. <c>b</c>: the backup, a permanent delete and the backup opened. <c>c</c>: created in a folder
/// that already holds a file, then permanently deleted.</item>
/// <item>Cloud: Phill creates <c>MCP Test Throwaway Cloud</c> at a pause; the probe finds it by exact name and OneDrive
/// path, backs it up, soft-deletes it, permanently deletes it, and opens the backup. Phill then checks OneDrive.</item>
/// </list>
/// </summary>
public sealed class NotebookDeleteProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "write-probes-notebook-delete";

    [Fact]
    public async Task WriteProbesNotebookDelete()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.CloudProbeCallSeconds);
        var stamp = HierarchyProbes.Stamp();
        var nameA = $"{TestNotebook.ThrowawayNamePrefix}{stamp} a";
        var nameB = $"{TestNotebook.ThrowawayNamePrefix}{stamp} b";
        var nameC = $"{TestNotebook.ThrowawayNamePrefix}{stamp} c";
        string? a = null;
        string? b = null;
        string? c = null;

        await s.StepAsync("nb-create", async () =>
        {
            var focus = ProbeSession.Foreground();
            var (attempt, id) = await s.CreateThrowawayAsync(nameA, requireEmpty: true);
            a = id;
            s.Recorder.Row("nb-create-local", "Create", "OpenHierarchy(cftNotebook)", new { path = "<throwaway root>\\MCP Test Throwaway <stamp> a", folder = "absent" }, attempt, new
            {
                id,
                notebook = id is null ? null : await NotebookAsync(s, id),
                focusKept = focus == ProbeSession.Foreground(),
            });
            if (id is not null)
            {
                await FillAsync(s, id, stamp, "a");
            }
        });

        await s.StepAsync("nb-create-existing-path", async () =>
        {
            if (a is null)
            {
                Missing(s, "nb-create-existing-path", "throwaway a", "nb-create");
                return;
            }

            var (attempt, id) = await ProbeSession.TryValueAsync(() => TestNotebook.ReopenThrowawayNotebookAsync(s.Gateway, a, s.Throwaways[a], ProbeSession.Ct));
            await s.CheckTimeoutAsync("OpenHierarchy-existing-path", attempt);
            s.Recorder.Row("nb-create-existing-path", "Create", "OpenHierarchy(cftNotebook)", new { path = "the path of throwaway a, already open" }, attempt, new { sameId = id == a, id });
        });

        await s.StepAsync("nb-create-nonempty", async () =>
        {
            var folder = Path.Combine(ProbeSession.ThrowawayRoot, nameC);
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, TestNotebook.ProbeMarkerFile), "story 5.1 probe marker", ProbeSession.Ct);
            var (attempt, id) = await s.CreateThrowawayAsync(nameC, requireEmpty: false);
            c = id;
            s.Recorder.Row("nb-create-nonempty", "Create", "OpenHierarchy(cftNotebook)", new { path = "<throwaway root>\\MCP Test Throwaway <stamp> c", folder = "holds one text file" }, attempt, new
            {
                id,
                notebook = id is null ? null : await NotebookAsync(s, id),
                folderAfter = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).ToList(),
            });
        });

        await s.StepAsync("nb-rename", async () =>
        {
            if (a is null)
            {
                Missing(s, "nb-rename", "throwaway a", "nb-create");
                return;
            }

            var before = await NotebookAsync(s, a);
            s.AllowThrowawayName(a, nameA + " renamed"); // the guard accepts only names this run gave it (review finding 8)
            var renamed = await s.UpdateHierarchyAsync(Hierarchy("Notebook", a, A("name", nameA + " renamed")));
            var afterName = await NotebookAsync(s, a);
            s.Recorder.Row("nb-rename-name", "HierarchyWrite", "UpdateHierarchy", new { shape = "Notebook root with a new name" }, renamed, new
            {
                before,
                after = afterName,
                diskOldPath = ProbeSession.Disk(before?.Path),
                diskNewPath = ProbeSession.Disk(afterName?.Path),
            });

            var nick = await s.UpdateHierarchyAsync(Hierarchy("Notebook", a, A("nickname", nameA + " nick")));
            s.Recorder.Row("nb-rename-nickname", "HierarchyWrite", "UpdateHierarchy", new { shape = "Notebook root with a new nickname" }, nick, new { after = await NotebookAsync(s, a) });
        });

        await s.StepAsync("nb-soft-delete", async () =>
        {
            if (a is null)
            {
                Missing(s, "nb-soft-delete", "throwaway a", "nb-create");
                return;
            }

            await NotebookDeleteRowAsync(s, "nb-soft-delete-local", a, permanently: false);
        });

        await s.StepAsync("nb-permanent-delete-after-soft", async () =>
        {
            if (a is null)
            {
                Missing(s, "nb-permanent-delete-after-soft", "throwaway a", "nb-create");
                return;
            }

            if (await NotebookAsync(s, a) is null)
            {
                s.Recorder.Row("nb-permanent-delete-after-soft", "Permanent delete", "DeleteHierarchy(permanent)", null, null, new
                {
                    notProbed = "throwaway a is no longer open after the soft delete",
                    disk = ProbeSession.Disk(Path.Combine(ProbeSession.ThrowawayRoot, nameA)),
                });
                return;
            }

            await NotebookDeleteRowAsync(s, "nb-permanent-delete-after-soft", a, permanently: true);
        });

        await s.StepAsync("nb-backup-delete-open", async () =>
        {
            var (attempt, id) = await s.CreateThrowawayAsync(nameB, requireEmpty: true);
            b = id;
            s.Recorder.Row("nb-create-local-b", "Create", "OpenHierarchy(cftNotebook)", null, attempt, new { id });
            if (id is null)
            {
                return;
            }

            await FillAsync(s, id, stamp, "b");
            await BackupDeleteOpenAsync(s, "nb-local", id, HierarchyProbes.GroupTries($"nb-local-{stamp}"), "the throwaway notebook b");
        });

        await s.StepAsync("nb-cleanup-nonempty", async () =>
        {
            if (c is null)
            {
                Missing(s, "nb-cleanup-nonempty", "throwaway c", "nb-create-nonempty");
                return;
            }

            if (await NotebookAsync(s, c) is null)
            {
                s.Recorder.Row("nb-permanent-delete-nonempty", "Permanent delete", "DeleteHierarchy(permanent)", null, null, new { notProbed = "throwaway c is no longer open" });
                return;
            }

            await NotebookDeleteRowAsync(s, "nb-permanent-delete-nonempty", c, permanently: true);
        });

        // --- Cloud: Phill makes the notebook; the probe never creates one on OneDrive (Q2 b). ---
        string? cloud = null;
        await s.StepAsync("nb-cloud-find", async () =>
        {
            cloud = await s.AdoptCloudThrowawayAsync();
            if (cloud is null)
            {
                await s.PauseAsync(
                    "nb-cloud-create",
                    $"In OneNote, create a new notebook on OneDrive named exactly \"{TestNotebook.CloudThrowawayName}\" (File > New > OneDrive > Documents). "
                    + "Add one section named \"probe\" with one page titled \"probe page\" and a word of text on it. Wait until it has synced. Then continue.");
                cloud = await s.AdoptCloudThrowawayAsync();
            }

            s.Recorder.Row("nb-cloud-find", "Setup", "GetHierarchy", null, null, new { found = cloud is not null, notebook = cloud is null ? null : await NotebookAsync(s, cloud) });
        });

        await s.StepAsync("nb-cloud-backup-delete-open", async () =>
        {
            if (cloud is null)
            {
                Missing(s, "nb-cloud-backup-delete-open", "the cloud throwaway", "nb-cloud-find");
                return;
            }

            // Review finding 5: if the soft delete takes it out of the open list, Phill reopens it from OneDrive so the
            // permanent delete still runs. A reopen gives it new IDs, so it is adopted again.
            async Task<string?> ReopenCloudAsync()
            {
                var reopened = await s.PauseAsync(
                    "nb-cloud-reopen",
                    $"The notebook \"{TestNotebook.CloudThrowawayName}\" was just soft-deleted and is no longer open in OneNote. If OneDrive still has it, reopen it: "
                    + $"in OneNote, File > Open > OneDrive > Documents > \"{TestNotebook.CloudThrowawayName}\", and wait until it has synced. Then continue. "
                    + "If it isn't in Documents (gone, or only in the OneDrive recycle bin), don't restore it: replace this file's content with skip.");
                return reopened ? await s.AdoptCloudThrowawayAsync() : null;
            }

            await BackupDeleteOpenAsync(s, "nb-cloud", cloud, HierarchyProbes.GroupTries($"nb-cloud-{stamp}"), TestNotebook.CloudThrowawayName, softFirst: true, reopen: ReopenCloudAsync);
            await s.PauseAsync(
                "nb-cloud-onedrive-check",
                $"In a browser, open OneDrive > Documents and its recycle bin. Note whether \"{TestNotebook.CloudThrowawayName}\" is still in Documents, is in the recycle bin, or is gone, and tell the team lead. Don't restore it. Then continue.");
        });

        s.Finish();
    }

    /// <summary>A section with two pages and a section group with a section and a page, so the backup has content to compare.</summary>
    private static async Task FillAsync(ProbeSession s, string notebookId, string stamp, string tag)
    {
        var section = await s.MustCreateChildAsync(notebookId, $"probe nb {tag} {stamp}.one", CreateFileType.cftSection);
        await s.CreateProbePageAsync(section, $"probe nb {tag} p1 {stamp}");
        await s.CreateProbePageAsync(section, $"probe nb {tag} p2 {stamp}");
        var group = await s.MustCreateChildAsync(notebookId, $"probe nb {tag} sg {stamp}", CreateFileType.cftFolder);
        var nested = await s.MustCreateChildAsync(group, $"probe nb {tag} nested {stamp}.one", CreateFileType.cftSection);
        await s.CreateProbePageAsync(nested, $"probe nb {tag} p3 {stamp}");
    }

    /// <summary>
    /// The backup check for a notebook (Q4): publish; on the cloud, a soft delete first when <paramref name="softFirst"/>;
    /// a permanent delete only after a backup succeeded (if the soft delete closed the notebook, <paramref name="reopen"/>
    /// gets it back, under new IDs); the backup opened and compared; Phill closes it.
    /// </summary>
    private static async Task BackupDeleteOpenAsync(ProbeSession s, string probe, string id, (PublishFormat, string)[] tries, string what, bool softFirst = false, Func<Task<string?>>? reopen = null)
    {
        var before = HierarchyProbes.Summary(XDocument.Parse(await s.Gateway.GetHierarchyAsync(id, OneNoteMcp.Core.HierarchyScope.Pages, ProbeSession.Ct)).Root!);
        var (attempts, path, last) = await s.PublishAsync(probe, id, tries);
        s.Recorder.Row(probe + "-publish", "Backup", "Publish", new { kind = "Notebook" }, last, new { tries = attempts, published = path is null ? null : Path.GetFileName(path), disk = ProbeSession.Disk(path), before });

        if (softFirst)
        {
            await NotebookDeleteRowAsync(s, probe + "-soft-delete", id, permanently: false);
        }

        if (path is null)
        {
            s.Recorder.Row(probe + "-permanent-delete", "Permanent delete", "DeleteHierarchy(permanent)", null, null, new { notProbed = "no backup succeeded, so the delete wasn't run (AD-7)" });
            return;
        }

        if (await NotebookAsync(s, id) is null && reopen is not null)
        {
            var reopened = await reopen();
            s.Recorder.Row(probe + "-reopen", "Soft delete", "GetHierarchy", null, null, new
            {
                reopened = reopened is not null,
                sameId = reopened == id,
                notebook = reopened is null ? null : await NotebookAsync(s, reopened),
                pages = reopened is null ? null : HierarchyProbes.Summary(XDocument.Parse(await s.Gateway.GetHierarchyAsync(reopened, OneNoteMcp.Core.HierarchyScope.Pages, ProbeSession.Ct)).Root!),
            });
            id = reopened ?? id;
        }

        if (await NotebookAsync(s, id) is null)
        {
            s.Recorder.Row(probe + "-permanent-delete", "Permanent delete", "DeleteHierarchy(permanent)", null, null, new { notProbed = "the notebook is no longer open after the soft delete, and wasn't reopened" });
        }
        else
        {
            await NotebookDeleteRowAsync(s, probe + "-permanent-delete", id, permanently: true);
        }

        await HierarchyProbes.OpenAndCompareAsync(s, probe, path, before, what);
    }

    /// <summary>One notebook delete row: the hierarchy date, then whether the notebook is still open, and its folder on disk (local).</summary>
    private static async Task NotebookDeleteRowAsync(ProbeSession s, string probe, string id, bool permanently)
    {
        await s.EnsureNothingShownAsync(id, "DeleteHierarchy"); // before the reads, so a pause can't come between them and the call
        var before = await NotebookAsync(s, id);
        var diskBefore = ProbeSession.Disk(before?.Path);
        var date = before?.LastModifiedTime is { } lmt ? ParseUtc(lmt) : DateTime.MinValue;
        var attempt = await s.DeleteHierarchyAsync(id, date, permanently);
        var after = await NotebookAsync(s, id);
        var byName = before?.Name is null
            ? new List<NodeInfo>()
            : (await s.NotebooksAsync()).Elements(One + "Notebook").Where(n => (string?)n.Attribute("name") == before.Name).Select(NodeInfo.From).ToList();
        s.Recorder.Row(probe, permanently ? "Permanent delete" : "Soft delete", permanently ? "DeleteHierarchy(permanent)" : "DeleteHierarchy(soft)", new { kind = "Notebook", dateSent = date == DateTime.MinValue ? "DateTime.MinValue" : FormatUtc(date) }, attempt, new
        {
            before,
            stillOpen = after,
            openByName = byName,
            existsAnywhere = await s.ExistsAnywhereAsync(id),
            diskBefore,
            diskAfter = ProbeSession.Disk(before?.Path),
        });
    }

    /// <summary>Review finding 9: a step whose setup step didn't run (a failure, or a step filter) says so in a row.</summary>
    private static void Missing(ProbeSession s, string step, string what, string setupStep) =>
        s.Recorder.Row(step, "Setup", "-", null, null, new { notProbed = $"no {what}: step {setupStep} didn't run or failed (include it in ONENOTE_MCP_PROBE_STEPS)" });

    /// <summary>The open notebook with ID <paramref name="id"/>, or null.</summary>
    private static async Task<NodeInfo?> NotebookAsync(ProbeSession s, string id) =>
        (await s.NotebooksAsync()).Elements(One + "Notebook").Where(n => (string?)n.Attribute("ID") == id).Select(NodeInfo.From).FirstOrDefault();
}
