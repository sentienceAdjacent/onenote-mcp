using OneNoteMcp.Com.Interop;
using Xunit;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Story 5.1, scenario <c>write-probes-protected-delete</c> (Q3 b, Q6 b): deletes in a password-protected section, on
/// a throwaway section this run makes in MCP Test and then in MCP Test Cloud, which Phill password-protects at a pause
/// (COM can't set a password). Ballast Vault is never touched: every delete refuses an ID this run didn't make. Per
/// notebook: a soft delete of a page in the unlocked section; with it locked, a create under it, a <c>Publish</c> and a
/// page delete; on MCP Test only, the modal probe with the section's password dialog open; unlocked again, the backup;
/// the section soft-deleted whole and its recycle-bin copy read locked and unlocked; its permanent delete; the backup
/// opened and compared.
/// </summary>
public sealed class ProtectedDeleteProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "write-probes-protected-delete";

    [Fact]
    public async Task WriteProbesProtectedDelete()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.CloudProbeCallSeconds);
        var stamp = HierarchyProbes.Stamp();

        var local = new ProbeTarget("protected-local", await s.SetupAsync("setup", s.McpTestIdAsync), TestNotebook.NotebookName, Cloud: false);
        await RunAsync(s, local, stamp);

        var cloudId = await s.SetupAsync("setup-cloud", () => TestNotebook.FindCloudAsync(s.Gateway, ProbeSession.Ct));
        if (cloudId is null)
        {
            s.Recorder.Row("protected-cloud", "Protected", "-", null, null, new { notProbed = "MCP Test Cloud is not open" });
        }
        else
        {
            await RunAsync(s, new ProbeTarget("protected-cloud", cloudId, TestNotebook.CloudNotebookName, Cloud: true), stamp);
        }

        s.Finish();
    }

    private static async Task RunAsync(ProbeSession s, ProbeTarget t, string stamp)
    {
        const string Q = "Protected";
        var p = t.Label;
        var vaultName = $"probe vault {stamp}";
        var where = $"{t.NotebookName} > probe run {stamp}";
        string? run = null;
        string? vault = null;
        var pages = new List<(string Id, string Title, DateTime Date)>();
        var ready = false;

        await s.StepAsync($"{p}-setup", async () =>
        {
            run = await HierarchyProbes.RunGroupAsync(s, t, stamp);
            vault = await s.MustCreateChildAsync(run, vaultName + ".one", CreateFileType.cftSection);
            for (var i = 1; i <= 3; i++)
            {
                var title = $"probe vault p{i} {stamp}";
                var id = await s.CreateProbePageAsync(vault, title);
                pages.Add((id, title, ParseUtc(Lmt(await s.PageAsync(id))!)));
            }
        });
        if (vault is null || run is null)
        {
            // Review finding 9: say why nothing else ran (a failed setup, or a step filter that left it out).
            s.Recorder.Row($"{p}-stopped", Q, "-", null, null, new { notProbed = $"no protected section: step {p}-setup didn't run or failed; every {p} step needs it (include it in ONENOTE_MCP_PROBE_STEPS)" });
            return;
        }

        var vaultId = vault;
        var runId = run;

        await s.StepAsync($"{p}-protect", async () =>
        {
            await s.PauseAsync(
                $"{p}-protect",
                $"In OneNote, open {where} and password-protect the section \"{vaultName}\": right-click its tab > Password Protect This Section, and set a password you'll remember for this sitting. "
                + "Leave it unlocked (if it locks, unlock it with that password). Don't touch Ballast Vault or any other section. Then continue.");
            // Run 3 (2026-11-12): after Phill protected the local section, its old ID was gone, so look it up by name and
            // record whether protecting a section gives it (and its pages) new IDs. FindByNameAsync adds a probe-named
            // node to Created, so the deletes below still accept the new ID.
            var oldId = vaultId;
            var oldPageIds = pages.Select(x => x.Id).ToList();
            var node = await s.NodeAsync(t.NotebookId, vaultId);
            if (node is null)
            {
                node = (await s.FindByNameAsync(t.NotebookId, vaultName)).FirstOrDefault(n => n.Kind == "Section" && !n.InRecycleBin && n.Id is not null);
                if (node is not null)
                {
                    vaultId = node.Id!;
                    for (var i = 0; i < pages.Count; i++)
                    {
                        if (await s.PageIdByTitleAsync(vaultId, pages[i].Title) is { } newPageId)
                        {
                            s.Created.Add(newPageId);
                            pages[i] = (newPageId, pages[i].Title, ParseUtc(Lmt(await s.PageAsync(newPageId))!));
                        }
                    }
                }
            }

            ready = node?.Encrypted is "true" or "1";
            s.Recorder.Row($"{p}-protected", Q, "GetHierarchy", null, null, new
            {
                node,
                ready,
                oldId,
                newId = vaultId,
                sectionIdChanged = oldId != vaultId,
                pageIdsChanged = pages.Select((x, i) => x.Id != oldPageIds[i]).Count(changed => changed),
            });
        });
        if (!ready)
        {
            s.Recorder.Row($"{p}-stopped", Q, "-", null, null, new { notProbed = "the section isn't encrypted, so no protected-section probe ran" });
            return;
        }

        // A page soft-deleted from the unlocked section: recycle bin, refused, or gone for good?
        await s.StepAsync($"{p}-soft-delete-page", async () =>
        {
            var (id, title, _) = pages[0];
            var attempt = await HierarchyProbes.DeleteRowAsync(s, t, $"{p}-soft-delete-page", Q, id, title, permanently: false, pageDate: true);
            s.Recorder.Row($"{p}-soft-delete-page-where", Q, "GetHierarchy", null, attempt, new
            {
                byTitle = await RecycleViewAsync(s, t, title),
            });
        });

        await s.StepAsync($"{p}-locked", async () =>
        {
            await s.PauseAsync($"{p}-lock", $"In OneNote, lock the section \"{vaultName}\" in {where}: press Ctrl+Alt+L (locks all protected sections). Then continue.");
            s.Recorder.Row($"{p}-locked-state", Q, "GetHierarchy", null, null, new { node = await s.NodeAsync(t.NotebookId, vaultId) });

            var (create, createdId) = await s.CreateChildAsync(vaultId, $"probe locked child {stamp}.one", CreateFileType.cftSection);
            s.Recorder.Row($"{p}-create-under-locked", "Create", "OpenHierarchy(cftSection)", new { parent = "a locked protected section" }, create, new
            {
                id = createdId,
                found = await s.FindByNameAsync(t.NotebookId, $"probe locked child {stamp}"),
            });

            var (tries, path, last) = await s.PublishAsync($"{p}-publish-locked", vaultId, [(PublishFormat.pfOneNote, $"{p}-locked-{stamp}.one")]);
            s.Recorder.Row($"{p}-publish-locked", "Backup", "Publish(pfOneNote)", new { source = "a locked protected section" }, last, new { tries, disk = ProbeSession.Disk(path) });

            var (pageId, pageTitle, pageDate) = pages[1];
            var deleteLocked = await s.DeleteHierarchyAsync(pageId, pageDate, permanently: false);
            s.Recorder.Row($"{p}-soft-delete-page-locked", Q, "DeleteHierarchy(soft)", new { date = "the page XML's, read before the lock" }, deleteLocked, new
            {
                byTitle = await RecycleViewAsync(s, t, pageTitle),
            });
        });

        if (!t.Cloud)
        {
            await s.StepAsync("modal-password", () => HierarchyProbes.ModalAsync(
                s,
                t,
                runId,
                stamp,
                "password",
                $"In OneNote, click the locked section \"{vaultName}\" in {where}, then click its \"click here to unlock\" text so the password dialog opens. "
                + "Leave the dialog open without typing. Then continue.",
                $"In OneNote, cancel the password dialog, leaving \"{vaultName}\" locked. Then continue."));
        }

        var backup = (string?)null;
        BackupSummary? summary = null;
        await s.StepAsync($"{p}-backup", async () =>
        {
            await s.PauseAsync(
                $"{p}-unlock",
                $"In OneNote, unlock the section \"{vaultName}\" in {where} with its password. Then click the page {Safe(t)} so the section is no longer on screen. "
                + "If the section locks again when you leave it, tell the team lead. Then continue.");
            await s.EnsureNothingShownAsync(vaultId, "Publish"); // before the read, so the row shows the state at the call
            summary = HierarchyProbes.Summary(await HierarchyProbes.NodeElementAsync(s, t, vaultId));
            var (tries, path, last) = await s.PublishAsync($"{p}-backup", vaultId, HierarchyProbes.SectionTries($"{p}-vault-{stamp}"));
            backup = path;
            s.Recorder.Row($"{p}-backup-publish", "Backup", "Publish", new { source = "an unlocked protected section" }, last, new { tries, before = summary, disk = ProbeSession.Disk(path) });
        });

        // The section soft-deleted whole: does encrypted survive in the recycle bin, locked and unlocked?
        await s.StepAsync($"{p}-soft-delete-section", async () =>
        {
            await HierarchyProbes.DeleteRowAsync(s, t, $"{p}-soft-delete-section", Q, vaultId, vaultName, permanently: false, pageDate: false);
            await s.PauseAsync($"{p}-recycled-lock", "In OneNote, press Ctrl+Alt+L to lock all protected sections (this includes the deleted section in the notebook's recycle bin). Then continue.");
            s.Recorder.Row($"{p}-recycled-locked", Q, "GetHierarchy", null, null, new { byName = await RecycleViewAsync(s, t, vaultName) });
            await s.PauseAsync(
                $"{p}-recycled-unlock",
                $"In OneNote, open {t.NotebookName}'s recycle bin (History > Notebook Recycle Bin), open the deleted section \"{vaultName}\" and unlock it with its password. "
                + $"Don't restore it. Then click the page {Safe(t)} so it is no longer on screen. If it locks again when you leave it, tell the team lead. Then continue.");
            s.Recorder.Row($"{p}-recycled-unlocked", Q, "GetHierarchy", null, null, new { byName = await RecycleViewAsync(s, t, vaultName) });
        });

        await s.StepAsync($"{p}-permanent-delete", async () =>
        {
            var recycled = (await s.FindByNameAsync(t.NotebookId, vaultName)).FirstOrDefault(n => n.Id is not null);
            if (recycled is null)
            {
                s.Recorder.Row($"{p}-permanent-delete", Q, "DeleteHierarchy(permanent)", null, null, new { notProbed = "the section wasn't found by name" });
                return;
            }

            // Review finding 3 (Phill 2026-11-10): the delete runs either way, since it answers the Matrix row on a
            // throwaway; this row records whether a backup preceded it.
            s.Recorder.Row($"{p}-permanent-delete-backup", "Backup", "-", null, null, new { backupSucceeded = backup is not null });
            await HierarchyProbes.DeleteRowAsync(s, t, $"{p}-permanent-delete", Q, recycled.Id!, vaultName, permanently: true, pageDate: false);
        });

        await s.StepAsync($"{p}-open-backup", async () =>
        {
            if (backup is null || summary is null)
            {
                s.Recorder.Row($"{p}-open-backup", "Backup", "OpenHierarchy(backup)", null, null, new { notProbed = "no backup was written" });
                return;
            }

            await OpenProtectedBackupAsync(s, p, backup, summary, vaultName);
        });

        // Leave nothing of this pass on screen before the next one (review of the run-1 fixes).
        await s.StepAsync($"{p}-end", () => s.EnsureNothingShownAsync(runId, "next-pass"));
    }

    /// <summary>The page Phill clicks to take a probe area off screen: in the other test notebook.</summary>
    private static string Safe(ProbeTarget t) => ProbeSession.SafePage(t.Cloud);

    /// <summary>
    /// Opens the protected backup. If it opens locked, Phill unlocks it so the pages can be compared (Q4), then closes it.
    /// </summary>
    private static async Task OpenProtectedBackupAsync(ProbeSession s, string p, string path, BackupSummary before, string vaultName)
    {
        var (attempt, openedId, opened) = await s.OpenBackupAsync(path);
        BackupSummary? locked = null;
        BackupSummary? unlocked = null;
        if (attempt.Ok && openedId is not null)
        {
            locked = await SummaryAsync(s, openedId);
            if (locked.Locked is "true" or "1")
            {
                await s.PauseAsync(
                    $"{p}-unlock-backup",
                    $"A backup copy of \"{vaultName}\" just opened in OneNote (it may show outside any notebook, under Open Sections or Misc). Unlock that copy with the same password. Then continue.");
                unlocked = await SummaryAsync(s, openedId);
            }
        }

        var after = unlocked ?? locked;
        s.Recorder.Row($"{p}-open-backup", "Backup", "OpenHierarchy(backup)", new { opened = opened is null ? null : Path.GetFileName(opened) }, attempt, new
        {
            before,
            asOpened = locked,
            afterUnlock = unlocked,
            samePageNames = after is not null && before.PageNames.SequenceEqual(after.PageNames),
            encryptedKept = locked?.Encrypted is "true" or "1",
        });

        if (attempt.Ok && openedId is not null)
        {
            await s.PauseAsync(
                $"{p}-close-backup",
                $"In OneNote, close the backup copy of \"{vaultName}\" that just opened: right-click its tab > Close This Section. Don't delete it, and don't close anything else. Then continue.");
        }
    }

    private static async Task<BackupSummary> SummaryAsync(ProbeSession s, string id) =>
        HierarchyProbes.Summary(System.Xml.Linq.XDocument.Parse(await s.Gateway.GetHierarchyAsync(id, OneNoteMcp.Core.HierarchyScope.Pages, ProbeSession.Ct)).Root!);

    /// <summary>
    /// Every node named <paramref name="name"/> in the notebook, recycle bin included, with its section's
    /// <c>encrypted</c> and <c>locked</c> (for a page, its parent section's).
    /// </summary>
    private static async Task<object> RecycleViewAsync(ProbeSession s, ProbeTarget t, string name)
    {
        var tree = await s.TreeAsync(t.NotebookId);
        return tree.Descendants()
            .Where(e => (string?)e.Attribute("name") == name)
            .Select(e =>
            {
                var section = e.Name.LocalName == "Section" ? e : e.Ancestors(e.Name.Namespace + "Section").FirstOrDefault();
                return new
                {
                    node = NodeInfo.From(e),
                    sectionName = (string?)section?.Attribute("name"),
                    sectionEncrypted = (string?)section?.Attribute("encrypted"),
                    sectionLocked = (string?)section?.Attribute("locked"),
                    sectionInRecycleBin = section is not null && NodeInfo.From(section).InRecycleBin,
                };
            })
            .ToList();
    }
}
