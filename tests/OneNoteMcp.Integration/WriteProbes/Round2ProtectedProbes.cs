using OneNoteMcp.Com.Interop;
using Xunit;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Spike 3.1, scenario <c>write-probes-r2-protected-local</c> (sitting A): XP1–2, O5, SP, MP and RP on MCP Test's run
/// group and a new throwaway notebook, <c>MCP Test Throwaway &lt;stamp&gt; p</c>, which holds the packaged protected
/// section (a test notebook is never packaged whole: it holds Ballast Vault).
/// </summary>
public sealed class Round2ProtectedLocalProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "write-probes-r2-protected-local";

    [Fact]
    public async Task WriteProbesR2ProtectedLocal()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.ProbeCallSeconds);
        var t = new ProbeTarget("local", await s.SetupAsync("setup", s.McpTestIdAsync), TestNotebook.NotebookName, Cloud: false);
        var stamp = HierarchyProbes.Stamp();
        var run = await s.SetupAsync("run-group", () => HierarchyProbes.RunGroupAsync(s, t, stamp));
        var c = new Round2Context(s, t, run, stamp);
        var name = $"{TestNotebook.ThrowawayNamePrefix}{stamp} p";
        var (attempt, id) = await s.SetupAsync("throwaway", () => s.CreateThrowawayAsync(name, requireEmpty: true));
        s.Recorder.Row("throwaway-create", "Setup", "OpenHierarchy(cftNotebook)", new { name = "MCP Test Throwaway <stamp> p" }, attempt, new { id });
        c.Other = id is null ? null : new ProbeTarget("protected-throwaway", id, name, Cloud: false);

        await Round2ProtectedProbes.RunAsync(c, openPackages: true);
        s.Finish();
    }
}

/// <summary>
/// Spike 3.1, scenario <c>write-probes-r2-protected-cloud</c> (sitting B): XP1–2, SP, MP and RP on MCP Test Cloud's run
/// group and <c>MCP Test Throwaway Cloud</c> (Q2 a), with a Sync Now check after each step.
/// </summary>
public sealed class Round2ProtectedCloudProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "write-probes-r2-protected-cloud";

    [Fact]
    public async Task WriteProbesR2ProtectedCloud()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.CloudProbeCallSeconds);
        var t = new ProbeTarget("cloud", await s.SetupAsync("setup", s.CloudIdAsync), TestNotebook.CloudNotebookName, Cloud: true);
        var c = await Round2.StartAsync(s, t);

        await Round2ProtectedProbes.RunAsync(c, openPackages: false);
        s.Finish();
    }
}

/// <summary>
/// The protected steps. Phill protects eight sections at one pause; then, unlocked: XP1, the unlocked SP and MP cases;
/// then he locks everything (Ctrl+Alt+L): XP2, the locked SP and MP cases; then O5 (local); then RP. The lock state
/// decides this order, so it interleaves the plan's export, merge and move order within each phase. No protected
/// section's <c>.one</c> backup is ever opened (5.1 constraint 17); packages are byte-scanned after <c>expand.exe</c>,
/// and opened in OneNote only for O5.
/// </summary>
internal static class Round2ProtectedProbes
{
    private const string Q = "Protected";

    /// <summary>A section a probe needs, found again by name after protecting changes its ID (5.1 constraint 15).</summary>
    private sealed class Spot(ProbeTarget nb, string name, string[] titles)
    {
        public ProbeTarget Nb { get; } = nb;

        public string Name { get; } = name;

        public string[] Titles { get; } = titles;

        public string? Id { get; set; }

        public Dictionary<string, string> Pages { get; } = new(StringComparer.Ordinal);

        public bool Encrypted { get; set; }
    }

    public static async Task RunAsync(Round2Context c, bool openPackages)
    {
        var s = c.S;
        var t = c.T;
        var label = t.Label;
        var st = c.Stamp;
        var where = $"{t.NotebookName} > probe run {st}";
        Spot Vault(string key, ProbeTarget nb, int pages) => new(nb, $"probe vault {key} {st}", Enumerable.Range(1, pages).Select(i => $"probe vault {key} p{i} {st}").ToArray());
        Spot Plain(string key, int pages) => new(t, $"probe plain {key} {st}", Enumerable.Range(1, pages).Select(i => $"probe plain {key} p{i} {st}").ToArray());

        var pkg = c.Other is null ? null : Vault("pkg", c.Other, 2);
        var vaults = new Dictionary<string, Spot>(StringComparer.Ordinal)
        {
            ["sa"] = Vault("sa", t, 2), // SP: unlocked source
            ["sb"] = Vault("sb", t, 1), // SP: unlocked, then locked, destination
            ["sc"] = Vault("sc", t, 2), // SP: locked source
            ["md"] = Vault("md", t, 3), // MP: pages out of and into it
            ["me"] = Vault("me", t, 1), // MP: moved unlocked
            ["mf"] = Vault("mf", t, 1), // MP: moved locked
            ["rg"] = Vault("rg", t, 2), // RP
        };
        var plains = new Dictionary<string, Spot>(StringComparer.Ordinal)
        {
            ["sa"] = Plain("sa", 1),
            ["sb"] = Plain("sb", 2),
            ["sc"] = Plain("sc", 1),
            ["sd"] = Plain("sd", 1),
            ["md"] = Plain("md", 2),
        };
        string? group = null;
        var ready = false;

        await s.StepAsync($"{label}-protected-setup", SyncChecked(c, $"{label}-protected-setup", async () =>
        {
            foreach (var spot in vaults.Values.Concat(plains.Values).Append(pkg).OfType<Spot>())
            {
                var parent = spot.Nb.NotebookId == t.NotebookId ? c.Run : spot.Nb.NotebookId;
                var (id, pageIds) = await Round2.SectionAsync(c, parent, spot.Name, spot.Titles);
                spot.Id = id;
                for (var i = 0; i < spot.Titles.Length; i++)
                {
                    spot.Pages[spot.Titles[i]] = pageIds[i];
                }
            }

            group = await s.MustCreateChildAsync(c.Run, $"probe mp grp {st}", CreateFileType.cftFolder);
        }));
        if (group is null || vaults.Values.Any(v => v.Id is null))
        {
            s.Recorder.Row($"{label}-protected-stopped", Q, "-", null, null, new { notProbed = $"step {label}-protected-setup didn't run or failed; every protected step needs it" });
            return;
        }

        await s.StepAsync($"{label}-protect", SyncChecked(c, $"{label}-protect", async () =>
        {
            var inRun = string.Join(", ", vaults.Values.Select(v => $"\"{v.Name}\""));
            var inPkg = pkg is null ? string.Empty : $" And in the notebook \"{pkg.Nb.NotebookName}\": \"{pkg.Name}\".";
            await s.PauseAsync(
                $"{label}-protect",
                $"In OneNote, password-protect each of these sections with the same password, one you'll remember for this sitting (right-click its tab > Password Protect This Section). "
                + $"In {where}: {inRun}.{inPkg} Leave them all unlocked (unlock any that lock). Don't protect the \"probe plain …\" sections, and don't touch Ballast Vault or any other section. "
                + $"Then click the page {ProbeSession.SafePage(t.Cloud)} so none of them is on screen. Then continue.");
            foreach (var spot in vaults.Values.Append(pkg).OfType<Spot>())
            {
                await ResolveAsync(s, spot);
            }

            ready = vaults.Values.All(v => v.Encrypted);
            s.Recorder.Row($"{label}-protected", Q, "GetHierarchy", null, null, new
            {
                ready,
                sections = vaults.Values.Append(pkg).OfType<Spot>().Select(v => new { v.Name, v.Encrypted, pages = v.Pages.Count }).ToList(),
            });
        }));
        if (!ready)
        {
            s.Recorder.Row($"{label}-protected-stopped", Q, "-", null, null, new { notProbed = "not every probe vault section is encrypted (see the protected row), so no protected probe ran" });
            return;
        }

        string? xp1 = null;
        string? xp2 = null;
        await s.StepAsync($"{label}-unlocked", SyncChecked(c, $"{label}-unlocked", async () =>
        {
            xp1 = await PackageAsync(c, $"xp1-{label}", pkg, "unlocked");
            await MergeAsync(c, $"sp-{label}-unlocked-into-plain", vaults["sa"], plains["sa"]);
            await MergeAsync(c, $"sp-{label}-plain-into-unlocked", plains["sb"], vaults["sb"]);
            await PageMoveAsync(c, $"mp-{label}-page-out-unlocked", vaults["md"], vaults["md"].Titles[0], plains["md"]);
            await PageMoveAsync(c, $"mp-{label}-page-in-unlocked", plains["md"], plains["md"].Titles[0], vaults["md"]);
            await SectionMovesAsync(c, $"mp-{label}-section-unlocked", vaults["me"], group);
        }));

        await s.StepAsync($"{label}-locked", SyncChecked(c, $"{label}-locked", async () =>
        {
            await s.PauseAsync(
                $"{label}-lock",
                "In OneNote, press Ctrl+Alt+L to lock all password-protected sections. Don't open or unlock any of them. Then continue.");
            var state = new List<object>();
            foreach (var spot in vaults.Values.Append(pkg).OfType<Spot>())
            {
                state.Add(new { spot.Name, node = spot.Id is null ? null : await s.NodeAsync(spot.Nb.NotebookId, spot.Id) });
            }

            s.Recorder.Row($"{label}-locked-state", Q, "GetHierarchy", null, null, new { sections = state });
            xp2 = await PackageAsync(c, $"xp2-{label}", pkg, "locked");
            await MergeAsync(c, $"sp-{label}-locked-source", vaults["sc"], plains["sc"]);
            await MergeAsync(c, $"sp-{label}-locked-destination", plains["sd"], vaults["sb"]);
            if (Environment.GetEnvironmentVariable("ONENOTE_MCP_PROBE_LOCKED_MOVES") == "1")
            {
                await PageMoveAsync(c, $"mp-{label}-page-out-locked", vaults["md"], vaults["md"].Titles[1], plains["md"]);
                await PageMoveAsync(c, $"mp-{label}-page-in-locked", plains["md"], plains["md"].Titles[1], vaults["md"]);
                await SectionMovesAsync(c, $"mp-{label}-section-locked", vaults["mf"], group);
            }
            else
            {
                // Sitting A (2026-11-16): moving a page out of a locked section (UpdateHierarchy, shape c) ended ONENOTE.EXE.
                // The other locked moves aren't sent either: the server refuses writes to locked sections anyway.
                s.Recorder.Row($"mp-{label}-locked", Q, "UpdateHierarchy", null, null, new { notProbed = "moving a page out of a locked section crashed OneNote (sitting A); locked moves not re-sent" });
            }
        }));

        if (openPackages)
        {
            await s.StepAsync($"{label}-open-packages", async () =>
            {
                await OpenPackageAsync(c, $"o5-{label}-xp1", xp1, "XP1's (unlocked when packaged)");
                await OpenPackageAsync(c, $"o5-{label}-xp2", xp2, "XP2's (locked when packaged)");
            });
        }

        await s.StepAsync($"{label}-restore", SyncChecked(c, $"{label}-restore", () => RestoreAsync(c, vaults["rg"])));
        await s.StepAsync($"{label}-close-opened", () => Round2.CloseOpenedAsync(c));
        await s.StepAsync($"{label}-end", () => s.EnsureNothingShownAsync(c.Run, "next-pass"));
    }

    private static Func<Task> SyncChecked(Round2Context c, string step, Func<Task> body) => HierarchyProbes.SyncChecked(c.S, c.T, step, c.Stamp, body);

    /// <summary>Finds a section again (by ID, else by name) and its pages by title; records whether it is encrypted.</summary>
    private static async Task ResolveAsync(ProbeSession s, Spot spot)
    {
        var node = (spot.Id is null ? null : await s.NodeAsync(spot.Nb.NotebookId, spot.Id))
            ?? (await s.FindByNameAsync(spot.Nb.NotebookId, spot.Name)).FirstOrDefault(n => n.Kind == "Section" && !n.InRecycleBin && n.Id is not null);
        if (node?.Id is null)
        {
            spot.Encrypted = false;
            return;
        }

        spot.Id = node.Id;
        spot.Encrypted = node.Encrypted is "true" or "1";
        foreach (var title in spot.Titles)
        {
            if (await s.PageIdByTitleAsync(spot.Id, title) is { } pageId)
            {
                s.Created.Add(pageId); // a probe-named page of this run, under its new ID
                spot.Pages[title] = pageId;
            }
        }
    }

    /// <summary>
    /// XP1/XP2: the notebook holding <paramref name="pkg"/> published as a package, then extracted with
    /// <c>expand.exe</c> and every extracted file byte-scanned for the section's page titles, its body text and
    /// <c>&lt;encryption</c> headers. Returns the package path when it landed.
    /// </summary>
    private static async Task<string?> PackageAsync(Round2Context c, string probe, Spot? pkg, string state)
    {
        var s = c.S;
        if (pkg?.Id is null || c.Other is null)
        {
            s.Recorder.Row(probe, "Export", "Publish(pfOneNotePackage)", null, null, new { notProbed = "no throwaway notebook holds a protected section" });
            return null;
        }

        var section = await s.NodeAsync(pkg.Nb.NotebookId, pkg.Id);
        var landing = await s.PublishFreshAsync($"{probe}-{c.Stamp}", c.Other.NotebookId, PublishFormat.pfOneNotePackage, $"probe-{probe}-{c.Stamp}.onepkg");
        ExtractResult? extracted = null;
        var scans = new List<object>();
        if (landing.Landed is { } file)
        {
            var into = ProbeSession.FreshFolder(ProbeSession.PublishDir, $"{probe}-{c.Stamp}-expanded");
            extracted = await ProbeSession.ExtractPackageAsync(file, into);
            foreach (var entry in extracted.Files.Where(f => f.Bytes > 0))
            {
                scans.Add(new { entry.Path, scan = ProtectedPublishProbes.Scan(Path.Combine(into, entry.Path), pkg.Titles) });
            }
        }

        s.Recorder.Row(probe, "Export", "Publish(pfOneNotePackage)", new { scope = "notebook", sectionState = state }, landing.Attempt, new
        {
            section = section is null ? null : new { section.Encrypted, section.Locked },
            publish = landing.Summary,
            extracted,
            scans,
        });
        return landing.Landed;
    }

    /// <summary>SP: one <c>MergeSections</c> between a protected and a plain section, with the usual merge read-back.</summary>
    private static Task MergeAsync(Round2Context c, string probe, Spot source, Spot destination) =>
        Round2MergeProbes.MergeExistingAsync(c, probe, $"{source.Name} into {destination.Name}", source.Nb, source.Id!, source.Name, source.Titles, destination.Nb, destination.Id!);

    /// <summary>MP: a page moved by shape "c" (the destination's XML, then the source's without it if needed); the page ID is the one read before any lock.</summary>
    private static async Task PageMoveAsync(Round2Context c, string probe, Spot from, string title, Spot to)
    {
        if (!from.Pages.TryGetValue(title, out var pageId))
        {
            c.S.Recorder.Row(probe, Q, "UpdateHierarchy", null, null, new { notProbed = $"no page ID for \"{title}\"" });
            return;
        }

        await Round2MoveProbes.MovePageAsync(c, probe, "c", from.Nb, from.Id!, to.Nb, to.Id!, pageId, title);
    }

    /// <summary>MP: a protected section moved to another group of the notebook, then to the other notebook's root.</summary>
    private static async Task SectionMovesAsync(Round2Context c, string probe, Spot vault, string group)
    {
        await Round2MoveProbes.MoveNodeAsync(c, $"{probe}-to-group", "Section", c.T, vault.Id!, c.Run, c.T, "SectionGroup", group);
        if (c.Other is null)
        {
            c.S.Recorder.Row($"{probe}-to-notebook", Q, "UpdateHierarchy", null, null, new { notProbed = "no other notebook is open" });
            return;
        }

        // Moving may give it a new ID (as protecting does): find it again by name first. Sitting B (2026-11-16): sent
        // straight after the move into the group, the cloud move to the other notebook raised OneNote's modal "can't
        // copy or move this section because it has been deleted by a user of this notebook" and blocked 180 s, so
        // let the first move settle before reading it back.
        await Task.Delay(TimeSpan.FromSeconds(10));
        await ResolveAsync(c.S, vault);
        await Round2MoveProbes.MoveNodeAsync(c, $"{probe}-to-notebook", "Section", c.T, vault.Id!, group, c.Other, "Notebook", c.Other.NotebookId);
    }

    /// <summary>
    /// O5: a package opened (<see cref="Round2ExportProbes.OpenOneAsync"/>), then its sections' <c>encrypted</c> and
    /// <c>locked</c> read; Phill says whether OneNote asked for the password.
    /// </summary>
    private static async Task OpenPackageAsync(Round2Context c, string probe, string? package, string which)
    {
        var s = c.S;
        if (package is null)
        {
            s.Recorder.Row(probe, "OpenPackage", "OpenPackage", null, null, new { notProbed = $"{which} package didn't land" });
            return;
        }

        var (_, notebookId) = await Round2ExportProbes.OpenOneAsync(c, $"{probe}-open", package, ProbeSession.FreshFolder(ProbeSession.PublishDir, $"{probe}-{c.Stamp}"), $"a fresh folder ({which} package)");
        var sections = notebookId is null
            ? null
            : (await s.TreeAsync(notebookId, OneNoteMcp.Core.HierarchyScope.Sections)).Descendants(One + "Section").Select(NodeInfo.From).Select(n => new { n.Name, n.Encrypted, n.Locked, n.InRecycleBin }).ToList();
        var answered = await s.PauseAsync(
            $"{probe}-prompt",
            $"A copy of {which} package just opened in OneNote (a new notebook whose name ends in \"-unpacked\"). Tell the team lead: did OneNote ask for a password when it opened, "
            + "and does the \"probe vault pkg …\" section in that copy show as locked? Don't unlock it. Then continue.");
        s.Recorder.Row(probe, "OpenPackage", "GetHierarchy", new { which }, null, new { sections, phillAnswered = answered, answer = "relayed by the team lead into the findings doc" });
    }

    /// <summary>
    /// RP: the section, unlocked, soft-deleted whole (its hierarchy date); locked in the recycle bin; restored by its run
    /// group's XML while locked; if that fails, Phill unlocks the recycled copy and the restore is tried again.
    /// </summary>
    private static async Task RestoreAsync(Round2Context c, Spot vault)
    {
        var s = c.S;
        var label = c.T.Label;
        await s.PauseAsync(
            $"{label}-rp-unlock",
            $"In OneNote, unlock the section \"{vault.Name}\" in {c.T.NotebookName} > probe run {c.Stamp} with its password. Then click the page {ProbeSession.SafePage(c.T.Cloud)} so it is no longer on screen. Then continue.");
        await ResolveAsync(s, vault);
        var before = await s.NodeAsync(c.T.NotebookId, vault.Id!);
        var deleted = await Round2.SoftDeleteAsync(c, c.T.NotebookId, vault.Id!);
        var recycled = await s.NodeAsync(c.T.NotebookId, vault.Id!);
        s.Recorder.Row($"rp-{label}-soft-delete", Q, "DeleteHierarchy(soft)", new { date = "hierarchy", sectionState = "unlocked" }, deleted, new { before, after = recycled });
        if (!deleted.Ok || recycled is not { InRecycleBin: true })
        {
            s.Recorder.Row($"rp-{label}", Q, "UpdateHierarchy", null, null, new { notProbed = "the section didn't reach the recycle bin" });
            return;
        }

        if (recycled.Locked is not ("true" or "1"))
        {
            await s.PauseAsync($"{label}-rp-lock", "In OneNote, press Ctrl+Alt+L to lock all password-protected sections (this includes the deleted one in the notebook's recycle bin). Then continue.");
            recycled = await s.NodeAsync(c.T.NotebookId, vault.Id!);
        }

        var locked = await RestoreRowAsync(c, $"rp-{label}-locked", vault, recycled);
        if (locked)
        {
            s.Recorder.Row($"rp-{label}-unlocked", Q, "UpdateHierarchy", null, null, new { notProbed = "the restore while locked already worked" });
            return;
        }

        await s.PauseAsync(
            $"{label}-rp-unlock-recycled",
            $"In OneNote, open {c.T.NotebookName}'s recycle bin (History > Notebook Recycle Bin), open the deleted section \"{vault.Name}\" and unlock it with its password. "
            + $"Don't restore it. Then click the page {ProbeSession.SafePage(c.T.Cloud)} so it is no longer on screen. Then continue.");
        await RestoreRowAsync(c, $"rp-{label}-unlocked", vault, await s.NodeAsync(c.T.NotebookId, vault.Id!));
    }

    /// <summary>One restore try: the run group's XML listing the recycled section. Returns true when it left the recycle bin.</summary>
    private static async Task<bool> RestoreRowAsync(Round2Context c, string probe, Spot vault, NodeInfo? recycled)
    {
        var s = c.S;
        var attempt = await s.UpdateHierarchyAsync(Hierarchy("SectionGroup", c.Run, string.Empty, Node("Section", vault.Id!)));
        var sameId = await s.NodeAsync(c.T.NotebookId, vault.Id!);
        var byName = await s.FindByNameAsync(c.T.NotebookId, vault.Name);
        s.Recorder.Row(probe, Q, "UpdateHierarchy", new { shape = "the run group's XML listing the recycled protected section" }, attempt, new
        {
            recycled,
            sameIdAfter = sameId,
            byName,
        });
        return byName.Any(n => n.Kind == "Section" && !n.InRecycleBin);
    }
}
