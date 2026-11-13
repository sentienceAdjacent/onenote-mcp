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
using OneNoteMcp.Server.Cli;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 5.6's live probes for the sitting (P1–P11 in the plan), opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c> and never run
/// in a build: <c>dotnet test tests/OneNoteMcp.Integration --filter PermanentDelete</c>. Each probe makes its own
/// throwaway sections, groups and pages in MCP Test (cloud ones in MCP Test Cloud, synced after each step), under stamped
/// names, and deletes only what it made: <see cref="GuardedGateway"/> refuses a <c>DeleteHierarchy</c> or <c>Publish</c> of
/// anything this run didn't create, and anything outside the test notebooks. Grants and the journal go to a temporary
/// folder, never the user's. "Gateway" probes call COM directly, because the tool refuses or pre-empts that case. The
/// output carries IDs, codes and the stamped names only, for Phill's notes.
/// </summary>
public sealed class PermanentDeleteIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    /// <summary>What this run made; only these may be deleted or published.</summary>
    private readonly HashSet<string> created = new(StringComparer.Ordinal);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Stamp => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private string Journal => Path.Combine(tempDir, "journal");

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

    // --- P1 (Deferred row, gateway): a stale date on a permanent delete -----------------------------------------------

    [Fact]
    public async Task P1_PermanentDelete_WithAStaleDate_GatewayThenTool()
    {
        await using var run = await StartAsync();
        var section = await NewSectionAsync(run, "P1");

        // A page that isn't displayed: its hierarchy date, then an edit, then DeleteHierarchy with the old date.
        var page = await NewPageAsync(run, section);
        var oldPageDate = await HierarchyDateAsync(run, page);
        await RetitleAsync(run, page, "P1 edited " + Stamp);
        output.WriteLine($"P1 page {page}: old hierarchy date {oldPageDate}, now {await HierarchyDateAsync(run, page)}");
        output.WriteLine($"RESULT P1-gateway-page: {await RawDeleteAsync(run, page, oldPageDate)}");

        // A section: its date, then a page added, then DeleteHierarchy with the old date.
        var other = await NewSectionAsync(run, "P1 section");
        var oldSectionDate = await HierarchyDateAsync(run, other);
        await NewPageAsync(run, other);
        output.WriteLine($"RESULT P1-gateway-section: {await RawDeleteAsync(run, other, oldSectionDate)}");

        // Through the tool: the server's own comparison refuses both, and nothing is deleted.
        var page2 = await NewPageAsync(run, section);
        var basis = await PageBasisAsync(run, page2);
        await RetitleAsync(run, page2, "P1 edited again " + Stamp);
        Assert.Equal("StaleWrite", Code(await Tool(run).PermanentlyDeleteHierarchyObject(page2, basis, Ct)));
        Assert.True(await ExistsAsync(run, page2));

        var section2 = await NewSectionAsync(run, "P1 tool");
        var sectionBasis = new PageBasisArgument(await HierarchyDateAsync(run, section2));
        await NewPageAsync(run, section2);
        await Task.Delay(1500, Ct); // a parent's date has 1 s resolution (AD-17)
        output.WriteLine($"RESULT P1-tool-section: {Code(await Tool(run).PermanentlyDeleteHierarchyObject(section2, sectionBasis, Ct)) ?? "ok (date didn't move)"}");
    }

    // --- P2 (Deferred row): an empty section group ---------------------------------------------------------------------

    [Fact]
    public async Task P2_EmptySectionGroup_IsDeleted_WithAHierarchyEntry()
    {
        await using var run = await StartAsync();
        var group = await NewGroupAsync(run, "P2");

        var root = Ok(await Tool(run).PermanentlyDeleteHierarchyObject(group, new PageBasisArgument(await HierarchyDateAsync(run, group)), Ct));

        Assert.False(await ExistsAsync(run, group));
        var entry = JournalStore.Read(JournalFiles.Find(Journal, Guid.Parse(EntryId(root)))!);
        Assert.Equal(JournalEntry.HierarchyKind, entry.SnapshotKind);
        output.WriteLine($"RESULT P2: group {group} gone; entry {entry.EntryId:D}. Phill: check the notebook folder and OneDrive for a leftover folder.");
    }

    // --- P3 (verify): no permanent-delete grant, then with it ---------------------------------------------------------

    [Fact]
    public async Task P3_WithoutThePermanentDeleteGrant_IsRefused_ThenWithItProceeds()
    {
        await using var run = await StartAsync(permanentDelete: false);
        var section = await NewSectionAsync(run, "P3");
        var page = await NewPageAsync(run, section);

        // No dialog can be shown here (no MCP client): PermanentDeleteNotGranted, nothing deleted, no entry.
        Assert.Equal("PermanentDeleteNotGranted", Code(await Tool(run).PermanentlyDeleteHierarchyObject(page, null, Ct)));
        Assert.True(await ExistsAsync(run, page));
        Assert.False(Directory.Exists(Journal) && Directory.EnumerateFiles(Journal).Any());

        new SettingsWriter(run.Store, TimeProvider.System).SavePermanentDeleteGrant(run.NotebookKey);
        Ok(await Tool(run).PermanentlyDeleteHierarchyObject(page, null, Ct));
        Assert.False(await ExistsAsync(run, page));
        output.WriteLine($"RESULT P3: refused without the grant, deleted {page} with it");
    }

    // --- P4 (verify): a section with two pages --------------------------------------------------------------------------

    [Fact]
    public async Task P4_Section_IsBackedUpFirst_Deleted_AndAPageEntryRestores()
    {
        await using var run = await StartAsync();
        var section = await NewSectionAsync(run, "P4");
        await NewPageAsync(run, section);
        await NewPageAsync(run, section);
        long? sizeAtDelete = null;
        run.Gateway.BeforeDelete = _ => sizeAtDelete = Directory.GetFiles(Path.Combine(Journal, JournalFiles.BackupsFolderName)).Select(f => new FileInfo(f).Length).Single();

        var root = Ok(await Tool(run).PermanentlyDeleteHierarchyObject(section, new PageBasisArgument(await HierarchyDateAsync(run, section)), Ct));

        Assert.True(sizeAtDelete > 0, "the backup file must exist and not be empty when DeleteHierarchy is called");
        Assert.False(await ExistsAsync(run, section));
        var meta = root.GetProperty("meta");
        var entry = JournalStore.Read(JournalFiles.Find(Journal, Guid.Parse(EntryId(root)))!);
        Assert.Equal(meta.GetProperty("backupFile").GetString(), entry.BackupFile);
        var pageEntries = meta.GetProperty("pageEntryIds").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(2, pageEntries.Count);

        var restore = new RestoreFromJournalUseCase(run.Gateway, run.WriteAccess, run.Pipeline(), run.Store.Read);
        var restored = Ok(await new ToolRunner(NullLogger<ToolRunner>.Instance).RunAsync(
            RestoreFromJournalTool.Name, restore, (context, token) => restore.ExecuteAsync(context, pageEntries[0], token), Ct));
        var newPage = restored.GetProperty("meta").GetProperty("pageId").GetString()!;
        created.Add(newPage);
        created.Add(restored.GetProperty("meta").GetProperty("parentId").GetString()!);
        output.WriteLine($"RESULT P4: section {section} gone, backup {entry.BackupFile} ({sizeAtDelete} bytes at the delete), page restored as {newPage}");
    }

    // --- P5 (verify): a forced backup failure ---------------------------------------------------------------------------

    [Fact]
    public async Task P5_BackupFailure_FromAGatewayThatFailsOrSkipsPublish_DeletesNothing()
    {
        await using var run = await StartAsync();
        foreach (var mode in new[] { PublishMode.Fail, PublishMode.Skip })
        {
            var section = await NewSectionAsync(run, "P5 " + mode);
            await NewPageAsync(run, section);
            run.Gateway.Publish = mode;
            if (mode == PublishMode.Skip)
            {
                // No file ever lands: wait 2 s for it, not the default 300.
                var settings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(run.Store.Path))!.AsObject();
                settings["timeouts"] = new System.Text.Json.Nodes.JsonObject { ["publishSeconds"] = 2 };
                File.WriteAllText(run.Store.Path, settings.ToJsonString());
            }

            Assert.Equal("BackupFailed", Code(await Tool(run).PermanentlyDeleteHierarchyObject(section, null, Ct)));
            Assert.True(await ExistsAsync(run, section));
            output.WriteLine($"RESULT P5-{mode}: BackupFailed, section {section} still there");
            run.Gateway.Publish = PublishMode.Real;
        }
    }

    /// <summary>P5 with a real dialog: Phill opens File > Options in OneNote first, then runs this test alone.</summary>
    [Fact]
    public async Task P5_BackupFailure_UnderAnOpenOptionsDialog()
    {
        await using var run = await StartAsync();
        var section = await NewSectionAsync(run, "P5 dialog");
        await NewPageAsync(run, section);

        var result = await Tool(run).PermanentlyDeleteHierarchyObject(section, null, Ct);

        // Spike 5.1 saw Publish fail with 0x80042006 under that dialog; if it succeeds, the gateway variant above stands in.
        output.WriteLine($"RESULT P5-dialog: {Code(result) ?? "ok: Publish succeeded under the dialog"}; hresult {Hresult(result) ?? "none"}; section still there: {await ExistsAsync(run, section)}");
    }

    // --- P6: a live page --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task P6_LivePage_IsGone_NotInDeletedPages_AndRestores()
    {
        await using var run = await StartAsync();
        var section = await NewSectionAsync(run, "P6");
        var page = await NewPageAsync(run, section);
        var basis = await PageBasisAsync(run, page);

        var root = Ok(await Tool(run).PermanentlyDeleteHierarchyObject(page, basis, Ct));

        Assert.False(await ExistsAsync(run, page));
        var restore = new RestoreFromJournalUseCase(run.Gateway, run.WriteAccess, run.Pipeline(), run.Store.Read);
        var restored = Ok(await new ToolRunner(NullLogger<ToolRunner>.Instance).RunAsync(
            RestoreFromJournalTool.Name, restore, (context, token) => restore.ExecuteAsync(context, EntryId(root), token), Ct));
        created.Add(restored.GetProperty("meta").GetProperty("pageId").GetString()!);
        output.WriteLine($"RESULT P6: page {page} gone (Phill: check Deleted Pages is empty of it); restored as {restored.GetProperty("meta").GetProperty("pageId").GetString()}");
    }

    // --- P7: a protected section's page, and the section itself ------------------------------------------------------

    [Fact]
    public async Task P7_ProtectedSection_APageGoesWithADateOnlyBasis_TheSectionIsRefused()
    {
        await using var run = await StartAsync();
        var tree = XDocument.Parse(await run.Gateway.GetHierarchyAsync(run.NotebookId, CoreScope.Sections, Ct));
        var vault = tree.Root!.Descendants(One + "Section")
            .FirstOrDefault(s => (string?)s.Attribute("encrypted") is "true" or "1" && (string?)s.Attribute("isInRecycleBin") != "true" && (string?)s.Attribute("locked") != "true");
        Assert.SkipWhen(vault is null, "MCP Test has no unlocked password-protected section: Phill unlocks the Ballast Vault first.");
        var vaultId = (string)vault!.Attribute("ID")!;
        var backupsBefore = Backups();

        Assert.Equal("NotSupported", Code(await Tool(run).PermanentlyDeleteHierarchyObject(vaultId, null, Ct)));
        Assert.Equal(backupsBefore, Backups());

        var page = await NewPageAsync(run, vaultId);
        var dateOnly = new PageBasisArgument((await PageBasisAsync(run, page)).LastModifiedTime);
        Assert.Equal("ReadNotGranted", Code(await Tool(run).PermanentlyDeleteHierarchyObject(page, new PageBasisArgument(dateOnly.LastModifiedTime, "0"), Ct)));
        var root = Ok(await Tool(run).PermanentlyDeleteHierarchyObject(page, dateOnly, Ct));
        var entry = JournalStore.Read(JournalFiles.Find(Journal, Guid.Parse(EntryId(root)))!);
        Assert.NotNull(entry.SectionKey);
        output.WriteLine($"RESULT P7: vault refused, no new backup; page {page} gone with a DPAPI entry");
    }

    // --- P8: a recycled section ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task P8_RecycledSection_Publishes_ThenLeavesTheRecycleBin()
    {
        await using var run = await StartAsync();
        var section = await NewSectionAsync(run, "P8");
        await NewPageAsync(run, section);
        await TestNotebook.DeleteHierarchyAsync(run.Gateway.Inner, section, OneNoteDates.ToUtc(await HierarchyDateAsync(run, section))!.Value, permanently: false, created, new Dictionary<string, IReadOnlySet<string>>(), Ct);

        var root = Ok(await Tool(run).PermanentlyDeleteHierarchyObject(section, new PageBasisArgument(await HierarchyDateAsync(run, section)), Ct));

        Assert.True(File.Exists(Path.Combine(Journal, JournalFiles.BackupsFolderName, root.GetProperty("meta").GetProperty("backupFile").GetString()!)));
        Assert.False(await ExistsAsync(run, section));
        output.WriteLine($"RESULT P8: recycled section {section} published and gone from OneNote_RecycleBin");
    }

    // --- P9: an empty section -------------------------------------------------------------------------------------------

    [Fact]
    public async Task P9_EmptySection_DoesPublishSucceed()
    {
        await using var run = await StartAsync();
        var section = await NewSectionAsync(run, "P9");

        var result = await Tool(run).PermanentlyDeleteHierarchyObject(section, null, Ct);

        output.WriteLine($"RESULT P9: {Code(result) ?? "ok: an empty section published and was deleted"}; hresult {Hresult(result) ?? "none"}");
    }

    // --- P10 (cloud): P4 and P6 on MCP Test Cloud --------------------------------------------------------------------

    [Fact]
    public async Task P10_Cloud_SectionAndPage()
    {
        await using var run = await StartAsync(cloud: true);
        var section = await NewSectionAsync(run, "P10");
        await NewPageAsync(run, section);
        var page = await NewPageAsync(run, section);
        await SyncAsync(run);

        Ok(await Tool(run).PermanentlyDeleteHierarchyObject(page, null, Ct));
        await SyncAsync(run);
        Ok(await Tool(run).PermanentlyDeleteHierarchyObject(section, null, Ct));
        await SyncAsync(run);

        output.WriteLine($"RESULT P10: page {page} and section {section} deleted on MCP Test Cloud. Phill: check OneNote for the web, and OneDrive's recycle bin for a copy.");
    }

    // --- P11: list_grants and the CLI on a real notebook --------------------------------------------------------------

    [Fact]
    public async Task P11_GrantListRevoke_OnARealNotebook()
    {
        await using var run = await StartAsync(permanentDelete: false);
        using var cli = new StringWriter();
        GrantCommand Command(string input) =>
            new(new StringReader(input), cli, interactive: true, ct => run.Gateway.GetHierarchyAsync(string.Empty, CoreScope.Sections, ct), new SettingsWriter(run.Store, TimeProvider.System), TimeProvider.System);

        Assert.Equal(0, await Command("yes\n").RunAsync(["--permanent-delete", TestNotebook.NotebookName], Ct));
        Assert.Contains(ListGrantsUseCase.PermanentDeleteNotebookKind, await KindsAsync(run));
        Assert.Equal(0, await Command(string.Empty).RunAsync(["--revoke", "--permanent-delete", TestNotebook.NotebookName], Ct));
        Assert.DoesNotContain(ListGrantsUseCase.PermanentDeleteNotebookKind, await KindsAsync(run));
        Assert.Contains(ListGrantsUseCase.WriteNotebookKind, await KindsAsync(run));
        output.WriteLine("RESULT P11: granted, listed, revoked (write grant kept), listed again");
    }

    // --- Setup ----------------------------------------------------------------------------------------------------------

    private enum PublishMode
    {
        Real,
        Fail,
        Skip,
    }

    private sealed class Run(GuardedGateway gateway, string notebookId, string notebookKey, SettingsStore store) : IAsyncDisposable
    {
        public GuardedGateway Gateway { get; } = gateway;

        public string NotebookId { get; } = notebookId;

        public string NotebookKey { get; } = notebookKey;

        public SettingsStore Store { get; } = store;

        public WriteAccess WriteAccess => new(Gateway, Store.Read);

        public WritePipeline Pipeline() => new(new WriteLock(), Gateway, PageTextCache.WithDefaults(), Store.Read);

        public ValueTask DisposeAsync()
        {
            Gateway.Inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A real gateway over a temporary <c>settings.json</c> and journal that grant MCP Test (or MCP Test Cloud) alone.</summary>
    private async Task<Run> StartAsync(bool permanentDelete = true, bool cloud = false)
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Dictionary<string, object> { ["schemaVersion"] = 1, ["journal"] = new Dictionary<string, object> { ["path"] = Journal } }));
        var store = new SettingsStore(settingsPath, Journal);
        var com = new ComGateway(new ProcessProbe(), store);
        try
        {
            var notebookId = cloud
                ? await TestNotebook.FindCloudAsync(com, Ct)
                : (await TestNotebook.EnsureAsync(com, Ct)).NotebookId;
            Assert.SkipWhen(notebookId is null, "MCP Test Cloud isn't open in OneNote.");
            var self = HierarchyNode.Root(await com.GetHierarchyAsync(notebookId!, CoreScope.Self, Ct));
            var key = GrantKeys.NotebookKey(self.Path);
            var writer = new SettingsWriter(store, TimeProvider.System);
            writer.SaveWriteGrant(new NotebookGrant(key, self.DisplayName, notebookId!, Write: true, PermanentDelete: false, DateTimeOffset.UtcNow, GrantedVia.Cli));
            if (permanentDelete)
            {
                writer.SavePermanentDeleteGrant(key);
            }

            return new Run(new GuardedGateway(com, created), notebookId!, key, store);
        }
        catch
        {
            com.Dispose();
            throw;
        }
    }

    private static PermanentlyDeleteHierarchyObjectTool Tool(Run run)
    {
        var journal = new JournalStore(run.Store.Read);
        var services = new PermanentDeleteServices(
            run.Gateway,
            run.WriteAccess,
            new ReadAccess(run.Gateway, run.Store.Read, null, TimeProvider.System),
            new PageSnapshot(run.Gateway, journal),
            new HierarchySnapshot(run.Gateway, journal),
            new SectionBackup(run.Gateway, journal, run.Store.Read));
        return new(new ToolRunner(NullLogger<ToolRunner>.Instance), new PermanentDeleteUseCase(services, run.Pipeline()));
    }

    private async Task<string> NewSectionAsync(Run run, string label)
    {
        await TestNotebook.EnsureTestTargetAsync(run.Gateway.Inner, run.NotebookId, "OpenHierarchy(5.6 probe section)", Ct);
        var id = await run.Gateway.CreateSectionAsync(run.NotebookId, $"Delete 5.6 {label} {Stamp}.one", Ct);
        created.Add(id);
        output.WriteLine($"made section {id} ({label})");
        return id;
    }

    private async Task<string> NewGroupAsync(Run run, string label)
    {
        await TestNotebook.EnsureTestTargetAsync(run.Gateway.Inner, run.NotebookId, "OpenHierarchy(5.6 probe group)", Ct);
        var id = await run.Gateway.CreateSectionGroupAsync(run.NotebookId, $"Delete 5.6 {label} {Stamp}", Ct);
        created.Add(id);
        output.WriteLine($"made section group {id} ({label})");
        return id;
    }

    private async Task<string> NewPageAsync(Run run, string sectionId)
    {
        await TestNotebook.EnsureTestTargetAsync(run.Gateway.Inner, sectionId, "CreateNewPage(5.6 probe page)", Ct);
        var id = await run.Gateway.CreateNewPageAsync(sectionId, NewPageStyle.BlankPageWithTitle, Ct);
        created.Add(id);
        return id;
    }

    private async Task RetitleAsync(Run run, string pageId, string title)
    {
        var page = XDocument.Parse(await run.Gateway.GetPageContentAsync(pageId, PageInfo.Basic, Ct));
        var date = (string)page.Root!.Attribute("lastModifiedTime")!;
        var xml = new XElement(
            One + "Page",
            new XAttribute(XNamespace.Xmlns + "one", One.NamespaceName),
            new XAttribute("ID", pageId),
            new XElement(One + "Title", new XElement(One + "OE", new XElement(One + "T", new XCData(title)))));
        await TestNotebook.EnsureTestTargetAsync(run.Gateway.Inner, pageId, "UpdatePageContent(5.6 probe)", Ct);
        await run.Gateway.UpdatePageContentAsync(xml.ToString(SaveOptions.DisableFormatting), date, Ct);
    }

    private async Task<string> RawDeleteAsync(Run run, string id, string expected)
    {
        try
        {
            await TestNotebook.DeleteHierarchyAsync(run.Gateway.Inner, id, OneNoteDates.ToUtc(expected)!.Value, permanently: true, created, new Dictionary<string, IReadOnlySet<string>>(), Ct);
            return $"accepted the stale date; still there: {await ExistsAsync(run, id)}";
        }
        catch (OneNoteException ex)
        {
            return $"{ex.Code} {Hex(ex.ComHResult)}; still there: {await ExistsAsync(run, id)}";
        }
    }

    private static async Task<string> HierarchyDateAsync(Run run, string id) =>
        (string)XDocument.Parse(await run.Gateway.GetHierarchyAsync(id, CoreScope.Self, Ct)).Root!.Attribute("lastModifiedTime")!;

    private static async Task<PageBasisArgument> PageBasisAsync(Run run, string pageId)
    {
        var basis = OneNoteMcp.Xml.PageBasis.From(await run.Gateway.GetPageContentAsync(pageId, PageInfo.Basic, Ct));
        return new PageBasisArgument(basis.LastModifiedTime, basis.PageHash);
    }

    /// <summary>True while a live read of every open notebook still lists <paramref name="id"/> anywhere.</summary>
    private static async Task<bool> ExistsAsync(Run run, string id) =>
        TestNotebookGuard.AnyContains(await run.Gateway.GetHierarchyAsync(string.Empty, CoreScope.Pages, Ct), id);

    private static async Task SyncAsync(Run run) =>
        await run.Gateway.Inner.InvokeAsync(
            app =>
            {
                app.SyncHierarchy(run.NotebookId);
                return true;
            },
            Ct,
            retryOnDisconnect: false,
            method: "SyncHierarchy(MCP Test Cloud)");

    private static async Task<List<string>> KindsAsync(Run run) =>
        [.. (await new ListGrantsUseCase(run.Gateway, run.Store.Read, TimeProvider.System).ExecuteAsync(new ToolCallContext("p11"), Ct)).Meta.Grants!.Select(g => g.Kind)];

    private string[] Backups()
    {
        var folder = Path.Combine(Journal, JournalFiles.BackupsFolderName);
        return Directory.Exists(folder) ? Directory.GetFiles(folder) : [];
    }

    private static JsonElement Ok(CallToolResult result)
    {
        var root = result.StructuredContent!.Value;
        Assert.False(result.IsError, $"the call failed: {(result.IsError == true ? root.GetProperty("error").GetProperty("code").GetString() : null)}");
        return root;
    }

    private static string? Code(CallToolResult result) =>
        result.IsError == true ? result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() : null;

    private static string? Hresult(CallToolResult result) =>
        result.IsError == true && result.StructuredContent!.Value.GetProperty("error").TryGetProperty("hresult", out var hr) ? hr.GetString() : null;

    private static string EntryId(JsonElement root) => root.GetProperty("meta").GetProperty("journalEntryId").GetString()!;

    private static string Hex(int? hresult) =>
        hresult is { } hr ? "0x" + hr.ToString("X8", CultureInfo.InvariantCulture) : "none";

    /// <summary>
    /// The real gateway, with every write a probe can make through the tool guarded: <c>DeleteHierarchy</c> and
    /// <c>Publish</c> run only on an object this run created, inside a test notebook. P5 can make <c>Publish</c> fail or
    /// do nothing, and P4 can look at the disk just before the delete.
    /// </summary>
    private sealed class GuardedGateway(ComGateway inner, IReadOnlySet<string> created) : IOneNoteGateway
    {
        public ComGateway Inner { get; } = inner;

        public PublishMode Publish { get; set; }

        public Action<string>? BeforeDelete { get; set; }

        public async Task DeleteHierarchyAsync(string objectId, string expectedLastModified, bool permanent, CancellationToken ct)
        {
            await Guard(objectId, "DeleteHierarchy", ct);
            BeforeDelete?.Invoke(objectId);
            await Inner.DeleteHierarchyAsync(objectId, expectedLastModified, permanent, ct);
        }

        public async Task PublishAsync(string objectId, string targetPath, CancellationToken ct)
        {
            await Guard(objectId, "Publish", ct);
            switch (Publish)
            {
                case PublishMode.Fail:
                    throw new OneNoteException(ErrorCode.Internal, "Publish failed (probe P5).", unchecked((int)0x80042006));
                case PublishMode.Skip:
                    return;
                default:
                    await Inner.PublishAsync(objectId, targetPath, ct);
                    break;
            }
        }

        private async Task Guard(string objectId, string method, CancellationToken ct)
        {
            if (!created.Contains(objectId))
            {
                throw new InvalidOperationException($"Refusing {method}: {objectId} wasn't made by this run.");
            }

            await TestNotebook.EnsureTestTargetAsync(Inner, objectId, method, ct);
        }

        public Task<string> GetHierarchyAsync(string startNodeId, CoreScope scope, CancellationToken ct) => Inner.GetHierarchyAsync(startNodeId, scope, ct);

        public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) => Inner.GetHierarchyParentAsync(objectId, ct);

        public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct) => Inner.GetPageContentAsync(pageId, pageInfo, ct);

        public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) => Inner.GetBinaryPageContentAsync(pageId, callbackId, ct);

        public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) => Inner.FindPagesAsync(startNodeId, query, includeUnindexed, ct);

        public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) => Inner.FindMetaAsync(startNodeId, name, includeUnindexed, ct);

        public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => Inner.GetHyperlinkToObjectAsync(hierarchyId, pageContentObjectId, ct);

        public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => Inner.GetWebHyperlinkToObjectAsync(hierarchyId, pageContentObjectId, ct);

        public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => Inner.GetSpecialLocationAsync(location, ct);

        public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct) =>
            throw new InvalidOperationException("Probes never navigate (AD-11).");

        public Task<string> CreateNewPageAsync(string sectionId, NewPageStyle style, CancellationToken ct) => Inner.CreateNewPageAsync(sectionId, style, ct);

        public Task<string> CreateSectionAsync(string parentId, string sectionFileName, CancellationToken ct) => Inner.CreateSectionAsync(parentId, sectionFileName, ct);

        public Task<string> CreateSectionGroupAsync(string parentId, string groupName, CancellationToken ct) => Inner.CreateSectionGroupAsync(parentId, groupName, ct);

        public Task<string> CreateNotebookAsync(string folderPath, CancellationToken ct) =>
            throw new InvalidOperationException("Probes never create a notebook here.");

        public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct) => Inner.UpdatePageContentAsync(pageXml, expectedLastModified, ct);

        public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct) => Inner.DeletePageContentAsync(pageId, objectId, expectedLastModified, ct);

        public Task UpdateHierarchyAsync(string changesXml, CancellationToken ct) =>
            throw new InvalidOperationException("Probes never rename or reorder here.");

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => Inner.WaitForAbandonedWritesAsync(ct);
    }
}
