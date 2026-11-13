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
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 5.4's live probes L1–L9 for <c>rename_hierarchy_object</c> and <c>reorder_hierarchy_children</c> against a real
/// OneNote, opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c> (<c>--filter UpdateHierarchy</c>). They were written in the build
/// and never run there; Phill runs them at the sitting.
/// <list type="bullet">
/// <item>Every write goes through the tools, over a gateway whose <c>UpdateHierarchy</c> is story 5.1's guarded one
/// (<see cref="TestNotebook.UpdateHierarchyAsync"/>): every ID it sends must be an object this run made, inside MCP Test,
/// MCP Test Cloud (L7) or this run's throwaway notebook (L8). Sections, groups and pages are made under stamped names and
/// soft-deleted at the end; a failed clean-up is logged and never fails the test.</item>
/// <item>Grants and the recovery journal live in a temporary folder, never the user's settings or journal.</item>
/// <item>L5 needs two sections Phill prepares in MCP Test, <c>Probe 5.4 Protected</c> (password-protected, unlocked) and
/// <c>Probe 5.4 Locked</c> (protected, locked); it is skipped without them, and renames each back afterwards. L9 needs
/// File &gt; Options open in OneNote and <c>ONENOTE_MCP_PROBE_MODAL=1</c>.</item>
/// <item>Probes that answer a question (L3, L4, L5, L9) print <c>RESULT</c> lines and assert only what is already
/// decided; L1, L2, L6 and L8 verify.</item>
/// </list>
/// The output carries IDs, codes and the probes' own stamped names only.
/// </summary>
public sealed class UpdateHierarchyIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private const string ProtectedProbeName = "Probe 5.4 Protected";
    private const string LockedProbeName = "Probe 5.4 Locked";

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    /// <summary>What this run made, in order; only these may be written to or deleted.</summary>
    private readonly List<string> created = [];

    /// <summary>The throwaway notebooks this run made (L8), by ID, with their names: the guard accepts writes there.</summary>
    private readonly Dictionary<string, IReadOnlySet<string>> throwaways = new(StringComparer.Ordinal);

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

    /// <summary>L1 (verify): a rename lands under the same ID, and the journal holds the hierarchy entry with the old name.</summary>
    [Fact]
    public async Task L1_RenameASection_TheNameLands_TheIdIsKept_AndTheJournalHoldsTheOldName()
    {
        await using var run = await StartAsync();
        var stamp = Stamp;
        try
        {
            var section = await CreateSectionAsync(run, run.NotebookId, $"Probe 5.4 L1 {stamp}");
            var root = Ok(await RenameTool(run).RenameHierarchyObject(section, $"Probe 5.4 L1 renamed {stamp}", await BasisAsync(run, section, CoreScope.Self), Ct));

            Assert.Equal(section, ObjectId(root));
            var after = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(section, CoreScope.Self, Ct));
            Assert.Equal($"Probe 5.4 L1 renamed {stamp}", after.Name);
            Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());

            var entryId = Guid.Parse(root.GetProperty("meta").GetProperty("journalEntryId").GetString()!);
            var entry = JournalStore.Read(JournalFiles.Find(run.JournalFolder, entryId)!);
            Assert.Equal(JournalEntry.HierarchyKind, entry.SnapshotKind);
            Assert.Equal($"Probe 5.4 L1 {stamp}", entry.Title);
            output.WriteLine($"RESULT L1: renamed {section}, journal entry {entryId:D}");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>L2 (verify): a page added after the basis was read moves the section's date, so the rename is StaleWrite.</summary>
    [Fact]
    public async Task L2_ASectionChangedSinceItsBasis_IsStaleWrite_AndNothingIsRenamed()
    {
        await using var run = await StartAsync();
        var stamp = Stamp;
        try
        {
            var section = await CreateSectionAsync(run, run.NotebookId, $"Probe 5.4 L2 {stamp}");
            var basis = await BasisAsync(run, section, CoreScope.Self);
            await Task.Delay(TimeSpan.FromSeconds(2), Ct); // the date has 1 s resolution and can lag our own write (spike 5.1 constraint 2)
            await CreatePageAsync(run, section);

            var result = await RenameTool(run).RenameHierarchyObject(section, $"Probe 5.4 L2 renamed {stamp}", basis, Ct);

            Assert.Equal("StaleWrite", ErrorCode(result));
            Assert.Equal($"Probe 5.4 L2 {stamp}", HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(section, CoreScope.Self, Ct)).Name);
            output.WriteLine($"RESULT L2: StaleWrite for {section}");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>L3: section groups reordered among themselves, then a full reorder (sections, then groups): applied in the order sent?</summary>
    [Fact]
    public async Task L3_ReorderGroupsAmongThemselves_AndAFullReorder_RecordsWhatOneNoteKept()
    {
        await using var run = await StartAsync();
        var stamp = Stamp;
        try
        {
            var group = await CreateGroupAsync(run, run.NotebookId, $"Probe 5.4 L3 {stamp}");
            var s1 = await CreateSectionAsync(run, group, "s1");
            var s2 = await CreateSectionAsync(run, group, "s2");
            var g1 = await CreateGroupAsync(run, group, "g1");
            var g2 = await CreateGroupAsync(run, group, "g2");

            await ReorderAndRecordAsync(run, "L3 groups", group, [s1, s2, g2, g1]);
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            await ReorderAndRecordAsync(run, "L3 full", group, [s2, s1, g1, g2]);
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>L4: a case-only rename (<c>probe x</c> → <c>Probe X</c>): applied, <c>0x80042017</c>, or ignored?</summary>
    [Fact]
    public async Task L4_ACaseOnlyRename_RecordsWhatOneNoteDid()
    {
        await using var run = await StartAsync();
        var stamp = Stamp;
        try
        {
            var section = await CreateSectionAsync(run, run.NotebookId, $"probe 5.4 l4 {stamp}");
            var result = await RenameTool(run).RenameHierarchyObject(section, $"Probe 5.4 L4 {stamp}", await BasisAsync(run, section, CoreScope.Self), Ct);
            var after = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(section, CoreScope.Self, Ct)).Name;
            output.WriteLine($"RESULT L4: {Outcome(result)}; reads back as {(after == $"Probe 5.4 L4 {stamp}" ? "the new case" : after == $"probe 5.4 l4 {stamp}" ? "the old case" : "something else")}");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>
    /// L5: renaming an unlocked protected section and a locked one (Phill prepares both). Renamed, <c>0x8004201B</c>, or
    /// something else? <c>encrypted</c> kept? The tool refuses the locked one before COM (SectionLocked) until this says
    /// otherwise, so the locked one is also sent a raw guarded rename (HierarchyUpdate.Rename's XML) to learn what OneNote
    /// does. Each is renamed back in a finally whenever its read-back shows the name changed.
    /// </summary>
    [Fact]
    public async Task L5_RenameProtectedSections_RecordsWhatOneNoteDid()
    {
        await using var run = await StartAsync();
        var sections = XDocument.Parse(await run.Gateway.GetHierarchyAsync(run.NotebookId, CoreScope.Children, Ct)).Root!
            .Elements(One + "Section").ToList();
        var unlocked = sections.FirstOrDefault(s => (string?)s.Attribute("name") == ProtectedProbeName && (string?)s.Attribute("encrypted") == "true");
        var locked = sections.FirstOrDefault(s => (string?)s.Attribute("name") == LockedProbeName && (string?)s.Attribute("encrypted") == "true");
        Assert.SkipWhen(unlocked is null || locked is null, $"Prepare two password-protected sections in MCP Test, \"{ProtectedProbeName}\" (unlocked) and \"{LockedProbeName}\" (locked).");

        foreach (var (section, name) in new[] { (unlocked!, ProtectedProbeName), (locked!, LockedProbeName) })
        {
            var id = (string)section.Attribute("ID")!;
            var which = section == unlocked ? "unlocked" : "locked";
            created.Add(id); // prepared by Phill for this probe; never deleted (only renamed back)
            try
            {
                // The tool: renames the unlocked one, and verifies our pre-COM SectionLocked for the locked one.
                var result = await RenameTool(run).RenameHierarchyObject(id, name + " renamed", await BasisAsync(run, id, CoreScope.Self), Ct);
                var after = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(id, CoreScope.Self, Ct));
                output.WriteLine($"RESULT L5 {which} (tool): {Outcome(result)}; encrypted {after.Encrypted}; renamed {after.Name != name}");

                if (section == locked)
                {
                    // What OneNote itself does with a locked section's rename, past our refusal.
                    string raw;
                    try
                    {
                        await TestNotebook.UpdateHierarchyAsync(run.Gateway, HierarchyUpdate.Rename(HierarchyKind.Section, id, name + " raw"), created.ToHashSet(StringComparer.Ordinal), throwaways, "UpdateHierarchy(L5 locked rename)", Ct);
                        raw = "no error";
                    }
                    catch (OneNoteException ex)
                    {
                        raw = $"error {ex.Code} {Hex(ex.ComHResult)}";
                    }

                    var rawAfter = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(id, CoreScope.Self, Ct));
                    output.WriteLine($"RESULT L5 locked (raw UpdateHierarchy): {raw}; encrypted {rawAfter.Encrypted}; locked {rawAfter.Locked}; renamed {rawAfter.Name != name}");
                }
            }
            finally
            {
                await RestoreNameAsync(run, id, name);
            }
        }

        output.WriteLine("PHILL: list_grants should now show any read grant for those sections as sectionNotOpen.");
    }

    /// <summary>
    /// Renames Phill's prepared section <paramref name="id"/> back to <paramref name="name"/> when a read-back shows it
    /// changed, through the guarded raw rename (it works whether or not the section is locked). A failure is logged.
    /// </summary>
    private async Task RestoreNameAsync(Run run, string id, string name)
    {
        try
        {
            if (HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(id, CoreScope.Self, Ct)).Name == name)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            await TestNotebook.UpdateHierarchyAsync(run.Gateway, HierarchyUpdate.Rename(HierarchyKind.Section, id, name), created.ToHashSet(StringComparer.Ordinal), throwaways, "UpdateHierarchy(L5 rename back)", Ct);
            var back = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(id, CoreScope.Self, Ct)).Name == name;
            output.WriteLine(back ? $"renamed {id} back" : $"PHILL: rename section {id} back to \"{name}\" in OneNote");
        }
#pragma warning disable CA1031 // A failed rename-back is logged and never hides the probe's own outcome.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            output.WriteLine($"PHILL: rename section {id} back to \"{name}\" in OneNote (rename-back failed: {ex.GetType().Name} {(ex as OneNoteException)?.Code})");
        }
    }

    private static string Hex(int? hresult) =>
        hresult is { } hr ? "0x" + hr.ToString("X8", CultureInfo.InvariantCulture) : "none";

    /// <summary>L6 (verify): pages reordered with a subpage block moved whole keep their levels and parents.</summary>
    [Fact]
    public async Task L6_ReorderPages_WithASubpageBlockMovedWhole_KeepsLevelsAndParents()
    {
        await using var run = await StartAsync();
        var stamp = Stamp;
        try
        {
            var section = await CreateSectionAsync(run, run.NotebookId, $"Probe 5.4 L6 {stamp}");
            var p1 = await CreatePageAsync(run, section);
            var p2 = await CreatePageAsync(run, section);
            var p3 = await CreatePageAsync(run, section);

            // p2 becomes p1's subpage through the guarded raw UpdateHierarchy (the tool never changes pageLevel).
            var level = $"<one:Section xmlns:one=\"{OneNoteXml.Namespace2013}\" ID=\"{section}\"><one:Page ID=\"{p1}\" /><one:Page ID=\"{p2}\" pageLevel=\"2\" /><one:Page ID=\"{p3}\" /></one:Section>";
            await TestNotebook.UpdateHierarchyAsync(run.Gateway, level, created.ToHashSet(StringComparer.Ordinal), throwaways, "UpdateHierarchy(L6 subpage level)", Ct);
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);

            var root = Ok(await ReorderTool(run).ReorderHierarchyChildren(section, [p3, p1, p2], await BasisAsync(run, section, CoreScope.Children), Ct));

            var after = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(section, CoreScope.Children, Ct));
            Assert.Equal([p3, p1, p2], after.Children.Select(c => c.Id));
            Assert.Equal([1, 1, 2], after.Children.Select(c => c.PageLevel));
            Assert.DoesNotContain(root.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == "readBackDiffers");
            output.WriteLine("RESULT L6: order and levels kept");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>
    /// L7 (cloud): section, page and section-group reorders on MCP Test Cloud, with Sync Now after each. Phill checks each
    /// reached OneDrive in OneNote for the web. Skipped when MCP Test Cloud isn't open.
    /// </summary>
    [Fact]
    public async Task L7_CloudReorders_AreSyncedForPhillToCheckOnTheWeb()
    {
        await using var run = await StartAsync(cloud: true);
        Assert.SkipWhen(run.CloudId is null, "MCP Test Cloud isn't open in OneNote.");
        var stamp = Stamp;
        try
        {
            var group = await CreateGroupAsync(run, run.CloudId!, $"Probe 5.4 L7 {stamp}");
            await SyncAsync(run, run.CloudId!);
            var s1 = await CreateSectionAsync(run, group, "s1");
            var s2 = await CreateSectionAsync(run, group, "s2");
            var g1 = await CreateGroupAsync(run, group, "g1");
            var g2 = await CreateGroupAsync(run, group, "g2");
            var p1 = await CreatePageAsync(run, s1);
            var p2 = await CreatePageAsync(run, s1);
            await SyncAsync(run, run.CloudId!);

            await ReorderAndRecordAsync(run, "L7 sections", group, [s2, s1, g1, g2]);
            await SyncAsync(run, run.CloudId!);
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            await ReorderAndRecordAsync(run, "L7 pages", s1, [p2, p1]);
            await SyncAsync(run, run.CloudId!);
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            await ReorderAndRecordAsync(run, "L7 groups", group, [s2, s1, g2, g1]);
            await SyncAsync(run, run.CloudId!);
            output.WriteLine($"PHILL: in OneNote for the web, check MCP Test Cloud > Probe 5.4 L7 {stamp}: sections s2, s1, groups g2, g1, and in s1 the second page first. Then the probe's clean-up soft-deletes it.");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>L8 (verify): a throwaway notebook's rename sets its nickname; its folder and its write grant are unchanged.</summary>
    [Fact]
    public async Task L8_RenameAThrowawayNotebook_SetsTheNickname_TheFolderAndGrantStay()
    {
        await using var run = await StartAsync();
        var name = TestNotebook.ThrowawayNamePrefix + "Rename " + Stamp;
        var notebookId = await TestNotebook.CreateThrowawayNotebookAsync(run.Gateway, name, requireEmpty: true, Ct);
        throwaways[notebookId] = new HashSet<string>([name, name + " renamed"]);
        created.Add(notebookId);
        var before = HierarchyNode.Root(await run.Gateway.GetHierarchyAsync(notebookId, CoreScope.Self, Ct));
        output.WriteLine($"PHILL: close the notebook \"{name}\" (shown as \"{name} renamed\") in OneNote, then delete its folder {before.Path}");
        new SettingsWriter(run.Store, TimeProvider.System).SaveWriteGrant(new NotebookGrant(
            GrantKeys.NotebookKey(before.Path), name, notebookId, Write: true, PermanentDelete: false, DateTimeOffset.UtcNow, GrantedVia.Cli));

        Ok(await RenameTool(run).RenameHierarchyObject(notebookId, name + " renamed", await BasisAsync(run, notebookId, CoreScope.Self), Ct));

        var after = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(notebookId, CoreScope.Self, Ct));
        Assert.Equal(name + " renamed", after.Nickname);
        Assert.Equal(before.Path, after.Path);
        var listed = await new ListGrantsUseCase(run.Gateway, run.Store.Read, TimeProvider.System).ExecuteAsync(new ToolCallContext("it"), Ct);
        Assert.Equal("active", Assert.Single(listed.Meta.Grants!, g => g.Key == GrantKeys.NotebookKey(before.Path)).Status);
    }

    /// <summary>L9 (optional): a rename with File &gt; Options open in OneNote returns ModalDialogOpen through the tool.</summary>
    [Fact]
    public async Task L9_ARenameWithAModalDialogOpen_IsModalDialogOpen()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONENOTE_MCP_PROBE_MODAL") == "1", "Open File > Options in OneNote and set ONENOTE_MCP_PROBE_MODAL=1 to run L9.");
        await using var run = await StartAsync();
        var stamp = Stamp;
        try
        {
            string section;
            try
            {
                section = await CreateSectionAsync(run, run.NotebookId, $"Probe 5.4 L9 {stamp}");
            }
            catch (Xunit.Sdk.XunitException)
            {
                output.WriteLine("RESULT L9: the dialog already blocks the create, so the rename can't be probed this way; close it, rerun with it closed until the section exists, then open it.");
                return;
            }

            var result = await RenameTool(run).RenameHierarchyObject(section, $"Probe 5.4 L9 renamed {stamp}", await BasisAsync(run, section, CoreScope.Self), Ct);
            output.WriteLine($"RESULT L9: {Outcome(result)}");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    // --- Setup ------------------------------------------------------------------------------------------------------

    private sealed record Run(ComGateway Gateway, string NotebookId, string? CloudId, SettingsStore Store, string JournalFolder) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Gateway.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A real gateway over a temporary <c>settings.json</c> and journal that grant MCP Test (and MCP Test Cloud with <paramref name="cloud"/>).</summary>
    private async Task<Run> StartAsync(bool cloud = false)
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var journal = Path.Combine(tempDir, "journal");
        File.WriteAllText(settingsPath, """{ "schemaVersion": 1 }""");
        var store = new SettingsStore(settingsPath, journal);
        var gateway = new ComGateway(new ProcessProbe(), store);
        try
        {
            var notebook = await TestNotebook.EnsureAsync(gateway, Ct);
            var self = HierarchyNode.Root(await gateway.GetHierarchyAsync(notebook.NotebookId, CoreScope.Self, Ct));
            Assert.Equal(TestNotebook.NotebookName, self.Name);
            var writer = new SettingsWriter(store, TimeProvider.System);
            writer.SaveWriteGrant(new NotebookGrant(GrantKeys.NotebookKey(self.Path), TestNotebook.NotebookName, notebook.NotebookId, Write: true, PermanentDelete: false, DateTimeOffset.UtcNow, GrantedVia.Cli));

            string? cloudId = null;
            if (cloud && await TestNotebook.FindCloudAsync(gateway, Ct) is { } found)
            {
                var cloudSelf = HierarchyNode.Root(await gateway.GetHierarchyAsync(found, CoreScope.Self, Ct));
                Assert.True(TestNotebookGuard.IsCloudTestPath(cloudSelf.Path));
                writer.SaveWriteGrant(new NotebookGrant(GrantKeys.NotebookKey(cloudSelf.Path), TestNotebook.CloudNotebookName, found, Write: true, PermanentDelete: false, DateTimeOffset.UtcNow, GrantedVia.Cli));
                cloudId = found;
            }

            return new Run(gateway, notebook.NotebookId, cloudId, store, journal);
        }
        catch
        {
            gateway.Dispose();
            throw;
        }
    }

    /// <summary>The tools' use case over <see cref="GuardedGateway"/>, so every <c>UpdateHierarchy</c> passes the test-notebook guard.</summary>
    private UpdateHierarchyUseCase UseCase(Run run)
    {
        var gateway = new GuardedGateway(run.Gateway, created, throwaways);
        var services = new HierarchyWriteServices(
            gateway,
            new WriteAccess(gateway, run.Store.Read),
            new ReadAccess(gateway, run.Store.Read, new SettingsWriter(run.Store, TimeProvider.System), TimeProvider.System),
            new HierarchySnapshot(gateway, new JournalStore(run.Store.Read)));
        return new UpdateHierarchyUseCase(services, new WritePipeline(new WriteLock(), gateway, PageTextCache.WithDefaults(), run.Store.Read));
    }

    private RenameHierarchyObjectTool RenameTool(Run run) => new(Runner(), UseCase(run));

    private ReorderHierarchyChildrenTool ReorderTool(Run run) => new(Runner(), UseCase(run));

    private static ToolRunner Runner() => new(NullLogger<ToolRunner>.Instance);

    /// <summary>A section or group made through story 5.3's create (granted notebooks only), recorded as this run's.</summary>
    private async Task<string> CreateSectionAsync(Run run, string parentId, string name)
    {
        var id = ObjectId(Ok(await new CreateSectionTool(Runner(), CreateUseCase(run)).CreateSection(parentId, name, Ct)));
        created.Add(id);
        return id;
    }

    private async Task<string> CreateGroupAsync(Run run, string parentId, string name)
    {
        var id = ObjectId(Ok(await new CreateSectionGroupTool(Runner(), CreateUseCase(run)).CreateSectionGroup(parentId, name, Ct)));
        created.Add(id);
        return id;
    }

    private static CreateHierarchyUseCase CreateUseCase(Run run) => new(
        run.Gateway,
        new WriteAccess(run.Gateway, run.Store.Read),
        new WritePipeline(new WriteLock(), run.Gateway, PageTextCache.WithDefaults(), run.Store.Read),
        run.Store.Read,
        new SettingsWriter(run.Store, TimeProvider.System),
        TimeProvider.System);

    /// <summary>A page in a section this run made, after the guard confirms the section is in a test notebook.</summary>
    private async Task<string> CreatePageAsync(Run run, string sectionId)
    {
        await TestNotebook.EnsureTestTargetAsync(run.Gateway, sectionId, "CreateNewPage(probe)", Ct);
        var id = await run.Gateway.CreateNewPageAsync(sectionId, NewPageStyle.BlankPageWithTitle, Ct);
        created.Add(id);
        return id;
    }

    /// <summary>The node's <c>lastModifiedTime</c> from a fresh read, as the tools' basis.</summary>
    private static async Task<PageBasisArgument> BasisAsync(Run run, string id, CoreScope scope) =>
        new(HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(id, scope, Ct)).LastModifiedTime);

    private async Task ReorderAndRecordAsync(Run run, string probe, string parentId, string[] order)
    {
        var result = await ReorderTool(run).ReorderHierarchyChildren(parentId, order, await BasisAsync(run, parentId, CoreScope.Children), Ct);
        var after = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(parentId, CoreScope.Children, Ct)).Children.Select(c => c.Id).ToList();
        output.WriteLine($"RESULT {probe}: {Outcome(result)}; read back in the order sent: {after.SequenceEqual(order)}");
    }

    private async Task SyncAsync(Run run, string notebookId)
    {
        await TestNotebook.EnsureTestTargetAsync(run.Gateway, notebookId, "SyncHierarchy(probe)", Ct);
        await run.Gateway.InvokeAsync(
            app =>
            {
                app.SyncHierarchy(notebookId);
                return true;
            },
            Ct,
            retryOnDisconnect: false,
            method: "SyncHierarchy(probe)");
    }

    private static JsonElement Ok(CallToolResult result)
    {
        var root = result.StructuredContent!.Value;
        Assert.False(result.IsError, $"the call failed: {(result.IsError == true ? root.GetProperty("error").GetProperty("code").GetString() : null)}");
        return root;
    }

    private static string? ErrorCode(CallToolResult result) =>
        result.IsError == true ? result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() : null;

    /// <summary>"ok" with its warning codes, or the error code and HRESULT.</summary>
    private static string Outcome(CallToolResult result)
    {
        var root = result.StructuredContent!.Value;
        if (result.IsError == true)
        {
            var error = root.GetProperty("error");
            return $"error {error.GetProperty("code").GetString()} {(error.TryGetProperty("hresult", out var hr) ? hr.GetString() : "no hresult")}";
        }

        return "ok [" + string.Join(", ", root.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString())) + "]";
    }

    private static string ObjectId(JsonElement root) => root.GetProperty("meta").GetProperty("objectId").GetString()!;

    /// <summary>
    /// Soft-deletes the sections and groups this run made, innermost first, through the guarded delete. Pages go with
    /// their section; the L5 sections Phill prepared and L8's notebook are never deleted. A failure is logged only.
    /// </summary>
    private async Task CleanUpAsync(Run run)
    {
        var ours = created.ToHashSet(StringComparer.Ordinal);
        foreach (var id in Enumerable.Reverse(created).ToList())
        {
            try
            {
                var self = XDocument.Parse(await run.Gateway.GetHierarchyAsync(id, CoreScope.Self, Ct)).Root!;
                var name = (string?)self.Attribute("name") ?? string.Empty;
                if (self.Name == One + "Page" || self.Name == One + "Notebook" || (string?)self.Attribute("isInRecycleBin") == "true" || name.StartsWith("Probe 5.4 Protected", StringComparison.Ordinal) || name.StartsWith("Probe 5.4 Locked", StringComparison.Ordinal))
                {
                    continue;
                }

                var date = OneNoteDates.ToUtc((string?)self.Attribute("lastModifiedTime")) ?? DateTime.MinValue;
                await TestNotebook.DeleteHierarchyAsync(run.Gateway, id, date, permanently: false, ours, throwaways, Ct);
                output.WriteLine($"soft-deleted {id}");
            }
#pragma warning disable CA1031 // A failed clean-up is logged and never fails the test.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                output.WriteLine($"clean-up of {id} failed: {ex.GetType().Name} {(ex as OneNoteException)?.Code}");
            }
        }
    }

    /// <summary>
    /// The real gateway, except that <c>UpdateHierarchy</c> goes through story 5.1's guard
    /// (<see cref="TestNotebook.UpdateHierarchyAsync"/>): every ID sent must be one this run made, inside a test notebook.
    /// </summary>
    private sealed class GuardedGateway(ComGateway inner, List<string> created, IReadOnlyDictionary<string, IReadOnlySet<string>> throwaways) : IOneNoteGateway
    {
        public Task UpdateHierarchyAsync(string changesXml, CancellationToken ct) =>
            TestNotebook.UpdateHierarchyAsync(inner, changesXml, created.ToHashSet(StringComparer.Ordinal), throwaways, "UpdateHierarchy(tool)", ct);

        public Task<string> GetHierarchyAsync(string startNodeId, CoreScope scope, CancellationToken ct) => inner.GetHierarchyAsync(startNodeId, scope, ct);

        public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) => inner.GetHierarchyParentAsync(objectId, ct);

        public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct) => inner.GetPageContentAsync(pageId, pageInfo, ct);

        public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) => inner.GetBinaryPageContentAsync(pageId, callbackId, ct);

        public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) => inner.FindPagesAsync(startNodeId, query, includeUnindexed, ct);

        public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) => inner.FindMetaAsync(startNodeId, name, includeUnindexed, ct);

        public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => inner.GetHyperlinkToObjectAsync(hierarchyId, pageContentObjectId, ct);

        public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => inner.GetWebHyperlinkToObjectAsync(hierarchyId, pageContentObjectId, ct);

        public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => inner.GetSpecialLocationAsync(location, ct);

        public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct) =>
            throw new InvalidOperationException("The probes never navigate (AD-11).");

        public Task<string> CreateNewPageAsync(string sectionId, NewPageStyle style, CancellationToken ct) =>
            throw new InvalidOperationException("The rename and reorder tools never create.");

        public Task<string> CreateSectionAsync(string parentId, string sectionFileName, CancellationToken ct) =>
            throw new InvalidOperationException("The rename and reorder tools never create.");

        public Task<string> CreateSectionGroupAsync(string parentId, string groupName, CancellationToken ct) =>
            throw new InvalidOperationException("The rename and reorder tools never create.");

        public Task<string> CreateNotebookAsync(string folderPath, CancellationToken ct) =>
            throw new InvalidOperationException("The rename and reorder tools never create.");

        public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct) =>
            throw new InvalidOperationException("The rename and reorder tools never write page content.");

        public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct) =>
            throw new InvalidOperationException("The rename and reorder tools never write page content.");

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => inner.WaitForAbandonedWritesAsync(ct);
    }
}
