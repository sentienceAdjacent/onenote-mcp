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
using InteropSchema = OneNoteMcp.Com.Interop.XMLSchema;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 3.5's live checks for the sitting (S1 in the plan), opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c> and never run in
/// a build: <c>dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~RestoreFromRecycleBinIntegrationTests</c>,
/// with MCP Test open.
/// <list type="bullet">
/// <item>Every item is one this run makes in MCP Test (stamped <c>Restore 3.5</c> names), soft-deleted with
/// <c>delete_hierarchy_object</c> after the guard checks it (<see cref="TestNotebookGuard.CheckDelete"/>), then restored
/// with <c>restore_from_recycle_bin</c> over a gateway whose <c>UpdateHierarchy</c> is story 5.1's guarded one
/// (<see cref="TestNotebook.UpdateHierarchyAsync"/>): every ID it sends must be this run's (the recycle-bin IDs and new IDs
/// included) inside MCP Test. So a section is restored into a run section group, never to the notebook's root, whose ID the
/// guard refuses. At the end the run's sections and groups are soft-deleted; a failed clean-up is logged only.</item>
/// <item>The protected check (S1.5) uses a hand-made throwaway: a section group named <see cref="HandVaultGroupName"/> in MCP
/// Test holding one password-protected section named <see cref="HandVaultName"/> with throwaway text only, which Phill
/// unlocks before the run. The test protects nothing itself; it is skipped when they are absent, unprotected or locked.</item>
/// <item>Grants and the recovery journal live in a temporary folder, never the user's settings or journal.</item>
/// </list>
/// The output carries IDs, codes and the run's own stamped names only.
/// </summary>
public sealed class RestoreFromRecycleBinIntegrationTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>A section group Phill makes by hand in MCP Test for S1.5, holding <see cref="HandVaultName"/>.</summary>
    public const string HandVaultGroupName = "Restore 3.5 Vault group";

    /// <summary>A password-protected section Phill makes by hand inside <see cref="HandVaultGroupName"/>, unlocked before the run.</summary>
    public const string HandVaultName = "Restore 3.5 Vault";

    /// <summary>The local throwaway S4.1 adopts by its exact name (spike 3.1's, as the move tests use it).</summary>
    private const string ThrowawayName = "MCP Test Throwaway 1112-034716 a";

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    /// <summary>Every ID this run made or verified (new and recycle-bin IDs too): the guards accept only these.</summary>
    private readonly HashSet<string> created = new(StringComparer.Ordinal);

    /// <summary>The sections and groups this run made, in order, soft-deleted at the end.</summary>
    private readonly List<string> containers = [];

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
    /// S1.1: a soft-deleted page comes back by its recycled ID under a new ID, last in its section, with one journal entry,
    /// and is gone from Deleted Pages; a second one restored at position 1 lands first.
    /// </summary>
    [Fact]
    public async Task S1_APage_ComesBackLast_UnderANewId_ThenASecondAtPosition1_LandsFirst()
    {
        await using var run = await StartAsync();
        try
        {
            var section = await CreateSectionAsync(run, run.NotebookId, $"Restore 3.5 pages {Stamp}");
            var keep = await CreatePageAsync(run, section);
            var first = await CreatePageAsync(run, section);
            var second = await CreatePageAsync(run, section);
            var firstInBin = await SoftDeletePageAsync(run, first);
            var secondInBin = await SoftDeletePageAsync(run, second);
            var entries = EntryCount();

            var root = Ok(await Tool(run).RestoreFromRecycleBin(firstInBin, null, null, null, Ct));
            var firstBack = NewId(root, firstInBin);
            Assert.Equal(section, root.GetProperty("meta").GetProperty("parentId").GetString());
            Assert.Equal([keep, firstBack], await PageIdsAsync(run, section));
            Assert.Equal(entries + 1, EntryCount());
            Assert.Equal(section, Entry(run, root).ObjectId);
            Assert.DoesNotContain(await DeletedPagesAsync(run), p => p.Id == firstInBin);
            output.WriteLine($"RESULT S1.1 page {firstInBin} -> {firstBack}, last in {section}; warnings {root.GetProperty("warnings").GetRawText()}");

            var atOne = Ok(await Tool(run).RestoreFromRecycleBin(secondInBin, null, null, 1, Ct));
            var secondBack = NewId(atOne, secondInBin);
            Assert.Equal([secondBack, keep, firstBack], await PageIdsAsync(run, section));
            output.WriteLine($"RESULT S1.1 page {secondInBin} -> {secondBack}, first in {section}; warnings {atOne.GetProperty("warnings").GetRawText()}");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>S1.2: a soft-deleted section comes back under its parent (a run section group) with its own ID.</summary>
    [Fact]
    public async Task S1_ASection_ComesBackUnderItsParent_WithItsId()
    {
        await using var run = await StartAsync();
        try
        {
            var stamp = Stamp;
            var group = await CreateGroupAsync(run, run.NotebookId, $"Restore 3-5 group {stamp}");
            var stays = await CreateSectionAsync(run, group, $"Restore 3.5 stays {stamp}");
            var section = await CreateSectionAsync(run, group, $"Restore 3.5 section {stamp}");
            await CreatePageAsync(run, section);
            await SoftDeleteAsync(run, section);
            Assert.True(HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(section, CoreScope.Self, Ct)).InRecycleBin);

            var root = Ok(await Tool(run).RestoreFromRecycleBin(section, null, null, null, Ct));

            Assert.Equal(section, root.GetProperty("meta").GetProperty("objectId").GetString());
            Assert.Equal(group, root.GetProperty("meta").GetProperty("parentId").GetString());
            var children = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(group, CoreScope.Children, Ct)).Children.Select(c => c.Id).ToList();
            Assert.Equal([stays, section], children);
            Assert.False(HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(section, CoreScope.Self, Ct)).InRecycleBin);
            output.WriteLine($"RESULT S1.2 section {section} back in {group}; warnings {root.GetProperty("warnings").GetRawText()}");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>
    /// S1.3: two same-named pages deleted from different sections, with both entries' <c>recycleBinId</c> removed: a restore
    /// without <c>entryId</c> lists both candidates and sends nothing; each <c>entryId</c> puts its page back in its own section.
    /// </summary>
    [Fact]
    public async Task S1_TwoSameNamedPages_GiveCandidates_ThenEachEntryIdPutsItsPageBack()
    {
        await using var run = await StartAsync();
        try
        {
            var stamp = Stamp;
            var left = await CreateSectionAsync(run, run.NotebookId, $"Restore 3.5 left {stamp}");
            var right = await CreateSectionAsync(run, run.NotebookId, $"Restore 3.5 right {stamp}");
            var a = await CreatePageAsync(run, left);
            var b = await CreatePageAsync(run, right);
            var title = $"Restore 3.5 twin {stamp}";
            await SetTitleAsync(run, a, title);
            await SetTitleAsync(run, b, title);
            var (aInBin, aEntry) = await SoftDeletePageWithEntryAsync(run, a);
            var (bInBin, bEntry) = await SoftDeletePageWithEntryAsync(run, b);
            ForgetRecycleBinId(aEntry);
            ForgetRecycleBinId(bEntry);

            var calls = run.Gateway.UpdateHierarchyCalls;
            var refused = await Tool(run).RestoreFromRecycleBin(aInBin, null, null, null, Ct);
            Assert.Equal("InvalidArgument", Code(refused));
            var message = refused.StructuredContent!.Value.GetProperty("error").GetProperty("message").GetString()!;
            Assert.Contains(aEntry.ToString("D"), message, StringComparison.Ordinal);
            Assert.Contains(bEntry.ToString("D"), message, StringComparison.Ordinal);
            Assert.DoesNotContain(title, message, StringComparison.Ordinal);
            Assert.Equal(calls, run.Gateway.UpdateHierarchyCalls);
            output.WriteLine("RESULT S1.3 two candidates: InvalidArgument, nothing sent");

            var aBack = Ok(await Tool(run).RestoreFromRecycleBin(aInBin, null, aEntry.ToString("D"), null, Ct));
            Assert.Equal(left, aBack.GetProperty("meta").GetProperty("parentId").GetString());
            Assert.Contains(NewId(aBack, aInBin), await PageIdsAsync(run, left));
            var bBack = Ok(await Tool(run).RestoreFromRecycleBin(bInBin, null, bEntry.ToString("D"), null, Ct));
            Assert.Equal(right, bBack.GetProperty("meta").GetProperty("parentId").GetString());
            Assert.Contains(NewId(bBack, bInBin), await PageIdsAsync(run, right));
            output.WriteLine($"RESULT S1.3 entryIds: {aInBin} -> {left}, {bInBin} -> {right}");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>S1.4: a page restored with <c>parentId</c> lands in that other section.</summary>
    [Fact]
    public async Task S1_APage_WithParentId_LandsInAnotherSection()
    {
        await using var run = await StartAsync();
        try
        {
            var stamp = Stamp;
            var home = await CreateSectionAsync(run, run.NotebookId, $"Restore 3.5 home {stamp}");
            var away = await CreateSectionAsync(run, run.NotebookId, $"Restore 3.5 away {stamp}");
            var page = await CreatePageAsync(run, home);
            var inBin = await SoftDeletePageAsync(run, page);

            var root = Ok(await Tool(run).RestoreFromRecycleBin(inBin, away, null, null, Ct));

            Assert.Equal(away, root.GetProperty("meta").GetProperty("parentId").GetString());
            Assert.Equal([NewId(root, inBin)], await PageIdsAsync(run, away));
            Assert.Empty(await PageIdsAsync(run, home));
            output.WriteLine($"RESULT S1.4 page {inBin} -> {away}");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>
    /// S1.5: the hand-made protected section, unlocked, soft-deleted with the tool (it is locked in the bin), restores to its
    /// own place still encrypted and locked, with its ID. A restore with a <c>parentId</c> elsewhere is
    /// <c>InvalidArgument</c>, and nothing is sent.
    /// </summary>
    [Fact]
    public async Task S1_AProtectedSection_GoesBackToItsOwnPlace_StillEncryptedAndLocked_NeverElsewhere()
    {
        await using var run = await StartAsync();
        var tree = XDocument.Parse(await run.Gateway.GetHierarchyAsync(run.NotebookId, CoreScope.Sections, Ct)).Root!;
        var group = tree.Elements(One + "SectionGroup").FirstOrDefault(g => (string?)g.Attribute("name") == HandVaultGroupName);
        Assert.SkipWhen(group is null, $"Make a section group \"{HandVaultGroupName}\" in MCP Test holding a password-protected section \"{HandVaultName}\" first.");
        var vault = group!.Elements(One + "Section").FirstOrDefault(s => (string?)s.Attribute("name") == HandVaultName);
        Assert.SkipWhen(vault is null, $"Make a password-protected section \"{HandVaultName}\" inside \"{HandVaultGroupName}\" first.");
        Assert.SkipUnless((string?)vault!.Attribute("encrypted") is "true" or "1", $"\"{HandVaultName}\" isn't password-protected.");
        Assert.SkipWhen((string?)vault.Attribute("locked") is "true" or "1", $"Unlock \"{HandVaultName}\" in OneNote first, so it can be soft-deleted.");
        var groupId = (string)group.Attribute("ID")!;
        var vaultId = (string)vault.Attribute("ID")!;
        created.Add(groupId);
        created.Add(vaultId);
        try
        {
            var elsewhere = await CreateGroupAsync(run, run.NotebookId, $"Restore 3-5 elsewhere {Stamp}");
            await SoftDeleteAsync(run, vaultId);
            var binned = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(vaultId, CoreScope.Self, Ct));
            output.WriteLine($"vault {vaultId} in the bin: inRecycleBin={binned.InRecycleBin}, encrypted={binned.Encrypted}, locked={binned.Locked}");

            var calls = run.Gateway.UpdateHierarchyCalls;
            var refused = await Tool(run).RestoreFromRecycleBin(vaultId, elsewhere, null, null, Ct);
            Assert.Equal("InvalidArgument", Code(refused));
            Assert.Equal(RestoreFromRecycleBinWrite.ProtectedElsewhereUserAction, refused.StructuredContent!.Value.GetProperty("error").GetProperty("userAction").GetString());
            Assert.Equal(calls, run.Gateway.UpdateHierarchyCalls);

            var root = Ok(await Tool(run).RestoreFromRecycleBin(vaultId, null, null, null, Ct));

            Assert.Equal(vaultId, root.GetProperty("meta").GetProperty("objectId").GetString());
            Assert.Equal(groupId, root.GetProperty("meta").GetProperty("parentId").GetString());
            var back = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(vaultId, CoreScope.Self, Ct));
            Assert.False(back.InRecycleBin);
            Assert.True(back.Encrypted);
            Assert.True(back.Locked);
            Assert.DoesNotContain(HandVaultName, root.GetRawText(), StringComparison.Ordinal);
            output.WriteLine($"RESULT S1.5 vault {vaultId} back in {groupId}, encrypted and locked; elsewhere refused; warnings {root.GetProperty("warnings").GetRawText()}");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>
    /// S4.1 (Q7): a run section soft-deleted in MCP Test, restored with <c>parentId</c> a run section group in the local
    /// throwaway (skipped when it isn't open): restored in place first, then moved there with its ID, with the restore's
    /// journal entry and the move's.
    /// </summary>
    [Fact]
    public async Task S4_ASection_IntoTheThrowaway_IsRestoredInPlace_ThenMovedThere()
    {
        await using var run = await StartAsync();
        var throwaway = await TestNotebook.AdoptThrowawayNotebookAsync(run.Com, ThrowawayName, Ct);
        Assert.SkipWhen(throwaway is null, $"Open the throwaway notebook \"{ThrowawayName}\" in OneNote first.");
        throwaways[throwaway!] = new HashSet<string>([ThrowawayName]);
        var throwawayKey = GrantKeys.NotebookKey(HierarchyNode.Root(await run.Com.GetHierarchyAsync(throwaway!, CoreScope.Self, Ct)).Path);
        new SettingsWriter(run.Store, TimeProvider.System).SaveWriteGrant(
            new NotebookGrant(throwawayKey, ThrowawayName, throwaway!, Write: true, PermanentDelete: false, DateTimeOffset.UtcNow, GrantedVia.Cli));
        try
        {
            var stamp = Stamp;
            var home = await CreateGroupAsync(run, run.NotebookId, $"Restore 3-5 home {stamp}");
            var section = await CreateSectionAsync(run, home, $"Restore 3.5 travels {stamp}");
            await CreatePageAsync(run, section);
            var landing = await CreateGroupAsync(run, throwaway!, $"Restore 3-5 landing {stamp}");
            await SoftDeleteAsync(run, section);

            var root = Ok(await Tool(run).RestoreFromRecycleBin(section, landing, null, null, Ct));

            var meta = root.GetProperty("meta");
            Assert.Equal(section, meta.GetProperty("objectId").GetString());
            Assert.Equal(landing, meta.GetProperty("parentId").GetString());
            var ids = meta.GetProperty("journalEntryIds").EnumerateArray().Select(e => Guid.Parse(e.GetString()!)).ToList();
            Assert.Equal(Guid.Parse(meta.GetProperty("journalEntryId").GetString()!), ids[0]);
            Assert.Equal([home, home, landing], ids.Select(id => JournalStore.Read(JournalFiles.Find(run.JournalFolder, id)!).ObjectId));
            Assert.Contains(section, HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(landing, CoreScope.Children, Ct)).Children.Select(c => c.Id));
            Assert.DoesNotContain(section, HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(home, CoreScope.Children, Ct)).Children.Select(c => c.Id));
            Assert.Equal(2, run.Gateway.UpdateHierarchyCalls);
            output.WriteLine($"RESULT S4.1 section {section}: restored in {home}, moved to {landing}; warnings {root.GetProperty("warnings").GetRawText()}");
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
    private static RestoreFromRecycleBinTool Tool(Run run)
    {
        var services = new HierarchyWriteServices(
            run.Gateway,
            new WriteAccess(run.Gateway, run.Store.Read),
            new ReadAccess(run.Gateway, run.Store.Read, new SettingsWriter(run.Store, TimeProvider.System), TimeProvider.System),
            new HierarchySnapshot(run.Gateway, new JournalStore(run.Store.Read)));
        return new RestoreFromRecycleBinTool(
            new ToolRunner(NullLogger<ToolRunner>.Instance),
            new RestoreFromRecycleBinUseCase(services, new WritePipeline(new WriteLock(), run.Gateway, PageTextCache.WithDefaults(), run.Store.Read), run.Store.Read));
    }

    /// <summary><c>delete_hierarchy_object</c> over the real gateway, after the guard checks the target is this run's and in MCP Test.</summary>
    private async Task<JsonElement> SoftDeleteAsync(Run run, string id)
    {
        var notebooks = await run.Com.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct);
        TestNotebookGuard.CheckDelete(DeleteHierarchyObjectTool.Name, id, TestNotebookGuard.TestNotebookIds(notebooks, TestNotebook.ResolveFolder(Environment.GetEnvironmentVariable)), created);
        await TestNotebook.EnsureTestTargetAsync(run.Com, id, DeleteHierarchyObjectTool.Name, Ct);
        var services = new PageWriteServices(
            run.Com,
            new WriteAccess(run.Com, run.Store.Read),
            new ReadAccess(run.Com, run.Store.Read, new SettingsWriter(run.Store, TimeProvider.System), TimeProvider.System),
            new PageSnapshot(run.Com, new JournalStore(run.Store.Read), TimeProvider.System));
        var useCase = new SoftDeleteUseCase(
            services,
            new HierarchySnapshot(run.Com, new JournalStore(run.Store.Read), TimeProvider.System),
            new WritePipeline(new WriteLock(), run.Com, PageTextCache.WithDefaults(), run.Store.Read));
        var root = Ok(await new DeleteHierarchyObjectTool(new ToolRunner(NullLogger<ToolRunner>.Instance), useCase).DeleteHierarchyObject(id, null, Ct));
        output.WriteLine($"soft-deleted {id}");
        return root;
    }

    /// <summary>A page's soft delete; returns its recycle-bin ID (recorded as this run's).</summary>
    private async Task<string> SoftDeletePageAsync(Run run, string pageId) => (await SoftDeletePageWithEntryAsync(run, pageId)).InBin;

    private async Task<(string InBin, Guid Entry)> SoftDeletePageWithEntryAsync(Run run, string pageId)
    {
        var meta = (await SoftDeleteAsync(run, pageId)).GetProperty("meta");
        Assert.True(meta.TryGetProperty("recycleBinId", out var id), "The soft delete didn't read the page's recycle-bin ID back.");
        var inBin = id.GetString()!;
        created.Add(inBin);
        return (inBin, Guid.Parse(meta.GetProperty("journalEntryId").GetString()!));
    }

    /// <summary>Rewrites the entry without its <c>recycleBinId</c>, as an entry whose read-back failed looks (AD-8), so the restore must match by name.</summary>
    private void ForgetRecycleBinId(Guid entryId)
    {
        var path = JournalFiles.Find(Path.Combine(tempDir, "journal"), entryId) ?? throw new InvalidOperationException($"No entry {entryId:D}.");
        var entry = JournalStore.Read(path);
        File.Delete(path);
        new JournalStore(() => throw new InvalidOperationException("WriteTo reads no settings.")).WriteTo(Path.GetDirectoryName(path)!, entry with { RecycleBinId = null });
    }

    /// <summary>A section under <paramref name="parentId"/> through the guarded raw write, recorded as this run's.</summary>
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
            "OpenHierarchy(3.5 probe section)",
            Ct,
            throwaways);
        created.Add(id);
        containers.Add(id);
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
            "OpenHierarchy(3.5 probe section group)",
            Ct,
            throwaways);
        created.Add(id);
        containers.Add(id);
        return id;
    }

    /// <summary>A page in a section this run made, after the guard confirms the section is in a test notebook.</summary>
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
            "CreateNewPage(3.5 probe page)",
            Ct,
            throwaways);
        created.Add(id);
        return id;
    }

    /// <summary>Sets a run page's title through the guarded raw <c>UpdatePageContent</c>, so two pages can share a name.</summary>
    private async Task SetTitleAsync(Run run, string pageId, string title)
    {
        var date = PageBasis.From(await run.Com.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct)).LastModifiedTime
            ?? throw new InvalidOperationException("The page has no lastModifiedTime.");
        var xml = new XElement(
            One + "Page",
            new XAttribute(XNamespace.Xmlns + "one", OneNoteXml.Namespace2013),
            new XAttribute("ID", pageId),
            new XElement(One + "Title", new XElement(One + "OE", new XElement(One + "T", new XCData(title))))).ToString(SaveOptions.DisableFormatting);
        var utc = OneNoteDates.ToUtc(date) ?? throw new InvalidOperationException("The page's lastModifiedTime isn't a date.");
        await TestNotebook.WriteAsync(
            run.Com,
            pageId,
            app =>
            {
                app.UpdatePageContent(xml, utc, InteropSchema.xs2013, force: false);
                return true;
            },
            "UpdatePageContent(3.5 twin title)",
            Ct,
            throwaways);
    }

    // --- Reads ------------------------------------------------------------------------------------------------------

    private static async Task<IReadOnlyList<string>> PageIdsAsync(Run run, string sectionId) =>
        [.. HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(sectionId, CoreScope.Children, Ct)).Children.Select(c => c.Id)];

    private static async Task<IReadOnlyList<DeletedPage>> DeletedPagesAsync(Run run)
    {
        var children = await run.Gateway.GetHierarchyAsync(run.NotebookId, CoreScope.Children, Ct);
        return RecycleBin.BinId(children) is { Length: > 0 } bin ? RecycleBin.DeletedPages(await run.Gateway.GetHierarchyAsync(bin, CoreScope.Pages, Ct)) : [];
    }

    private int EntryCount() =>
        Directory.Exists(Path.Combine(tempDir, "journal"))
            ? Directory.GetFiles(Path.Combine(tempDir, "journal")).Count(f => JournalFiles.IsEntry(Path.GetFileName(f)))
            : 0;

    private static JournalEntry Entry(Run run, JsonElement root) =>
        JournalStore.Read(JournalFiles.Find(run.JournalFolder, Guid.Parse(root.GetProperty("meta").GetProperty("journalEntryId").GetString()!))!);

    /// <summary>The restored page's new ID from <c>meta.objectId</c>, recorded as this run's; it must differ from the recycled one.</summary>
    private string NewId(JsonElement root, string oldId)
    {
        var id = root.GetProperty("meta").GetProperty("objectId").GetString()!;
        Assert.NotEqual(oldId, id);
        created.Add(id);
        return id;
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
    /// Soft-deletes the sections and section groups this run made, last first, through the guarded delete; a failure is
    /// logged only. The hand-made Vault and its group are never deleted.
    /// </summary>
    private async Task CleanUpAsync(Run run)
    {
        foreach (var id in Enumerable.Reverse(containers).ToList())
        {
            try
            {
                var self = XDocument.Parse(await run.Com.GetHierarchyAsync(id, CoreScope.Self, Ct)).Root!;
                if ((string?)self.Attribute("isInRecycleBin") == "true")
                {
                    continue;
                }

                var date = OneNoteDates.ToUtc((string?)self.Attribute("lastModifiedTime")) ?? DateTime.MinValue;
                await TestNotebook.DeleteHierarchyAsync(run.Com, id, date, permanently: false, created, throwaways, Ct);
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
    private sealed class GuardedGateway(ComGateway inner, HashSet<string> created, IReadOnlyDictionary<string, IReadOnlySet<string>> throwaways) : IOneNoteGateway
    {
        private int updateHierarchyCalls;

        /// <summary>How many <c>UpdateHierarchy</c> calls the tool sent (each passed the guard first).</summary>
        public int UpdateHierarchyCalls => Volatile.Read(ref updateHierarchyCalls);

        public async Task UpdateHierarchyAsync(string changesXml, CancellationToken ct)
        {
            Interlocked.Increment(ref updateHierarchyCalls);
            await TestNotebook.UpdateHierarchyAsync(inner, changesXml, created.ToHashSet(StringComparer.Ordinal), throwaways, "UpdateHierarchy(restore tool)", ct);
        }

        public Task<string> GetHierarchyAsync(string startNodeId, CoreScope scope, CancellationToken ct) => inner.GetHierarchyAsync(startNodeId, scope, ct);

        public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) => inner.GetHierarchyParentAsync(objectId, ct);

        public Task<string> GetPageContentAsync(string pageId, CorePageInfo pageInfo, CancellationToken ct) => inner.GetPageContentAsync(pageId, pageInfo, ct);

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
            Task.FromException(new InvalidOperationException($"The restore tool never calls {method}."));

        private static Task<T> Refused<T>(string method) =>
            Task.FromException<T>(new InvalidOperationException($"The restore tool never calls {method}."));
    }
}
