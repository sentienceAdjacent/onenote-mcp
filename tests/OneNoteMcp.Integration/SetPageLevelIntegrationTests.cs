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
/// Story 3.14's live checks for the sitting (S1 in the plan), opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c> and never run in
/// a build: <c>dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~SetPageLevelIntegrationTests</c>, with
/// MCP Test open and the Ballast Vault unlocked.
/// <list type="bullet">
/// <item>Every <c>UpdateHierarchy</c>, the tools' and the probe's, goes through story 5.1's guard
/// (<see cref="TestNotebook.UpdateHierarchyAsync"/>): every ID it sends must be an object this run made, inside MCP Test.
/// The tools' <c>CreateNewPage</c> goes through the guarded raw write into a section this run made. The run makes its own
/// sections (stamped names) and pages, and soft-deletes the sections at the end; a failed clean-up is logged and never fails
/// the test.</item>
/// <item>Grants and the recovery journal live in a temporary folder, never the user's settings or journal.</item>
/// <item>The Vault check reads one Vault page ID while the Vault is unlocked, then pauses (the probes' pause file) for Phill
/// to lock it with Ctrl+Alt+L; then <c>set_page_level</c> is <c>SectionLocked</c>, and no <c>UpdateHierarchy</c> is sent.</item>
/// </list>
/// Each check writes a <c>RESULT</c> line. The output carries IDs, codes, levels and the run's own stamped names only.
/// </summary>
public sealed class SetPageLevelIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    /// <summary>What this run made, in order; only these may be written to or deleted.</summary>
    private readonly List<string> created = [];

    /// <summary>No throwaway notebook is adopted: every write stays in MCP Test.</summary>
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
    /// S1.1, the level-4 probe: the guarded raw <c>UpdateHierarchy</c> sends a run page at <c>pageLevel="4"</c> after pages at
    /// levels 1, 2 and 3. It records OneNote's HRESULT, or the level read back. Expected: refused, or read back at 3 or lower
    /// (a level 4 kept reopens the cap with Phill, since the AD-16 gate refuses our own level-4 XML).
    /// </summary>
    [Fact]
    public async Task Level4_IsRefused_OrReadsBackAt3OrLower()
    {
        await using var run = await StartAsync();
        try
        {
            var section = await CreateSectionAsync(run, $"Probe 3.14 level4 {Stamp}");
            var pages = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                pages.Add(await CreatePageAsync(run, section));
            }

            await SetLevelsRawAsync(run, section, pages, [1, 2, 3], "UpdateHierarchy(3.14 levels 1-3)");
            Assert.Equal([1, 2, 3, 1], await LevelsAsync(run, section, pages));

            string outcome;
            try
            {
                await SetLevelsRawAsync(run, section, pages, [1, 2, 3, 4], "UpdateHierarchy(3.14 level 4 probe)");
                var read = (await LevelsAsync(run, section, pages))[3];
                outcome = string.Create(CultureInfo.InvariantCulture, $"accepted; read back at level {read}");
                Assert.True(read <= 3, $"OneNote kept level {read}: the cap reopens with Phill (plan, Design Notes).");
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                var hresult = (ex as OneNoteException)?.ComHResult ?? ex.HResult;
                outcome = string.Create(CultureInfo.InvariantCulture, $"refused: {ex.GetType().Name} {(ex as OneNoteException)?.Code} 0x{hresult:X8}");
            }

            output.WriteLine($"RESULT level 4: {outcome}; levels now {string.Join(",", await LevelsAsync(run, section, pages))}");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>
    /// S1.2 and S1.3: through the tool, a page goes to 2, then 3, then back to 1, keeping its ID, with every other page's
    /// level unchanged and <c>GetPageContent</c>'s root level recorded at each step; then a page with a subpage is indented,
    /// and the subpage moves to 3.
    /// </summary>
    [Fact]
    public async Task IndentAndOutdent_KeepTheId_AndTheOtherLevels_ThenABlockMovesWhole()
    {
        await using var run = await StartAsync();
        try
        {
            var section = await CreateSectionAsync(run, $"Probe 3.14 levels {Stamp}");
            var p = await CreatePageAsync(run, section);
            var q = await CreatePageAsync(run, section);
            var a = await CreatePageAsync(run, section);
            var z = await CreatePageAsync(run, section);
            var pages = new List<string> { p, q, a, z };
            await SetLevelsRawAsync(run, section, pages, [1, 2, 1, 1], "UpdateHierarchy(3.14 setup Q at 2)");

            foreach (var (level, expected) in new[] { (2, new[] { 1, 2, 2, 1 }), (3, new[] { 1, 2, 3, 1 }), (1, new[] { 1, 2, 1, 1 }) })
            {
                // AD-17: a node's date can move about a second after a hierarchy write; let it settle before the basis.
                await Task.Delay(TimeSpan.FromSeconds(2), Ct);
                var root = Ok(await Tool(run).SetPageLevel(a, level, await BasisAsync(run, section, a), Ct));
                Assert.Equal(a, root.GetProperty("meta").GetProperty("objectId").GetString());
                Assert.Equal(section, root.GetProperty("meta").GetProperty("parentId").GetString());
                Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
                Assert.Equal(pages, await IdsAsync(run, section));
                Assert.Equal(expected, await LevelsAsync(run, section, pages));
                AssertEntry(run, root, section);
                output.WriteLine($"RESULT level {level}: page {a} kept its ID; levels {string.Join(",", expected)}; GetPageContent root pageLevel={await ContentLevelAsync(run, a)}");
            }

            // S1.3: a page with a subpage indented; the subpage moves with it to 3.
            var block = await CreateSectionAsync(run, $"Probe 3.14 block {Stamp}");
            var bp = await CreatePageAsync(run, block);
            var ba = await CreatePageAsync(run, block);
            var bb = await CreatePageAsync(run, block);
            var blockPages = new List<string> { bp, ba, bb };
            await SetLevelsRawAsync(run, block, blockPages, [1, 1, 2], "UpdateHierarchy(3.14 setup subpage)");
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            Ok(await Tool(run).SetPageLevel(ba, 2, await BasisAsync(run, block, ba), Ct));
            Assert.Equal(blockPages, await IdsAsync(run, block));
            Assert.Equal([1, 2, 3], await LevelsAsync(run, block, blockPages));
            output.WriteLine($"RESULT block: page {ba} to 2, its subpage {bb} to 3, IDs kept");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>S1.4: <c>create_page</c> with a parent that has 2 subpages lands after them, one level deeper than the parent.</summary>
    [Fact]
    public async Task CreateUnderAParent_LandsAfterItsSubpages_OneLevelDeeper()
    {
        await using var run = await StartAsync();
        try
        {
            var section = await CreateSectionAsync(run, $"Probe 3.14 create {Stamp}");
            var parent = await CreatePageAsync(run, section);
            var s1 = await CreatePageAsync(run, section);
            var s2 = await CreatePageAsync(run, section);
            var after = await CreatePageAsync(run, section);
            await SetLevelsRawAsync(run, section, [parent, s1, s2, after], [1, 2, 2, 1], "UpdateHierarchy(3.14 setup subpages)");

            var root = Ok(await CreateTool(run).CreatePage(section, cancellationToken: Ct, parentPageId: parent));
            var page = root.GetProperty("meta").GetProperty("pageId").GetString()!;
            Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
            Assert.Equal([parent, s1, s2, page, after], await IdsAsync(run, section));
            Assert.Equal([1, 2, 2, 2, 1], await LevelsAsync(run, section, [parent, s1, s2, page, after]));
            AssertEntry(run, root, section);
            output.WriteLine($"RESULT create under parent: page {page} after {s2} at level 2; GetPageContent root pageLevel={await ContentLevelAsync(run, page)}");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>S1.5: the Ballast Vault, read unlocked and then locked by Phill: <c>set_page_level</c> is <c>SectionLocked</c>, nothing sent.</summary>
    [Fact]
    public async Task TheBallastVault_Locked_IsSectionLocked_NothingSent()
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

        Assert.True(
            await PauseAsync("vault-lock", "In OneNote, press Ctrl+Alt+L to lock the password-protected sections (the Ballast Vault in MCP Test). Then continue."),
            "The Vault must be locked for this check.");
        var now = XDocument.Parse(await run.Gateway.GetHierarchyAsync(vaultId, CoreScope.Self, Ct)).Root!;
        Assert.True((string?)now.Attribute("locked") is "true" or "1", "The Ballast Vault still reads as unlocked.");

        var result = await Tool(run).SetPageLevel(vaultPage!, 2, null, Ct);
        Assert.Equal("SectionLocked", Code(result));
        Assert.DoesNotContain(BallastContent.VaultSectionName, result.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(0, run.Gateway.UpdateHierarchyCalls);
        output.WriteLine("RESULT vault: set_page_level is SectionLocked; nothing sent");
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

    private static HierarchyWriteServices Services(Run run) => new(
        run.Gateway,
        new WriteAccess(run.Gateway, run.Store.Read),
        new ReadAccess(run.Gateway, run.Store.Read, new SettingsWriter(run.Store, TimeProvider.System), TimeProvider.System),
        new HierarchySnapshot(run.Gateway, new JournalStore(run.Store.Read)));

    private static WritePipeline Pipeline(Run run) => new(new WriteLock(), run.Gateway, PageTextCache.WithDefaults(), run.Store.Read);

    /// <summary><c>set_page_level</c> over <see cref="GuardedGateway"/>. No elicitation.</summary>
    private static SetPageLevelTool Tool(Run run) =>
        new(new ToolRunner(NullLogger<ToolRunner>.Instance), new SetPageLevelUseCase(Services(run), Pipeline(run)));

    /// <summary><c>create_page</c> as the server builds it, over <see cref="GuardedGateway"/>. No elicitation.</summary>
    private static CreatePageTool CreateTool(Run run)
    {
        var services = Services(run);
        return new CreatePageTool(
            new ToolRunner(NullLogger<ToolRunner>.Instance),
            new CreatePageUseCase(run.Gateway, services.WriteAccess, Pipeline(run), services.ReadAccess, services.Snapshot));
    }

    /// <summary>A section in MCP Test through the guarded raw write, recorded as this run's.</summary>
    private async Task<string> CreateSectionAsync(Run run, string name)
    {
        var id = await TestNotebook.WriteAsync(
            run.Com,
            run.NotebookId,
            app =>
            {
                app.OpenHierarchy(name + ".one", run.NotebookId, out var section, Com.Interop.CreateFileType.cftSection);
                return section;
            },
            "OpenHierarchy(3.14 probe section)",
            Ct,
            throwaways);
        created.Add(id);
        return id;
    }

    /// <summary>A page in a section this run made, after the guard confirms the section is in MCP Test.</summary>
    private Task<string> CreatePageAsync(Run run, string sectionId) => GuardedCreatePageAsync(run.Com, sectionId, created, throwaways);

    private static async Task<string> GuardedCreatePageAsync(ComGateway com, string sectionId, List<string> created, IReadOnlyDictionary<string, IReadOnlySet<string>> throwaways)
    {
        if (!created.Contains(sectionId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The run creates pages only in sections it made.");
        }

        var id = await TestNotebook.WriteAsync(
            com,
            sectionId,
            app =>
            {
                app.CreateNewPage(sectionId, out var page, Com.Interop.NewPageStyle.npsBlankPageWithTitle);
                return page;
            },
            "CreateNewPage(3.14 probe page)",
            Ct,
            throwaways);
        created.Add(id);
        return id;
    }

    /// <summary>The first <paramref name="levels"/>.Count of <paramref name="pages"/> at those levels (the rest at 1), in that order, through the guarded raw write.</summary>
    private async Task SetLevelsRawAsync(Run run, string sectionId, IReadOnlyList<string> pages, IReadOnlyList<int> levels, string method)
    {
        var xml = new XElement(
            One + "Section",
            new XAttribute(XNamespace.Xmlns + "one", OneNoteXml.Namespace2013),
            new XAttribute("ID", sectionId),
            pages.Select((id, i) => new XElement(One + "Page", new XAttribute("ID", id), new XAttribute("pageLevel", i < levels.Count ? levels[i] : 1))));
        await TestNotebook.UpdateHierarchyAsync(run.Com, xml.ToString(SaveOptions.DisableFormatting), created.ToHashSet(StringComparer.Ordinal), throwaways, method, Ct);
    }

    private static async Task<List<string>> IdsAsync(Run run, string sectionId) =>
        [.. HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(sectionId, CoreScope.Children, Ct)).Children.Select(c => c.Id)];

    private static async Task<List<int>> LevelsAsync(Run run, string sectionId, IReadOnlyList<string> pages)
    {
        var read = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(sectionId, CoreScope.Children, Ct)).Children.ToDictionary(c => c.Id, StringComparer.Ordinal);
        return [.. pages.Select(id => read.TryGetValue(id, out var c) ? c.PageLevel : 0)];
    }

    /// <summary>The <c>pageLevel</c> on <c>GetPageContent</c>'s root (recorded, not asserted: the sitting settles it), or "absent".</summary>
    private static async Task<string> ContentLevelAsync(Run run, string pageId) =>
        (string?)XDocument.Parse(await run.Gateway.GetPageContentAsync(pageId, PageInfo.Basic, Ct)).Root!.Attribute("pageLevel") ?? "absent";

    /// <summary>The page's hierarchy <c>lastModifiedTime</c> from a fresh read of its section, as the basis (AD-17).</summary>
    private static async Task<PageBasisArgument> BasisAsync(Run run, string sectionId, string pageId) =>
        new(HierarchyTarget.Page(await run.Gateway.GetHierarchyAsync(sectionId, CoreScope.Children, Ct), pageId)!.LastModifiedTime);

    /// <summary>The result's journal entry exists and is a <c>hierarchy</c> entry of the section.</summary>
    private static void AssertEntry(Run run, JsonElement root, string sectionId)
    {
        var id = Guid.Parse(root.GetProperty("meta").GetProperty("journalEntryId").GetString()!);
        var entry = JournalStore.Read(JournalFiles.Find(run.JournalFolder, id)!);
        Assert.Equal((sectionId, JournalEntry.HierarchyKind), (entry.ObjectId, entry.SnapshotKind));
    }

    /// <summary>
    /// The probes' hitl pause (<see cref="ProbeSession.PauseAsync"/>'s file and protocol): writes the instruction to the
    /// pause file and waits until the main session deletes it (true) or writes <c>skip</c> into it (false).
    /// </summary>
    private async Task<bool> PauseAsync(string step, string instruction)
    {
        Directory.CreateDirectory(ProbeRecorder.ProbesDir);
        var file = Path.Combine(ProbeRecorder.ProbesDir, ProbeSession.PauseFileName);
        await File.WriteAllTextAsync(file, $"[set_page_level / {step}]\n{instruction}\n\nWhen done, delete this file. To skip this step, replace this file's content with: skip\n", Ct);
        output.WriteLine($"PAUSE ({file}): {instruction}");
        Console.WriteLine($"PAUSE [set_page_level / {step}]: {instruction}");

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

    /// <summary>Soft-deletes the sections this run made, last first, through the guarded delete. A failure is logged only.</summary>
    private async Task CleanUpAsync(Run run)
    {
        var ours = created.ToHashSet(StringComparer.Ordinal);
        foreach (var id in Enumerable.Reverse(created).ToList())
        {
            try
            {
                var self = XDocument.Parse(await run.Com.GetHierarchyAsync(id, CoreScope.Self, Ct)).Root!;
                if (self.Name != One + "Section" || (string?)self.Attribute("isInRecycleBin") == "true")
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
    /// (<see cref="TestNotebook.UpdateHierarchyAsync"/>) and is counted, <c>CreateNewPage</c> goes through the guarded raw
    /// write into a section this run made (and the page is recorded as the run's), and every other write is refused. Reads
    /// go straight through.
    /// </summary>
    private sealed class GuardedGateway(ComGateway inner, List<string> created, IReadOnlyDictionary<string, IReadOnlySet<string>> throwaways) : IOneNoteGateway
    {
        private int updateHierarchyCalls;

        /// <summary>How many <c>UpdateHierarchy</c> calls the tools sent (each passed the guard first).</summary>
        public int UpdateHierarchyCalls => Volatile.Read(ref updateHierarchyCalls);

        public async Task UpdateHierarchyAsync(string changesXml, CancellationToken ct)
        {
            Interlocked.Increment(ref updateHierarchyCalls);
            await TestNotebook.UpdateHierarchyAsync(inner, changesXml, created.ToHashSet(StringComparer.Ordinal), throwaways, "UpdateHierarchy(3.14 tool)", ct);
        }

        public Task<string> CreateNewPageAsync(string sectionId, NewPageStyle style, CancellationToken ct) =>
            GuardedCreatePageAsync(inner, sectionId, created, throwaways);

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

        public Task<string> CreateSectionAsync(string parentId, string sectionFileName, CancellationToken ct) => Refused<string>("OpenHierarchy");

        public Task<string> CreateSectionGroupAsync(string parentId, string groupName, CancellationToken ct) => Refused<string>("OpenHierarchy");

        public Task<string> CreateNotebookAsync(string folderPath, CancellationToken ct) => Refused<string>("OpenHierarchy");

        public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct) => Refused("UpdatePageContent");

        public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct) => Refused("DeletePageContent");

        public Task DeleteHierarchyAsync(string objectId, string expectedLastModified, bool permanent, CancellationToken ct) => Refused("DeleteHierarchy");

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => inner.WaitForAbandonedWritesAsync(ct);

        private static Task Refused(string method) =>
            Task.FromException(new InvalidOperationException($"Story 3.14's tools never call {method}."));

        private static Task<T> Refused<T>(string method) =>
            Task.FromException<T>(new InvalidOperationException($"Story 3.14's tools never call {method}."));
    }
}
