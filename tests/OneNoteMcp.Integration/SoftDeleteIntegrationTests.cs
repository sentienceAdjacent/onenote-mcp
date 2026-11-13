using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;
using CorePageInfo = OneNoteMcp.Core.PageInfo;
using CoreScope = OneNoteMcp.Core.HierarchyScope;
using InteropNewPageStyle = OneNoteMcp.Com.Interop.NewPageStyle;
using InteropSchema = OneNoteMcp.Com.Interop.XMLSchema;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 5.5: <c>delete_hierarchy_object</c> against a real OneNote, the plan's live probes L1-L12. Opt-in with
/// <c>ONENOTE_MCP_INTEGRATION=1</c>, never run in a build; grants and the journal go to a temporary folder, never the
/// user's.
/// <list type="bullet">
/// <item>Every target is a throwaway this run makes in MCP Test (or MCP Test Cloud, with Sync Now after each step), under a
/// stamped <c>Soft delete 5.5</c> name. Before each delete the guard checks it is one this run made, inside a test
/// notebook, and not the notebook itself (<see cref="TestNotebookGuard.CheckDelete"/>,
/// <see cref="TestNotebook.EnsureTestTargetAsync"/>). The user's Quick Notes section is never touched.</item>
/// <item>The probes that need a password-protected section (L4, L7, the protected half of L10) use throwaways Phill makes by
/// hand, found by their exact names (<see cref="HandVaultName"/>, <see cref="HandLockedGroupName"/>) and skipped when
/// absent. They hold only throwaway text.</item>
/// <item>"gateway" probes call <c>DeleteHierarchy</c> directly through the guarded seam, because the tool refuses that case.</item>
/// </list>
/// Output lines carry IDs, codes and the throwaway names only; lines starting <c>PHILL:</c> ask for a hand step.
/// </summary>
public sealed class SoftDeleteIntegrationTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>An unlocked password-protected section Phill makes by hand in MCP Test (and in MCP Test Cloud for L4's cloud row), holding nothing real.</summary>
    public const string HandVaultName = "Soft delete 5.5 Vault";

    /// <summary>A section group Phill makes by hand in MCP Test, holding one password-protected section, locked before L7 runs.</summary>
    public const string HandLockedGroupName = "Soft delete 5.5 Locked group";

    /// <summary>A page Phill makes by hand in MCP Test Cloud for L3, shown in OneNote with one letter typed, so its two dates differ.</summary>
    public const string HandDisplayedPageName = "Soft delete 5.5 L3";

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    /// <summary>What this run made, or hand-made throwaways it verified by name; only these may be deleted.</summary>
    private readonly HashSet<string> created = new(StringComparer.Ordinal);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Stamp => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a temporary settings file and journal.
        }
    }

    // --- L1, L2: pages ---------------------------------------------------------------------------------------------------

    /// <summary>L1 (verify): a throwaway page soft-deleted through the tool lands in Deleted Pages, the entry's recycleBinId matches the read-back, and restore_from_journal brings it back as a new page.</summary>
    [Fact]
    public async Task L1_Page_IsInDeletedPages_ItsEntryHoldsTheRecycleBinId_AndRestoreMakesANewPage()
    {
        await using var run = await StartAsync(cloud: false);
        var section = await NewSectionAsync(run, run.NotebookId, $"Soft delete 5.5 L1 {Stamp}");
        var title = $"Soft delete 5.5 L1 page {Stamp}";
        var page = await NewPageAsync(run, section, title);
        var basis = await PageBasisAsync(run, page);
        var before = await DeletedPagesAsync(run);

        var root = Ok(await DeleteAsync(run, page, new DeleteBasisArgument(basis.LastModifiedTime, basis.PageHash)));

        var meta = root.GetProperty("meta");
        var recycleBinId = meta.TryGetProperty("recycleBinId", out var id) ? id.GetString() : null;
        var after = await DeletedPagesAsync(run);
        var added = after.Where(p => !before.Any(b => b.Id == p.Id)).ToList();
        output.WriteLine($"RESULT L1: recycleBinId {recycleBinId ?? "(none)"}, new in Deleted Pages: [{string.Join(", ", added.Select(p => p.Id))}], warnings {root.GetProperty("warnings").GetRawText()}");
        Assert.NotNull(recycleBinId);
        Assert.Equal(recycleBinId, Assert.Single(added).Id);

        var entry = JournalStore.Read(Assert.Single(Directory.GetFiles(JournalFolder)));
        Assert.Equal(recycleBinId, entry.RecycleBinId);

        // What the recycled page keeps (name, dateTime, pageLevel, dates).
        var recycled = XDocument.Parse(await run.Gateway.GetHierarchyAsync(recycleBinId!, CoreScope.Self, Ct)).Root!;
        output.WriteLine(
            $"RESULT L1 recycled page keeps: name equal={(string?)recycled.Attribute("name") == title}, dateTime={(string?)recycled.Attribute("dateTime")}, "
            + $"pageLevel={(string?)recycled.Attribute("pageLevel")}, lastModifiedTime={(string?)recycled.Attribute("lastModifiedTime")}, isInRecycleBin={(string?)recycled.Attribute("isInRecycleBin")}");

        var restored = Ok(await new RestoreFromJournalTool(Runner(), RestoreUseCase(run)).RestoreFromJournal(entry.EntryId.ToString("D"), Ct));
        var newPage = restored.GetProperty("meta").GetProperty("pageId").GetString()!;
        created.Add(newPage);
        output.WriteLine($"RESULT L1 restore: new page {newPage} in {restored.GetProperty("meta").GetProperty("parentId").GetString()}");
        Assert.Equal(section, restored.GetProperty("meta").GetProperty("parentId").GetString());
    }

    /// <summary>L2 (verify): a page that isn't displayed, edited after the basis was read, is StaleWrite from the pageHash (its page-XML date doesn't move, spike 2.2 constraint 4), and nothing is deleted.</summary>
    [Fact]
    public async Task L2_HiddenPage_EditedAfterTheBasis_IsStaleWrite_AndStaysLive()
    {
        await using var run = await StartAsync(cloud: false);
        var section = await NewSectionAsync(run, run.NotebookId, $"Soft delete 5.5 L2 {Stamp}");
        var page = await NewPageAsync(run, section, $"Soft delete 5.5 L2 page {Stamp}");
        var basis = await PageBasisAsync(run, page);

        await SetTitleAsync(run, page, $"Soft delete 5.5 L2 edited {Stamp}");
        var dateAfterEdit = (await PageBasisAsync(run, page)).LastModifiedTime;
        output.WriteLine($"RESULT L2: page-XML date moved after the edit: {dateAfterEdit != basis.LastModifiedTime}");

        var result = await DeleteAsync(run, page, new DeleteBasisArgument(basis.LastModifiedTime, basis.PageHash));

        output.WriteLine($"RESULT L2: {Code(result)}");
        Assert.Equal("StaleWrite", Code(result));
        Assert.False(await InRecycleBinAsync(run, page));
    }

    // --- L3, L4: dates and protected pages ------------------------------------------------------------------------------

    /// <summary>
    /// L3 (Deferred row): a displayed page on MCP Test Cloud whose two dates differ, deleted through the tool, which sends the
    /// hierarchy date: deleted, not <c>0x80042010</c>. Phill makes the page by hand (<see cref="HandDisplayedPageName"/>), shows
    /// it and types one letter first.
    /// </summary>
    [Fact]
    public async Task L3_DisplayedCloudPage_WhoseDatesDiffer_IsDeleted_WithTheHierarchyDate()
    {
        await using var run = await StartAsync(cloud: true);
        var page = await HandMadeAsync(run, run.NotebookId, HandDisplayedPageName, "Page");
        Assert.SkipWhen(page is null, $"PHILL: in MCP Test Cloud, make a page titled \"{HandDisplayedPageName}\", show it in OneNote and type one letter, then run L3 again.");
        var hierarchyDate = (string?)XDocument.Parse(await run.Gateway.GetHierarchyAsync(page!, CoreScope.Self, Ct)).Root!.Attribute("lastModifiedTime");
        var basis = await PageBasisAsync(run, page!);
        output.WriteLine($"RESULT L3 dates: page XML {basis.LastModifiedTime}, hierarchy {hierarchyDate}, differ={basis.LastModifiedTime != hierarchyDate}");

        var result = await DeleteAsync(run, page!, new DeleteBasisArgument(basis.LastModifiedTime, basis.PageHash));
        await SyncAsync(run);

        output.WriteLine($"RESULT L3: {(result.IsError == true ? Code(result) + " " + Hex(result) : "deleted")}");
        Assert.False(result.IsError, "L3: the hierarchy date should be accepted");
    }

    /// <summary>
    /// L4 (Deferred row, gateway): a page in an unlocked protected section, soft-deleted with its hierarchy date, local and
    /// cloud: where does it land (Deleted Pages, and is that <c>encrypted</c>), is it refused, or is it deleted outright? The
    /// tool refuses it first. Nothing is read beyond the hierarchy. If it lands in the plain Deleted Pages, COM can't remove it
    /// from there (spike 5.1 constraint 9), so the output asks Phill to empty the recycle bin.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task L4_PageInAnUnlockedProtectedSection_WhereItLands(bool cloud)
    {
        await using var run = await StartAsync(cloud);
        var vault = await HandMadeAsync(run, run.NotebookId, HandVaultName, "Section");
        Assert.SkipWhen(vault is null, $"PHILL: in {(cloud ? TestNotebook.CloudNotebookName : TestNotebook.NotebookName)}, make a password-protected section named \"{HandVaultName}\" holding nothing real, unlock it, then run L4 again.");
        var page = await NewPageAsync(run, vault!, title: null);

        var refused = await DeleteAsync(run, page, null);
        output.WriteLine($"RESULT L4-{run.Label} tool: {Code(refused)}");
        Assert.Equal("NotSupported", Code(refused));

        var before = await DeletedPagesAsync(run);
        var outcome = await RawDeleteAsync(run, page, HierarchyDate(await run.Gateway.GetHierarchyAsync(page, CoreScope.Self, Ct)));
        await SyncAsync(run);
        var after = await DeletedPagesAsync(run);
        var added = after.Where(p => !before.Any(b => b.Id == p.Id)).Select(p => p.Id).ToList();
        var binXml = await BinXmlAsync(run);
        var landedIn = binXml is null ? null : XDocument.Parse(binXml).Descendants(One + "Page").FirstOrDefault(p => added.Contains((string?)p.Attribute("ID") ?? string.Empty))?.Parent;
        output.WriteLine(
            $"RESULT L4-{run.Label}: {outcome}; old ID resolves={await ResolvesAsync(run, page)}; new in Deleted Pages: [{string.Join(", ", added)}]; "
            + $"landed in section {(string?)landedIn?.Attribute("ID") ?? "(none)"} encrypted={(string?)landedIn?.Attribute("encrypted") ?? "absent"}");
        if (added.Count > 0 && (string?)landedIn?.Attribute("encrypted") is not ("true" or "1"))
        {
            output.WriteLine($"PHILL: the protected page landed in the plain Deleted Pages of {run.Label}; empty that notebook's recycle bin in OneNote before the sitting ends.");
        }
    }

    // --- L5, L6, L7: section groups ---------------------------------------------------------------------------------------

    /// <summary>L5 (Deferred row): an empty section group through the tool: what is left in the hierarchy and the recycle bin.</summary>
    [Fact]
    public async Task L5_EmptyGroup_WhatIsLeft()
    {
        await using var run = await StartAsync(cloud: false);
        var group = await NewGroupAsync(run, run.NotebookId, $"Soft delete 5.5 L5 {Stamp}");

        var result = await DeleteAsync(run, group, new DeleteBasisArgument(await NodeDateAsync(run, group)));

        output.WriteLine($"RESULT L5: {(result.IsError == true ? Code(result) + " " + Hex(result) : "ok")}; old ID resolves={await ResolvesAsync(run, group)}; in the bin={Contains(await BinXmlAsync(run), group)}");
    }

    /// <summary>L6 (Deferred row): a nested group (a section, plus a subgroup holding a section): does the subgroup dissolve too, and do both sections land in the recycle bin?</summary>
    [Fact]
    public async Task L6_NestedGroup_BothSectionsLandInTheBin()
    {
        await using var run = await StartAsync(cloud: false);
        var stamp = Stamp;
        var group = await NewGroupAsync(run, run.NotebookId, $"Soft delete 5.5 L6 {stamp}");
        var outer = await NewSectionAsync(run, group, "Outer");
        var subgroup = await NewGroupAsync(run, group, "Sub");
        var inner = await NewSectionAsync(run, subgroup, "Inner");

        var result = await DeleteAsync(run, group, new DeleteBasisArgument(await NodeDateAsync(run, group)));

        var bin = await BinXmlAsync(run);
        output.WriteLine(
            $"RESULT L6: {(result.IsError == true ? Code(result) + " " + Hex(result) : "ok")}; group resolves={await ResolvesAsync(run, group)}, subgroup resolves={await ResolvesAsync(run, subgroup)}; "
            + $"outer in bin={Contains(bin, outer)}, inner in bin={Contains(bin, inner)}, subgroup in bin={Contains(bin, subgroup)}");
    }

    /// <summary>
    /// L7 (gateway): a group holding a locked protected section (<see cref="HandLockedGroupName"/>, made and locked by Phill):
    /// the tool refuses it with SectionLocked; then a raw <c>DeleteHierarchy</c> records what OneNote does: <c>0x8004201B</c>,
    /// a partial delete, or all of it.
    /// </summary>
    [Fact]
    public async Task L7_GroupHoldingALockedSection_ToolRefuses_ThenWhatOneNoteDoes()
    {
        await using var run = await StartAsync(cloud: false);
        var group = await HandMadeAsync(run, run.NotebookId, HandLockedGroupName, "SectionGroup");
        Assert.SkipWhen(group is null, $"PHILL: in MCP Test, make a section group \"{HandLockedGroupName}\" holding one password-protected section with nothing real in it, lock it, then run L7 again.");

        var refused = await DeleteAsync(run, group!, null);
        output.WriteLine($"RESULT L7 tool: {Code(refused)}");
        Assert.Equal("SectionLocked", Code(refused));

        var sections = XDocument.Parse(await run.Gateway.GetHierarchyAsync(group!, CoreScope.Pages, Ct)).Descendants(One + "Section").Select(s => (string?)s.Attribute("ID") ?? string.Empty).ToList();
        var outcome = await RawDeleteAsync(run, group!, HierarchyDate(await run.Gateway.GetHierarchyAsync(group!, CoreScope.Self, Ct)));
        var bin = await BinXmlAsync(run);
        output.WriteLine($"RESULT L7 gateway: {outcome}; group resolves={await ResolvesAsync(run, group!)}; sections before={sections.Count}; in the bin: [{string.Join(", ", sections.Select(s => Contains(bin, s)))}]");
    }

    // --- L8, L9: subpages and same-named pages ----------------------------------------------------------------------------

    /// <summary>L8 (gateway): a page with one subpage: the tool refuses it; a raw delete of the parent records what OneNote does to the subpage (OneMore: left behind).</summary>
    [Fact]
    public async Task L8_PageWithASubpage_ToolRefuses_ThenWhatOneNoteDoesToTheSubpage()
    {
        await using var run = await StartAsync(cloud: false);
        var section = await NewSectionAsync(run, run.NotebookId, $"Soft delete 5.5 L8 {Stamp}");
        var parent = await NewPageAsync(run, section, "Parent");
        var child = await NewPageAsync(run, section, "Child");
        await TestNotebook.UpdateHierarchyAsync(
            run.Gateway,
            $"<one:Section xmlns:one=\"{OneNoteXml.Namespace2013}\" ID=\"{section}\"><one:Page ID=\"{parent}\" /><one:Page ID=\"{child}\" pageLevel=\"2\" /></one:Section>",
            created,
            new Dictionary<string, IReadOnlySet<string>>(),
            "UpdateHierarchy(L8 subpage)",
            Ct);

        var refused = await DeleteAsync(run, parent, null);
        output.WriteLine($"RESULT L8 tool: {Code(refused)} {Message(refused)}");
        Assert.Equal("InvalidArgument", Code(refused));

        var outcome = await RawDeleteAsync(run, parent, HierarchyDate(await run.Gateway.GetHierarchyAsync(parent, CoreScope.Self, Ct)));
        var childNow = await SelfOrNullAsync(run, child);
        output.WriteLine($"RESULT L8 gateway: {outcome}; subpage resolves={childNow is not null}, isInRecycleBin={(string?)childNow?.Attribute("isInRecycleBin") ?? "absent"}, pageLevel={(string?)childNow?.Attribute("pageLevel") ?? "absent"}");
    }

    /// <summary>L9: two same-named pages already in Deleted Pages, then a third same-named delete through the tool: the right recycleBinId.</summary>
    [Fact]
    public async Task L9_ThirdSameNamedDelete_GetsTheRightRecycleBinId()
    {
        await using var run = await StartAsync(cloud: false);
        var stamp = Stamp;
        var section = await NewSectionAsync(run, run.NotebookId, $"Soft delete 5.5 L9 {stamp}");
        var name = $"Soft delete 5.5 L9 same {stamp}";
        foreach (var n in new[] { 1, 2 })
        {
            var earlier = await NewPageAsync(run, section, name);
            output.WriteLine($"L9 earlier page {n}: {await RawDeleteAsync(run, earlier, HierarchyDate(await run.Gateway.GetHierarchyAsync(earlier, CoreScope.Self, Ct)))}");
        }

        var page = await NewPageAsync(run, section, name);
        var before = await DeletedPagesAsync(run);
        var root = Ok(await DeleteAsync(run, page, null));
        var added = (await DeletedPagesAsync(run)).Where(p => !before.Any(b => b.Id == p.Id)).Select(p => p.Id).ToList();
        var recycleBinId = root.GetProperty("meta").TryGetProperty("recycleBinId", out var id) ? id.GetString() : null;

        output.WriteLine($"RESULT L9: recycleBinId {recycleBinId ?? "(none)"}, new: [{string.Join(", ", added)}], same-named in Deleted Pages: {(await DeletedPagesAsync(run)).Count(p => p.Name == name)}");
        Assert.Equal(Assert.Single(added), recycleBinId);
        output.WriteLine("L9 optional (a first page delete in a notebook with no recycle bin yet): run it by hand in 5.3's throwaway notebook if it is still open.");
    }

    // --- L10, L11: sections --------------------------------------------------------------------------------------------

    /// <summary>L10: a section, and an unlocked protected one, through the tool: in OneNote_RecycleBin with the same ID, a hierarchy entry (DPAPI for the protected one), and restore_from_journal's Q4 NotSupported.</summary>
    [Fact]
    public async Task L10_Section_AndAProtectedSection_KeepTheirIds_InTheBin_AndRestoreSaysWhere()
    {
        await using var run = await StartAsync(cloud: false);
        var section = await NewSectionAsync(run, run.NotebookId, $"Soft delete 5.5 L10 {Stamp}");
        await NewPageAsync(run, section, title: null);
        await AssertSectionSoftDeleteAsync(run, section, "plain");

        var vault = await HandMadeAsync(run, run.NotebookId, HandVaultName, "Section");
        if (vault is null)
        {
            output.WriteLine($"PHILL: for L10's protected half, make an unlocked password-protected section \"{HandVaultName}\" in MCP Test, then run L10 again.");
            return;
        }

        await AssertSectionSoftDeleteAsync(run, vault, "protected");
    }

    /// <summary>L11: a section's date read, a page added to it, then a delete with the old basis: StaleWrite, nothing deleted.</summary>
    [Fact]
    public async Task L11_SectionChangedAfterTheBasis_IsStaleWrite()
    {
        await using var run = await StartAsync(cloud: false);
        var section = await NewSectionAsync(run, run.NotebookId, $"Soft delete 5.5 L11 {Stamp}");
        var date = await NodeDateAsync(run, section);
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct); // the hierarchy date has 1 s resolution (spike 5.1 constraint 2)
        await NewPageAsync(run, section, title: null);
        output.WriteLine($"RESULT L11 date moved: {await NodeDateAsync(run, section) != date}");

        var result = await DeleteAsync(run, section, new DeleteBasisArgument(date));

        output.WriteLine($"RESULT L11: {Code(result)}");
        Assert.Equal("StaleWrite", Code(result));
        Assert.False(await InRecycleBinAsync(run, section));
    }

    // --- L12: cloud ------------------------------------------------------------------------------------------------------

    /// <summary>L12 (cloud): page, section and group soft deletes on MCP Test Cloud, with Sync Now after each; Phill checks each reached OneDrive in OneNote for the web.</summary>
    [Fact]
    public async Task L12_Cloud_PageSectionAndGroup_SoftDeletes()
    {
        await using var run = await StartAsync(cloud: true);
        var stamp = Stamp;
        var section = await NewSectionAsync(run, run.NotebookId, $"Soft delete 5.5 L12 {stamp}");
        var page = await NewPageAsync(run, section, $"Soft delete 5.5 L12 page {stamp}");
        var group = await NewGroupAsync(run, run.NotebookId, $"Soft delete 5.5 L12 group {stamp}");
        await NewSectionAsync(run, group, "Inside");
        await SyncAsync(run);

        foreach (var id in new[] { page, section, group })
        {
            var basis = id == page ? null : new DeleteBasisArgument(await NodeDateAsync(run, id));
            var result = await DeleteAsync(run, id, basis);
            await SyncAsync(run);
            output.WriteLine($"RESULT L12 {id}: {(result.IsError == true ? Code(result) + " " + Hex(result) : "ok")}");
        }

        output.WriteLine($"PHILL: in OneNote for the web, check that \"Soft delete 5.5 L12 page {stamp}\", the section and the group named with {stamp} are in MCP Test Cloud's recycle bin.");
    }

    // --- Shared probe steps --------------------------------------------------------------------------------------------

    private async Task AssertSectionSoftDeleteAsync(Run run, string section, string label)
    {
        var result = await DeleteAsync(run, section, new DeleteBasisArgument(await NodeDateAsync(run, section)));
        var self = await SelfOrNullAsync(run, section);
        output.WriteLine(
            $"RESULT L10 {label}: {(result.IsError == true ? Code(result) + " " + Hex(result) : "ok")}; same ID resolves={self is not null}, isInRecycleBin={(string?)self?.Attribute("isInRecycleBin") ?? "absent"}, "
            + $"encrypted={(string?)self?.Attribute("encrypted") ?? "absent"}, locked={(string?)self?.Attribute("locked") ?? "absent"}");
        var root = Ok(result);
        var entryId = root.GetProperty("meta").GetProperty("journalEntryId").GetString()!;
        var entry = JournalStore.Read(JournalFiles.Find(JournalFolder, Guid.Parse(entryId))!);
        Assert.Equal(JournalEntry.HierarchyKind, entry.SnapshotKind);
        Assert.Equal(label == "protected", entry.SectionKey is not null);
        Assert.Equal("true", (string?)self?.Attribute("isInRecycleBin"));

        var restore = await new RestoreFromJournalTool(Runner(), RestoreUseCase(run)).RestoreFromJournal(entryId, Ct);
        output.WriteLine($"RESULT L10 {label} restore: {Code(restore)}");
        Assert.Equal("NotSupported", Code(restore));
    }

    // --- Setup ---------------------------------------------------------------------------------------------------------

    private sealed record Run(ComGateway Gateway, string NotebookId, string SettingsPath, string Label, bool Cloud) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Gateway.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private string JournalFolder => Path.Combine(tempDir, "journal");

    /// <summary>A real gateway over a temporary <c>settings.json</c> that grants only the test notebook used, keyed by its live path.</summary>
    private async Task<Run> StartAsync(bool cloud)
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");
        File.WriteAllText(settingsPath, """{ "schemaVersion": 1 }""");
        var store = new SettingsStore(settingsPath, JournalFolder);
        var gateway = new ComGateway(new ProcessProbe(), store);
        try
        {
            string notebookId;
            if (cloud)
            {
                var cloudId = await TestNotebook.FindCloudAsync(gateway, Ct);
                Assert.SkipWhen(cloudId is null, $"{TestNotebook.CloudNotebookName} isn't open.");
                notebookId = cloudId!;
            }
            else
            {
                notebookId = (await TestNotebook.EnsureAsync(gateway, Ct)).NotebookId;
            }

            var self = HierarchyNode.Root(await gateway.GetHierarchyAsync(notebookId, CoreScope.Self, Ct));
            new SettingsWriter(store, TimeProvider.System).SaveWriteGrant(new NotebookGrant(
                GrantKeys.NotebookKey(self.Path), self.DisplayName, notebookId, Write: true, PermanentDelete: false, DateTimeOffset.UtcNow, GrantedVia.Cli));
            return new Run(gateway, notebookId, settingsPath, cloud ? "cloud" : "local", cloud);
        }
        catch
        {
            gateway.Dispose();
            throw;
        }
    }

    private SettingsStore Store(Run run) => new(run.SettingsPath, JournalFolder);

    private SoftDeleteUseCase UseCase(Run run)
    {
        var store = Store(run);
        var services = new PageWriteServices(
            run.Gateway,
            new WriteAccess(run.Gateway, store.Read),
            new ReadAccess(run.Gateway, store.Read, new SettingsWriter(store, TimeProvider.System), TimeProvider.System),
            new PageSnapshot(run.Gateway, new JournalStore(store.Read), TimeProvider.System));
        return new SoftDeleteUseCase(services, new HierarchySnapshot(run.Gateway, new JournalStore(store.Read), TimeProvider.System), Pipeline(run));
    }

    private RestoreFromJournalUseCase RestoreUseCase(Run run)
    {
        var store = Store(run);
        return new RestoreFromJournalUseCase(run.Gateway, new WriteAccess(run.Gateway, store.Read), Pipeline(run), store.Read);
    }

    private CreateHierarchyUseCase CreateUseCase(Run run)
    {
        var store = Store(run);
        return new CreateHierarchyUseCase(run.Gateway, new WriteAccess(run.Gateway, store.Read), Pipeline(run), store.Read, new SettingsWriter(store, TimeProvider.System), TimeProvider.System);
    }

    private WritePipeline Pipeline(Run run) => new(new WriteLock(), run.Gateway, PageTextCache.WithDefaults(), Store(run).Read);

    private static ToolRunner Runner() => new(NullLogger<ToolRunner>.Instance);

    // --- Throwaways ----------------------------------------------------------------------------------------------------

    private async Task<string> NewSectionAsync(Run run, string parentId, string name)
    {
        var id = ObjectId(Ok(await new CreateSectionTool(Runner(), CreateUseCase(run)).CreateSection(parentId, name, Ct)));
        created.Add(id);
        output.WriteLine($"section {id} ({name}) under {parentId}");
        return id;
    }

    private async Task<string> NewGroupAsync(Run run, string parentId, string name)
    {
        var id = ObjectId(Ok(await new CreateSectionGroupTool(Runner(), CreateUseCase(run)).CreateSectionGroup(parentId, name, Ct)));
        created.Add(id);
        output.WriteLine($"section group {id} ({name}) under {parentId}");
        return id;
    }

    /// <summary>A new page in <paramref name="sectionId"/>, titled <paramref name="title"/> when given (a guarded <c>UpdatePageContent</c> of its title).</summary>
    private async Task<string> NewPageAsync(Run run, string sectionId, string? title)
    {
        var id = await TestNotebook.WriteAsync(
            run.Gateway,
            sectionId,
            app =>
            {
                app.CreateNewPage(sectionId, out var pageId, InteropNewPageStyle.npsBlankPageWithTitle);
                return pageId;
            },
            "CreateNewPage(soft delete throwaway)",
            Ct);
        created.Add(id);
        if (title is not null)
        {
            await SetTitleAsync(run, id, title);
        }

        output.WriteLine($"page {id} in {sectionId}");
        return id;
    }

    private async Task SetTitleAsync(Run run, string pageId, string title)
    {
        var date = (await PageBasisAsync(run, pageId)).LastModifiedTime ?? throw new InvalidOperationException("The page has no lastModifiedTime.");
        var xml = new XElement(
            One + "Page",
            new XAttribute(XNamespace.Xmlns + "one", OneNoteXml.Namespace2013),
            new XAttribute("ID", pageId),
            new XElement(One + "Title", new XElement(One + "OE", new XElement(One + "T", new XCData(title))))).ToString(SaveOptions.DisableFormatting);
        var utc = OneNoteDates.ToUtc(date) ?? throw new InvalidOperationException("The page's lastModifiedTime isn't a date.");
        await TestNotebook.WriteAsync(
            run.Gateway,
            pageId,
            app =>
            {
                app.UpdatePageContent(xml, utc, InteropSchema.xs2013, force: false);
                return true;
            },
            "UpdatePageContent(throwaway title)",
            Ct);
    }

    /// <summary>
    /// A hand-made throwaway directly under <paramref name="notebookId"/> with exactly <paramref name="name"/> and element
    /// <paramref name="kind"/>, live (not in the recycle bin); added to <see cref="created"/> so the guard lets it be deleted.
    /// Null when there is none.
    /// </summary>
    private async Task<string?> HandMadeAsync(Run run, string notebookId, string name, string kind)
    {
        var tree = XDocument.Parse(await run.Gateway.GetHierarchyAsync(notebookId, CoreScope.Pages, Ct)).Root!;
        var match = tree.Descendants(One + kind)
            .Where(e => (string?)e.Attribute("name") == name && (string?)e.Attribute("isInRecycleBin") != "true")
            .Select(e => (string?)e.Attribute("ID"))
            .FirstOrDefault();
        if (match is not null)
        {
            created.Add(match);
        }

        return match;
    }

    // --- Deletes -------------------------------------------------------------------------------------------------------

    /// <summary>The tool's delete of a target the guard accepts: one this run made (or verified by name), inside a test notebook, not the notebook.</summary>
    private async Task<CallToolResult> DeleteAsync(Run run, string id, DeleteBasisArgument? basis)
    {
        await GuardAsync(run, id, DeleteHierarchyObjectTool.Name);
        return await new DeleteHierarchyObjectTool(Runner(), UseCase(run)).DeleteHierarchyObject(id, basis, Ct);
    }

    /// <summary>A raw <c>DeleteHierarchy</c> (permanent false) through the guarded seam; returns "ok" or the error's code and HRESULT.</summary>
    private async Task<string> RawDeleteAsync(Run run, string id, DateTime date)
    {
        try
        {
            await TestNotebook.DeleteHierarchyAsync(run.Gateway, id, date, permanently: false, created, new Dictionary<string, IReadOnlySet<string>>(), Ct);
            return "ok";
        }
        catch (OneNoteException ex)
        {
            return $"{ex.Code} {Hex(ex.ComHResult)}";
        }
    }

    private async Task GuardAsync(Run run, string id, string method)
    {
        var notebooks = await run.Gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct);
        TestNotebookGuard.CheckDelete(method, id, TestNotebookGuard.TestNotebookIds(notebooks, TestNotebook.ResolveFolder(Environment.GetEnvironmentVariable)), created);
        await TestNotebook.EnsureTestTargetAsync(run.Gateway, id, method, Ct);
    }

    private async Task SyncAsync(Run run)
    {
        if (!run.Cloud)
        {
            return;
        }

        try
        {
            await run.Gateway.InvokeAsync(
                app =>
                {
                    app.SyncHierarchy(run.NotebookId);
                    return true;
                },
                Ct,
                retryOnDisconnect: false,
                method: "SyncHierarchy(MCP Test Cloud)");
            output.WriteLine("Sync Now: SyncHierarchy returned; watch for the cloud sync jam (0xE0000644)");
        }
        catch (OneNoteException ex)
        {
            output.WriteLine($"Sync Now failed: {ex.Code} {Hex(ex.ComHResult)}");
        }
    }

    // --- Reads ---------------------------------------------------------------------------------------------------------

    private static async Task<PageBasis> PageBasisAsync(Run run, string pageId) =>
        PageBasis.From(await run.Gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct));

    private static async Task<string> NodeDateAsync(Run run, string id) =>
        (string?)XDocument.Parse(await run.Gateway.GetHierarchyAsync(id, CoreScope.Self, Ct)).Root!.Attribute("lastModifiedTime")
            ?? throw new InvalidOperationException($"{id} has no lastModifiedTime.");

    private static DateTime HierarchyDate(string selfXml) =>
        OneNoteDates.ToUtc((string?)XDocument.Parse(selfXml).Root!.Attribute("lastModifiedTime"))
            ?? throw new InvalidOperationException("No hierarchy lastModifiedTime.");

    private static async Task<string?> BinXmlAsync(Run run)
    {
        var children = await run.Gateway.GetHierarchyAsync(run.NotebookId, CoreScope.Children, Ct);
        return RecycleBin.BinId(children) is { Length: > 0 } bin ? await run.Gateway.GetHierarchyAsync(bin, CoreScope.Pages, Ct) : null;
    }

    private static async Task<IReadOnlyList<DeletedPage>> DeletedPagesAsync(Run run) =>
        await BinXmlAsync(run) is { } bin ? RecycleBin.DeletedPages(bin) : [];

    private static async Task<XElement?> SelfOrNullAsync(Run run, string id)
    {
        try
        {
            return XDocument.Parse(await run.Gateway.GetHierarchyAsync(id, CoreScope.Self, Ct)).Root;
        }
        catch (OneNoteException)
        {
            return null;
        }
    }

    private static async Task<bool> ResolvesAsync(Run run, string id) => await SelfOrNullAsync(run, id) is not null;

    private static async Task<bool> InRecycleBinAsync(Run run, string id) =>
        (string?)(await SelfOrNullAsync(run, id))?.Attribute("isInRecycleBin") == "true";

    private static bool Contains(string? bin, string id) => bin is not null && TestNotebookGuard.AnyContains(bin, id);

    // --- Results -------------------------------------------------------------------------------------------------------

    private static JsonElement Ok(CallToolResult result)
    {
        var root = result.StructuredContent!.Value;
        Assert.False(result.IsError, $"the call failed: {(result.IsError == true ? root.GetProperty("error").GetProperty("code").GetString() : null)}");
        return root;
    }

    private static string Code(CallToolResult result) =>
        result.IsError == true ? result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString()! : "ok";

    private static string Message(CallToolResult result) =>
        result.IsError == true ? result.StructuredContent!.Value.GetProperty("error").GetProperty("message").GetString()! : string.Empty;

    private static string Hex(CallToolResult result) =>
        result.IsError == true && result.StructuredContent!.Value.GetProperty("error").TryGetProperty("hresult", out var hr) ? hr.ToString() : "none";

    private static string ObjectId(JsonElement root) => root.GetProperty("meta").GetProperty("objectId").GetString()!;

    private static string Hex(int? hresult) =>
        hresult is { } hr ? "0x" + hr.ToString("X8", CultureInfo.InvariantCulture) : "none";
}
