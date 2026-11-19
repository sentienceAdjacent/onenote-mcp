using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Integration.WriteProbes;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration;

/// <summary>
/// Stories 3.4's and 3.12's live checks for the sittings (S1 in each plan), opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c>
/// and never run in a build: <c>dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~MoveHierarchyObjectIntegrationTests</c>,
/// with MCP Test and the throwaway <c>MCP Test Throwaway 1112-034716 a</c> open and the Ballast Vault unlocked (3.4's check
/// pauses for Phill to lock it; 3.12's needs it locked, and pauses if it isn't).
/// <list type="bullet">
/// <item>Every move goes through the tool, over a gateway whose <c>UpdateHierarchy</c> is story 5.1's guarded one
/// (<see cref="TestNotebook.UpdateHierarchyAsync"/>): every ID it sends must be an object this run made, inside MCP Test or
/// the adopted throwaway. The run makes its own sections (stamped names) and pages, and soft-deletes the sections at the
/// end; a failed clean-up is logged and never fails the test.</item>
/// <item>Grants and the recovery journal live in a temporary folder, never the user's settings or journal.</item>
/// <item>The Vault check reads one Vault page ID while the Vault is unlocked, then pauses (the probes' pause file) for Phill
/// to lock it with Ctrl+Alt+L; then both refusals run, and no <c>UpdateHierarchy</c> is sent.</item>
/// </list>
/// The output carries IDs, codes and the run's own stamped names only.
/// </summary>
public sealed class MoveHierarchyObjectIntegrationTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The local throwaway the cross-notebook move adopts by its exact name (spike 3.1's, still open).</summary>
    private const string ThrowawayName = "MCP Test Throwaway 1112-034716 a";

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    /// <summary>What this run made, in order; only these may be written to or deleted.</summary>
    private readonly List<string> created = [];

    /// <summary>The adopted throwaway, by ID, with its name: the guard accepts writes there.</summary>
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

    /// <summary>
    /// In MCP Test: a page with a subpage moves to the middle of another run section. The page lands at position 2 under
    /// a new ID, the old ID is gone, its subpage stays behind at level 1, and both sections have a journal entry.
    /// </summary>
    [Fact]
    public async Task SameNotebook_APageWithASubpage_MovesToPosition2_UnderANewId_TheSubpageStays()
    {
        await using var run = await StartAsync();
        var stamp = Stamp;
        try
        {
            var source = await CreateSectionAsync(run, run.NotebookId, $"Probe 3.4 source {stamp}");
            var target = await CreateSectionAsync(run, run.NotebookId, $"Probe 3.4 target {stamp}");
            var page = await CreatePageAsync(run, source);
            var subpage = await CreatePageAsync(run, source);
            var first = await CreatePageAsync(run, target);
            var second = await CreatePageAsync(run, target);

            // The subpage through the guarded raw UpdateHierarchy (the tools never change pageLevel), as 5.4's L6 did.
            var level = $"<one:Section xmlns:one=\"{OneNoteXml.Namespace2013}\" ID=\"{source}\"><one:Page ID=\"{page}\" /><one:Page ID=\"{subpage}\" pageLevel=\"2\" /></one:Section>";
            await TestNotebook.UpdateHierarchyAsync(run.Com, level, created.ToHashSet(StringComparer.Ordinal), throwaways, "UpdateHierarchy(3.4 subpage level)", Ct);
            Assert.Equal(2, HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(source, CoreScope.Children, Ct)).Children.Single(c => c.Id == subpage).PageLevel);

            // AD-17: a node's date can move about a second after a hierarchy write; let it settle before reading the basis.
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            var root = Ok(await Tool(run).MoveHierarchyObject(page, target, 2, await BasisAsync(run, source, page), Ct));
            var moved = NewId(root, page);

            var after = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(target, CoreScope.Children, Ct));
            Assert.Equal([first, moved, second], after.Children.Select(c => c.Id));
            Assert.Equal(1, after.Children[1].PageLevel);
            var left = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(source, CoreScope.Children, Ct));
            Assert.DoesNotContain(left.Children, c => c.Id == page);
            Assert.Equal(1, Assert.Single(left.Children, c => c.Id == subpage).PageLevel);
            var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
            Assert.Equal(ToolWarnings.SubpagesLeftBehind, warning.GetProperty("code").GetString());
            AssertTwoEntries(run, root, source, target);
            output.WriteLine($"RESULT same notebook: page {page} -> {moved} at position 2 of {target}; subpage {subpage} stayed at level 1");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>
    /// To the adopted throwaway (skipped when it isn't open): a page moves under a new ID with two journal entries; then,
    /// with the throwaway's write grant removed, another move is <c>WriteNotGranted</c> and nothing is sent.
    /// </summary>
    [Fact]
    public async Task OtherNotebook_ToTheThrowaway_MovesUnderANewId_ThenWithoutItsGrant_IsWriteNotGranted()
    {
        await using var run = await StartAsync();
        var throwaway = await TestNotebook.AdoptThrowawayNotebookAsync(run.Com, ThrowawayName, Ct);
        Assert.SkipWhen(throwaway is null, $"Open the throwaway notebook \"{ThrowawayName}\" in OneNote first.");
        throwaways[throwaway!] = new HashSet<string>([ThrowawayName]);
        var throwawayKey = GrantKeys.NotebookKey(HierarchyNode.Root(await run.Com.GetHierarchyAsync(throwaway!, CoreScope.Self, Ct)).Path);
        var writer = new SettingsWriter(run.Store, TimeProvider.System);
        writer.SaveWriteGrant(new NotebookGrant(throwawayKey, ThrowawayName, throwaway!, Write: true, PermanentDelete: false, DateTimeOffset.UtcNow, GrantedVia.Cli));
        var stamp = Stamp;
        try
        {
            var source = await CreateSectionAsync(run, run.NotebookId, $"Probe 3.4 cross {stamp}");
            var target = await CreateSectionAsync(run, throwaway!, $"Probe 3.4 landing {stamp}");
            var page = await CreatePageAsync(run, source);
            var stays = await CreatePageAsync(run, source);

            // AD-17: a node's date can move about a second after a hierarchy write; let it settle before reading the basis.
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            var root = Ok(await Tool(run).MoveHierarchyObject(page, target, null, await BasisAsync(run, source, page), Ct));
            var moved = NewId(root, page);
            Assert.Equal([moved], HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(target, CoreScope.Children, Ct)).Children.Select(c => c.Id));
            Assert.DoesNotContain(HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(source, CoreScope.Children, Ct)).Children, c => c.Id == page);
            AssertTwoEntries(run, root, source, target);
            output.WriteLine($"RESULT other notebook: page {page} -> {moved} in {target}");

            // The throwaway's grant removed: the move is refused before anything is sent.
            Assert.Equal(1, writer.RevokeWriteGrant(throwawayKey));
            var calls = run.Gateway.UpdateHierarchyCalls;
            var refused = await Tool(run).MoveHierarchyObject(stays, target, null, await BasisAsync(run, source, stays), Ct);
            Assert.Equal("WriteNotGranted", Code(refused));
            Assert.Equal(calls, run.Gateway.UpdateHierarchyCalls);
            Assert.Contains(HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(source, CoreScope.Children, Ct)).Children, c => c.Id == stays);
            output.WriteLine("RESULT not granted: WriteNotGranted, nothing sent");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>
    /// The Ballast Vault, read unlocked and then locked by Phill: moving its page is <c>NotSupported</c>, and moving a run
    /// page into it is <c>SectionLocked</c>. No <c>UpdateHierarchy</c> is sent.
    /// </summary>
    [Fact]
    public async Task TheBallastVault_Locked_APageOutIsNotSupported_APageInIsSectionLocked()
    {
        await using var run = await StartAsync();
        var children = XDocument.Parse(await run.Gateway.GetHierarchyAsync(run.NotebookId, CoreScope.Children, Ct));
        var vault = children.Root!.Elements(One + "Section").FirstOrDefault(s => (string?)s.Attribute("name") == BallastContent.VaultSectionName);
        Assert.SkipWhen(vault is null, "MCP Test has no Ballast Vault: run the Ballast scenario (story 17).");
        Assert.SkipUnless((string?)vault!.Attribute("encrypted") is "true" or "1", "The Ballast Vault isn't password-protected yet (story 17's owner step).");
        Assert.SkipWhen((string?)vault.Attribute("locked") is "true" or "1", "Unlock the Ballast Vault in OneNote first: its page IDs are read while it is unlocked.");
        var vaultId = (string)vault.Attribute("ID")!;

        // Hierarchy only (IDs, never content): one page of the Vault, read while it is unlocked.
        var vaultPage = XDocument.Parse(await run.Gateway.GetHierarchyAsync(vaultId, CoreScope.Pages, Ct))
            .Descendants(One + "Page").Select(p => (string?)p.Attribute("ID")).FirstOrDefault();
        Assert.SkipWhen(vaultPage is null, "The Ballast Vault has no page.");
        output.WriteLine($"Vault {vaultId}: page {vaultPage}");

        var stamp = Stamp;
        try
        {
            var section = await CreateSectionAsync(run, run.NotebookId, $"Probe 3.4 vault {stamp}");
            var page = await CreatePageAsync(run, section);

            Assert.True(
                await PauseAsync("vault-lock", "In OneNote, press Ctrl+Alt+L to lock the password-protected sections (the Ballast Vault in MCP Test). Then continue."),
                "The Vault must be locked for this check.");
            var now = XDocument.Parse(await run.Gateway.GetHierarchyAsync(vaultId, CoreScope.Self, Ct)).Root!;
            Assert.True((string?)now.Attribute("locked") is "true" or "1", "The Ballast Vault still reads as unlocked.");

            var outOf = await Tool(run).MoveHierarchyObject(vaultPage!, section, null, null, Ct);
            Assert.Equal("NotSupported", Code(outOf));
            Assert.DoesNotContain(BallastContent.VaultSectionName, outOf.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);

            Assert.Equal("SectionLocked", Code(await Tool(run).MoveHierarchyObject(page, vaultId, null, null, Ct)));
            Assert.Equal(0, run.Gateway.UpdateHierarchyCalls);
            output.WriteLine("RESULT vault: a page out is NotSupported, a page in is SectionLocked; nothing sent");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    // --- Story 3.12: sections and section groups ----------------------------------------------------------------------

    /// <summary>
    /// In MCP Test (story 3.12): a run section with a page moves into a run group, before the run section already there;
    /// then a second run group, holding a section, moves into the first group. Each keeps its ID, the page stays in its
    /// section, and both parents have a journal entry each time. Only run-made IDs are sent (the guard).
    /// </summary>
    [Fact]
    public async Task SameNotebook_ASectionAndASectionGroup_MoveIntoAGroup_KeepingTheirIds()
    {
        await using var run = await StartAsync();
        var stamp = Stamp;
        try
        {
            var group = await CreateGroupAsync(run, run.NotebookId, $"Probe 3-12 group {stamp}");
            var inner = await CreateGroupAsync(run, run.NotebookId, $"Probe 3-12 inner {stamp}");
            var resident = await CreateSectionAsync(run, group, $"Probe 3.12 resident {stamp}");
            var section = await CreateSectionAsync(run, run.NotebookId, $"Probe 3.12 moving {stamp}");
            var page = await CreatePageAsync(run, section);
            await CreateSectionAsync(run, inner, $"Probe 3.12 carried {stamp}");

            // AD-17: a node's date can move about a second after a hierarchy write; let it settle before reading the basis.
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            var root = Ok(await Tool(run).MoveHierarchyObject(section, group, 1, await NodeBasisAsync(run, section), Ct));
            Assert.Equal(section, root.GetProperty("meta").GetProperty("objectId").GetString());
            var sections = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(group, CoreScope.Children, Ct)).Children
                .Where(c => c.Kind == HierarchyKind.Section).Select(c => c.Id);
            Assert.Equal([section, resident], sections);
            Assert.Contains(HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(section, CoreScope.Children, Ct)).Children, c => c.Id == page);
            AssertTwoEntries(run, root, run.NotebookId, group);
            Assert.Empty(root.GetProperty("warnings").EnumerateArray());
            output.WriteLine($"RESULT section: {section} into group {group} at position 1, page {page} with it");

            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            var groupRoot = Ok(await Tool(run).MoveHierarchyObject(inner, group, null, await NodeBasisAsync(run, inner), Ct));
            Assert.Equal(inner, groupRoot.GetProperty("meta").GetProperty("objectId").GetString());
            Assert.Contains(HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(group, CoreScope.Children, Ct)).Children, c => c.Id == inner);
            Assert.DoesNotContain(HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(run.NotebookId, CoreScope.Children, Ct)).Children, c => c.Id == inner);
            AssertTwoEntries(run, groupRoot, run.NotebookId, group);
            output.WriteLine($"RESULT group: {inner} into group {group}");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>
    /// To the adopted throwaway (story 3.12; skipped when it isn't open): a run section moves into a run group there under
    /// its own ID, with two journal entries; then, with the throwaway's write grant removed, another section's move is
    /// <c>WriteNotGranted</c> and nothing is sent. A move to the throwaway's root lists its other sections, which the guard
    /// refuses, so sitting S2 makes that one through Claude.
    /// </summary>
    [Fact]
    public async Task OtherNotebook_ASectionIntoAGroupInTheThrowaway_KeepsItsId_ThenWithoutItsGrant_IsWriteNotGranted()
    {
        await using var run = await StartAsync();
        var throwaway = await TestNotebook.AdoptThrowawayNotebookAsync(run.Com, ThrowawayName, Ct);
        Assert.SkipWhen(throwaway is null, $"Open the throwaway notebook \"{ThrowawayName}\" in OneNote first.");
        throwaways[throwaway!] = new HashSet<string>([ThrowawayName]);
        var throwawayKey = GrantKeys.NotebookKey(HierarchyNode.Root(await run.Com.GetHierarchyAsync(throwaway!, CoreScope.Self, Ct)).Path);
        var writer = new SettingsWriter(run.Store, TimeProvider.System);
        writer.SaveWriteGrant(new NotebookGrant(throwawayKey, ThrowawayName, throwaway!, Write: true, PermanentDelete: false, DateTimeOffset.UtcNow, GrantedVia.Cli));
        var stamp = Stamp;
        try
        {
            var landing = await CreateGroupAsync(run, throwaway!, $"Probe 3-12 landing {stamp}");
            var section = await CreateSectionAsync(run, run.NotebookId, $"Probe 3.12 cross {stamp}");
            var stays = await CreateSectionAsync(run, run.NotebookId, $"Probe 3.12 stays {stamp}");
            var page = await CreatePageAsync(run, section);

            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            var root = Ok(await Tool(run).MoveHierarchyObject(section, landing, null, await NodeBasisAsync(run, section), Ct));
            Assert.Equal(section, root.GetProperty("meta").GetProperty("objectId").GetString());
            Assert.Equal([section], HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(landing, CoreScope.Children, Ct)).Children.Select(c => c.Id));
            Assert.Contains(HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(section, CoreScope.Children, Ct)).Children, c => c.Id == page);
            AssertTwoEntries(run, root, run.NotebookId, landing);
            output.WriteLine($"RESULT other notebook: section {section} into {landing}");

            Assert.Equal(1, writer.RevokeWriteGrant(throwawayKey));
            var calls = run.Gateway.UpdateHierarchyCalls;
            var refused = await Tool(run).MoveHierarchyObject(stays, landing, null, null, Ct);
            Assert.Equal("WriteNotGranted", Code(refused));
            Assert.Equal(calls, run.Gateway.UpdateHierarchyCalls);
            output.WriteLine("RESULT not granted: WriteNotGranted, nothing sent");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>
    /// Q14 (story 3.12): moving the Ballast Vault itself, locked, into a run group is <c>NotSupported</c>, never
    /// <c>SectionLocked</c> (Fable M1), and no <c>UpdateHierarchy</c> is sent. If the Vault is unlocked, the run pauses for
    /// Phill to lock it.
    /// </summary>
    [Fact]
    public async Task TheBallastVault_Locked_MovedWhole_IsNotSupported_NothingSent()
    {
        await using var run = await StartAsync();
        var children = XDocument.Parse(await run.Gateway.GetHierarchyAsync(run.NotebookId, CoreScope.Children, Ct));
        var vault = children.Root!.Elements(One + "Section").FirstOrDefault(s => (string?)s.Attribute("name") == BallastContent.VaultSectionName);
        Assert.SkipWhen(vault is null, "MCP Test has no Ballast Vault: run the Ballast scenario (story 17).");
        Assert.SkipUnless((string?)vault!.Attribute("encrypted") is "true" or "1", "The Ballast Vault isn't password-protected yet (story 17's owner step).");
        var vaultId = (string)vault.Attribute("ID")!;
        var stamp = Stamp;
        try
        {
            var group = await CreateGroupAsync(run, run.NotebookId, $"Probe 3-12 vault {stamp}");
            if ((string?)vault.Attribute("locked") is not ("true" or "1"))
            {
                Assert.True(
                    await PauseAsync("vault-lock-3.12", "In OneNote, press Ctrl+Alt+L to lock the password-protected sections (the Ballast Vault in MCP Test). Then continue."),
                    "The Vault must be locked for this check.");
            }

            var now = XDocument.Parse(await run.Gateway.GetHierarchyAsync(vaultId, CoreScope.Self, Ct)).Root!;
            Assert.True((string?)now.Attribute("locked") is "true" or "1", "The Ballast Vault still reads as unlocked.");

            var refused = await Tool(run).MoveHierarchyObject(vaultId, group, null, null, Ct);
            Assert.Equal("NotSupported", Code(refused));
            Assert.Equal(MoveContainerWrite.ProtectedUserAction, refused.StructuredContent!.Value.GetProperty("error").GetProperty("userAction").GetString());
            Assert.DoesNotContain(BallastContent.VaultSectionName, refused.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(0, run.Gateway.UpdateHierarchyCalls);
            output.WriteLine("RESULT vault moved whole: NotSupported; nothing sent");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    // --- Setup ------------------------------------------------------------------------------------------------------

    private sealed record Run(ComGateway Com, GuardedGateway Gateway, string NotebookId, SettingsStore Store, string JournalFolder) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Com.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A real gateway over a temporary <c>settings.json</c> and journal that grant MCP Test.</summary>
    private async Task<Run> StartAsync()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var journal = Path.Combine(tempDir, "journal");
        File.WriteAllText(settingsPath, """{ "schemaVersion": 1 }""");
        var store = new SettingsStore(settingsPath, journal);
        var com = new ComGateway(new ProcessProbe(), store);
        try
        {
            var notebook = await TestNotebook.EnsureAsync(com, Ct);
            var self = HierarchyNode.Root(await com.GetHierarchyAsync(notebook.NotebookId, CoreScope.Self, Ct));
            Assert.Equal(TestNotebook.NotebookName, self.Name);
            new SettingsWriter(store, TimeProvider.System).SaveWriteGrant(
                new NotebookGrant(GrantKeys.NotebookKey(self.Path), TestNotebook.NotebookName, notebook.NotebookId, Write: true, PermanentDelete: false, DateTimeOffset.UtcNow, GrantedVia.Cli));
            return new Run(com, new GuardedGateway(com, created, throwaways), notebook.NotebookId, store, journal);
        }
        catch
        {
            com.Dispose();
            throw;
        }
    }

    /// <summary>The tool over <see cref="GuardedGateway"/>, so every <c>UpdateHierarchy</c> passes the test-notebook guard. No elicitation.</summary>
    private static MoveHierarchyObjectTool Tool(Run run)
    {
        var services = new HierarchyWriteServices(
            run.Gateway,
            new WriteAccess(run.Gateway, run.Store.Read),
            new ReadAccess(run.Gateway, run.Store.Read, new SettingsWriter(run.Store, TimeProvider.System), TimeProvider.System),
            new HierarchySnapshot(run.Gateway, new JournalStore(run.Store.Read)));
        return new MoveHierarchyObjectTool(
            new ToolRunner(NullLogger<ToolRunner>.Instance),
            new MoveHierarchyObjectUseCase(services, new WritePipeline(new WriteLock(), run.Gateway, PageTextCache.WithDefaults(), run.Store.Read)));
    }

    /// <summary>A section under <paramref name="parentId"/> through the guarded raw write (test or adopted throwaway notebook only), recorded as this run's.</summary>
    private async Task<string> CreateSectionAsync(Run run, string parentId, string name)
    {
        var id = await TestNotebook.WriteAsync(
            run.Com,
            parentId,
            app =>
            {
                app.OpenHierarchy(name + ".one", parentId, out var section, Com.Interop.CreateFileType.cftSection);
                return section;
            },
            "OpenHierarchy(3.4 probe section)",
            Ct,
            throwaways);
        created.Add(id);
        return id;
    }

    /// <summary>A section group under <paramref name="parentId"/> through the guarded raw write, recorded as this run's. No '.' in the name (0x80042018).</summary>
    private async Task<string> CreateGroupAsync(Run run, string parentId, string name)
    {
        var id = await TestNotebook.WriteAsync(
            run.Com,
            parentId,
            app =>
            {
                app.OpenHierarchy(name, parentId, out var group, Com.Interop.CreateFileType.cftFolder);
                return group;
            },
            "OpenHierarchy(3.12 probe section group)",
            Ct,
            throwaways);
        created.Add(id);
        return id;
    }

    /// <summary>A section's or section group's own hierarchy <c>lastModifiedTime</c>, read at <c>self</c>, as the move's basis (AD-17).</summary>
    private static async Task<PageBasisArgument> NodeBasisAsync(Run run, string id) =>
        new(HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(id, CoreScope.Self, Ct)).LastModifiedTime);

    /// <summary>A page in a section this run made, after the guard confirms the section is in a test or adopted notebook.</summary>
    private async Task<string> CreatePageAsync(Run run, string sectionId)
    {
        var id = await TestNotebook.WriteAsync(
            run.Com,
            sectionId,
            app =>
            {
                app.CreateNewPage(sectionId, out var page, Com.Interop.NewPageStyle.npsBlankPageWithTitle);
                return page;
            },
            "CreateNewPage(3.4 probe page)",
            Ct,
            throwaways);
        created.Add(id);
        return id;
    }

    /// <summary>The page's hierarchy <c>lastModifiedTime</c> from a fresh read of its section, as the move's basis (AD-17).</summary>
    private static async Task<PageBasisArgument> BasisAsync(Run run, string sectionId, string pageId) =>
        new(HierarchyTarget.Page(await run.Gateway.GetHierarchyAsync(sectionId, CoreScope.Children, Ct), pageId)!.LastModifiedTime);

    /// <summary>The moved page's new ID from <c>meta.objectId</c>, recorded as this run's; it must differ from the old one.</summary>
    private string NewId(JsonElement root, string oldId)
    {
        var moved = root.GetProperty("meta").GetProperty("objectId").GetString()!;
        Assert.NotEqual(oldId, moved);
        created.Add(moved);
        return moved;
    }

    /// <summary>Both journal entries exist, source then target, each a <c>hierarchy</c> entry of its section.</summary>
    private static void AssertTwoEntries(Run run, JsonElement root, string source, string target)
    {
        var ids = root.GetProperty("meta").GetProperty("journalEntryIds").EnumerateArray().Select(e => Guid.Parse(e.GetString()!)).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids[0], Guid.Parse(root.GetProperty("meta").GetProperty("journalEntryId").GetString()!));
        var entries = ids.Select(id => JournalStore.Read(JournalFiles.Find(run.JournalFolder, id)!)).ToList();
        Assert.Equal([source, target], entries.Select(e => e.ObjectId));
        Assert.All(entries, e => Assert.Equal(JournalEntry.HierarchyKind, e.SnapshotKind));
    }

    /// <summary>
    /// The probes' hitl pause (<see cref="ProbeSession.PauseAsync"/>'s file and protocol): writes the instruction to the
    /// pause file and waits until the main session deletes it (true) or writes <c>skip</c> into it (false).
    /// </summary>
    private async Task<bool> PauseAsync(string step, string instruction)
    {
        Directory.CreateDirectory(ProbeRecorder.ProbesDir);
        var file = Path.Combine(ProbeRecorder.ProbesDir, ProbeSession.PauseFileName);
        await File.WriteAllTextAsync(file, $"[move / {step}]\n{instruction}\n\nWhen done, delete this file. To skip this step, replace this file's content with: skip\n", Ct);
        output.WriteLine($"PAUSE ({file}): {instruction}");
        Console.WriteLine($"PAUSE [move / {step}]: {instruction}");

        var limit = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("ONENOTE_MCP_PROBE_PAUSE_SECONDS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 1800);
        var waited = Stopwatch.StartNew();
        while (true)
        {
            if (!File.Exists(file))
            {
                return true;
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, Ct);
            }
            catch (IOException)
            {
                content = string.Empty;
            }

            if (content.Trim().Equals("skip", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                return false;
            }

            if (waited.Elapsed > limit)
            {
                File.Delete(file);
                throw new TimeoutException($"No answer to the pause for {step} within {limit.TotalSeconds} s.");
            }

            await Task.Delay(500, Ct);
        }
    }

    private static JsonElement Ok(CallToolResult result)
    {
        var root = result.StructuredContent!.Value;
        Assert.False(result.IsError, $"the call failed: {(result.IsError == true ? root.GetProperty("error").GetProperty("code").GetString() : null)}");
        return root;
    }

    private static string? Code(CallToolResult result) =>
        result.IsError == true ? result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() : null;

    /// <summary>
    /// Soft-deletes the sections and section groups this run made, last first, through the guarded delete; the moved pages
    /// go with them, and a group's sections land in the recycle bin one by one. A failure is logged only.
    /// </summary>
    private async Task CleanUpAsync(Run run)
    {
        var ours = created.ToHashSet(StringComparer.Ordinal);
        foreach (var id in Enumerable.Reverse(created).ToList())
        {
            try
            {
                var self = XDocument.Parse(await run.Com.GetHierarchyAsync(id, CoreScope.Self, Ct)).Root!;
                if ((self.Name != One + "Section" && self.Name != One + "SectionGroup") || (string?)self.Attribute("isInRecycleBin") == "true")
                {
                    continue;
                }

                var date = OneNoteDates.ToUtc((string?)self.Attribute("lastModifiedTime")) ?? DateTime.MinValue;
                await TestNotebook.DeleteHierarchyAsync(run.Com, id, date, permanently: false, ours, throwaways, Ct);
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
    /// (<see cref="TestNotebook.UpdateHierarchyAsync"/>) and is counted, and every other write is refused. Reads go straight through.
    /// </summary>
    private sealed class GuardedGateway(ComGateway inner, List<string> created, IReadOnlyDictionary<string, IReadOnlySet<string>> throwaways) : IOneNoteGateway
    {
        private int updateHierarchyCalls;

        /// <summary>How many <c>UpdateHierarchy</c> calls the tool sent (each passed the guard first).</summary>
        public int UpdateHierarchyCalls => Volatile.Read(ref updateHierarchyCalls);

        public async Task UpdateHierarchyAsync(string changesXml, CancellationToken ct)
        {
            Interlocked.Increment(ref updateHierarchyCalls);
            await TestNotebook.UpdateHierarchyAsync(inner, changesXml, created.ToHashSet(StringComparer.Ordinal), throwaways, "UpdateHierarchy(move tool)", ct);
        }

        public Task<string> GetHierarchyAsync(string startNodeId, CoreScope scope, CancellationToken ct) => inner.GetHierarchyAsync(startNodeId, scope, ct);

        public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) => inner.GetHierarchyParentAsync(objectId, ct);

        public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct) => inner.GetPageContentAsync(pageId, pageInfo, ct);

        public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) => inner.GetBinaryPageContentAsync(pageId, callbackId, ct);

        public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) => inner.FindPagesAsync(startNodeId, query, includeUnindexed, ct);

        public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) => inner.FindMetaAsync(startNodeId, name, includeUnindexed, ct);

        public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => inner.GetHyperlinkToObjectAsync(hierarchyId, pageContentObjectId, ct);

        public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => inner.GetWebHyperlinkToObjectAsync(hierarchyId, pageContentObjectId, ct);

        public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => inner.GetSpecialLocationAsync(location, ct);

        public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct) => Refused("NavigateTo");

        public Task<string> CreateNewPageAsync(string sectionId, NewPageStyle style, CancellationToken ct) => Refused<string>("CreateNewPage");

        public Task<string> CreateSectionAsync(string parentId, string sectionFileName, CancellationToken ct) => Refused<string>("OpenHierarchy");

        public Task<string> CreateSectionGroupAsync(string parentId, string groupName, CancellationToken ct) => Refused<string>("OpenHierarchy");

        public Task<string> CreateNotebookAsync(string folderPath, CancellationToken ct) => Refused<string>("OpenHierarchy");

        public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct) => Refused("UpdatePageContent");

        public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct) => Refused("DeletePageContent");

        public Task DeleteHierarchyAsync(string objectId, string expectedLastModified, bool permanent, CancellationToken ct) => Refused("DeleteHierarchy");

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => inner.WaitForAbandonedWritesAsync(ct);

        private static Task Refused(string method) =>
            Task.FromException(new InvalidOperationException($"The move tool never calls {method}."));

        private static Task<T> Refused<T>(string method) =>
            Task.FromException<T>(new InvalidOperationException($"The move tool never calls {method}."));
    }
}
