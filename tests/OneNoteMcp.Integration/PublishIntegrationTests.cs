using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
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
/// Story 3.3's live checks for the sitting (S1 in the plan), opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c> and never run in
/// a build: <c>dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~PublishIntegrationTests</c>, with MCP
/// Test open and its Ballast Vault unlocked. <c>settings.json</c> is a temporary one whose only export root is a temporary
/// folder, so every file lands there and is deleted afterwards. <see cref="GuardedGateway"/> lets <c>Publish</c> run only on
/// an object inside a test notebook, and refuses every other write. The Vault check pauses (the probes' pause file) for
/// Phill to lock the Vault with Ctrl+Alt+L, then runs both refusals; nothing is published there. The output carries IDs,
/// codes and sizes only.
/// </summary>
public sealed class PublishIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Stamp => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private string Root => Path.Combine(tempDir, "exports");

    public void Dispose()
    {
        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a temporary settings file and the exported PDFs.
        }
    }

    [Fact]
    public async Task APage_LandsAsPdf_UnderTheRoot_ThenFileExists_AndOutsideTheRootIsRefused()
    {
        await using var run = await StartAsync();
        var name = $"mcp-test-{Stamp}.pdf";

        // PDF lands: on disk, not empty, a PDF, at meta.outputPath under the temporary root (created by the export).
        var root = Ok(await Tool(run).Publish(run.PageId, name, PublishFormatArgument.Pdf, Ct));
        var outputPath = root.GetProperty("meta").GetProperty("outputPath").GetString()!;
        Assert.Equal(Path.Combine(FilePathGate.Canonical(Root, out _)!, name), outputPath, ignoreCase: true);
        var bytes = await File.ReadAllBytesAsync(outputPath, Ct);
        Assert.True(bytes.Length > 0);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        output.WriteLine($"RESULT pdf: page {run.PageId} -> {bytes.Length} bytes");

        // FileExists: the same name again, refused before COM; the file keeps its bytes.
        Assert.Equal("FileExists", Code(await Tool(run).Publish(run.PageId, name, PublishFormatArgument.Pdf, Ct)));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(outputPath, Ct));

        // ExportPathNotAllowed: a path that leaves the root, refused before COM.
        Assert.Equal("ExportPathNotAllowed", Code(await Tool(run).Publish(run.PageId, $@"..\escaped-{Stamp}.pdf", PublishFormatArgument.Pdf, Ct)));
        Assert.Equal(1, run.Gateway.PublishCalls);
        Assert.Single(Directory.GetFiles(tempDir, "*.pdf", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task TheBallastVault_Locked_IsReadNotGrantedWithoutAGrant_AndSectionLockedWithOne()
    {
        await using var run = await StartAsync();
        var children = XDocument.Parse(await run.Gateway.GetHierarchyAsync(run.NotebookId, CoreScope.Children, Ct));
        var vault = children.Root!.Elements(One + "Section").FirstOrDefault(s => (string?)s.Attribute("name") == BallastContent.VaultSectionName);
        Assert.SkipWhen(vault is null, "MCP Test has no Ballast Vault: run the Ballast scenario (story 17).");
        Assert.SkipUnless((string?)vault!.Attribute("encrypted") is "true" or "1", "The Ballast Vault isn't password-protected yet (story 17's owner step).");
        Assert.SkipWhen((string?)vault.Attribute("locked") is "true" or "1", "Unlock the Ballast Vault in OneNote first: its page IDs are read while it is unlocked.");
        var vaultId = (string)vault.Attribute("ID")!;

        // Hierarchy only (IDs, never content): one page of the Vault, read while it is unlocked (3.3 plan Q3).
        var pageId = XDocument.Parse(await run.Gateway.GetHierarchyAsync(vaultId, CoreScope.Pages, Ct))
            .Descendants(One + "Page").Select(p => (string?)p.Attribute("ID")).FirstOrDefault();
        Assert.SkipWhen(pageId is null, "The Ballast Vault has no page.");
        output.WriteLine($"Vault {vaultId}: page {pageId}");

        Assert.True(
            await PauseAsync("vault-lock", "In OneNote, press Ctrl+Alt+L to lock the password-protected sections (the Ballast Vault in MCP Test). Then continue."),
            "The Vault must be locked for this check.");
        var now = XDocument.Parse(await run.Gateway.GetHierarchyAsync(vaultId, CoreScope.Self, Ct)).Root!;
        Assert.True((string?)now.Attribute("locked") is "true" or "1", "The Ballast Vault still reads as unlocked.");

        // No read grant: ReadNotGranted, before the locked check (AD-21), naming the section's ID only.
        var refused = await Tool(run).Publish(pageId!, $"vault-{Stamp}.pdf", PublishFormatArgument.Pdf, Ct);
        Assert.Equal("ReadNotGranted", Code(refused));
        Assert.DoesNotContain(BallastContent.VaultSectionName, refused.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);

        // A CLI-style read grant in the temporary settings: SectionLocked, before COM.
        new SettingsWriter(run.Store, TimeProvider.System).SaveReadGrant(new ProtectedSectionGrant(
            GrantKeys.SectionKey((string)vault.Attribute("path")!),
            BallastContent.VaultSectionName,
            GrantKeys.NotebookKey((string)children.Root.Attribute("path")!),
            vaultId,
            DateTimeOffset.UtcNow,
            GrantedVia.Cli,
            DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Equal("SectionLocked", Code(await Tool(run).Publish(pageId!, $"vault-{Stamp}.pdf", PublishFormatArgument.Pdf, Ct)));

        // Nothing was published in either case.
        Assert.Equal(0, run.Gateway.PublishCalls);
        Assert.False(Directory.Exists(Root) && Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Any());
        output.WriteLine("RESULT vault: ReadNotGranted without a grant, SectionLocked with one; nothing published");
    }

    // --- Helpers --------------------------------------------------------------------------------------------------------

    /// <summary>A real gateway over a temporary <c>settings.json</c> whose only export root is a temporary folder.</summary>
    private async Task<Run> StartAsync()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["schemaVersion"] = 1,
            ["exportRoots"] = new[] { Root },
            ["journal"] = new Dictionary<string, object> { ["path"] = Path.Combine(tempDir, "journal") },
        }));
        var store = new SettingsStore(settingsPath, Path.Combine(tempDir, "journal"));
        var com = new ComGateway(new ProcessProbe(), store);
        try
        {
            var notebook = await TestNotebook.EnsureAsync(com, Ct);
            return new Run(new GuardedGateway(com), notebook.NotebookId, notebook.PageId, store);
        }
        catch
        {
            com.Dispose();
            throw;
        }
    }

    private static PublishTool Tool(Run run) => new(
        new ToolRunner(NullLogger<ToolRunner>.Instance),
        new PublishUseCase(
            run.Gateway,
            new ReadAccess(run.Gateway, run.Store.Read, null, TimeProvider.System),
            run.Store.Read,
            new WritePipeline(new WriteLock(), run.Gateway, PageTextCache.WithDefaults(), run.Store.Read)));

    /// <summary>
    /// The probes' hitl pause (<see cref="ProbeSession.PauseAsync"/>'s file and protocol): writes the instruction to the
    /// pause file and waits until the main session deletes it (true) or writes <c>skip</c> into it (false).
    /// </summary>
    private async Task<bool> PauseAsync(string step, string instruction)
    {
        Directory.CreateDirectory(ProbeRecorder.ProbesDir);
        var file = Path.Combine(ProbeRecorder.ProbesDir, ProbeSession.PauseFileName);
        await File.WriteAllTextAsync(file, $"[publish / {step}]\n{instruction}\n\nWhen done, delete this file. To skip this step, replace this file's content with: skip\n", Ct);
        output.WriteLine($"PAUSE ({file}): {instruction}");
        Console.WriteLine($"PAUSE [publish / {step}]: {instruction}");

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

    private sealed class Run(GuardedGateway gateway, string notebookId, string pageId, SettingsStore store) : IAsyncDisposable
    {
        public GuardedGateway Gateway { get; } = gateway;

        public string NotebookId { get; } = notebookId;

        /// <summary>MCP Test's seed page.</summary>
        public string PageId { get; } = pageId;

        public SettingsStore Store { get; } = store;

        public ValueTask DisposeAsync()
        {
            Gateway.Inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// The real gateway with every write guarded: <c>Publish</c> runs only on an object inside a test notebook (it is
    /// counted), and every other write is refused. Reads go straight through.
    /// </summary>
    private sealed class GuardedGateway(ComGateway inner) : IOneNoteGateway
    {
        private int publishCalls;

        public ComGateway Inner { get; } = inner;

        /// <summary>How many <c>Publish</c> calls reached the real gateway.</summary>
        public int PublishCalls => Volatile.Read(ref publishCalls);

        public async Task PublishAsync(string objectId, string targetPath, ExportFormat format, CancellationToken ct)
        {
            await TestNotebook.EnsureTestTargetAsync(Inner, objectId, "Publish", ct);
            Interlocked.Increment(ref publishCalls);
            await Inner.PublishAsync(objectId, targetPath, format, ct);
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

        public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct) => Refused("NavigateTo");

        public Task<string> CreateNewPageAsync(string sectionId, NewPageStyle style, CancellationToken ct) => Refused<string>("CreateNewPage");

        public Task<string> CreateSectionAsync(string parentId, string sectionFileName, CancellationToken ct) => Refused<string>("OpenHierarchy");

        public Task<string> CreateSectionGroupAsync(string parentId, string groupName, CancellationToken ct) => Refused<string>("OpenHierarchy");

        public Task<string> CreateNotebookAsync(string folderPath, CancellationToken ct) => Refused<string>("OpenHierarchy");

        public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct) => Refused("UpdatePageContent");

        public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct) => Refused("DeletePageContent");

        public Task UpdateHierarchyAsync(string changesXml, CancellationToken ct) => Refused("UpdateHierarchy");

        public Task DeleteHierarchyAsync(string objectId, string expectedLastModified, bool permanent, CancellationToken ct) => Refused("DeleteHierarchy");

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => Inner.WaitForAbandonedWritesAsync(ct);

        private static Task Refused(string method) =>
            Task.FromException(new InvalidOperationException($"The publish checks never call {method}."));

        private static Task<T> Refused<T>(string method) =>
            Task.FromException<T>(new InvalidOperationException($"The publish checks never call {method}."));
    }
}
