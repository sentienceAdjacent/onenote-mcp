using System.Xml.Linq;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;
using Xunit;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 4.4's live check, opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c> and never run in a build:
/// <c>dotnet test tests/OneNoteMcp.Integration --filter CloseNotebook</c>, with MCP Test and the throwaway
/// <c>MCP Test Throwaway 1112-034716 b</c> open, and an MCP Test page on screen.
/// <list type="bullet">
/// <item><c>close_notebook</c>'s use case runs through the <c>AppState</c> pipeline over a gateway that refuses
/// <c>SyncHierarchy</c> and <c>CloseNotebook</c> unless <see cref="TestNotebookGuard.CheckClose"/> passes on a live read
/// (throwaways b and c, the cloud throwaway; never MCP Test or MCP Test Cloud), and every other write.</item>
/// <item>Throwaway b gets a write grant in the run's own temporary <c>settings.json</c>, is closed (only when nothing of it is
/// on screen), reopened by its recorded path (<see cref="TestNotebook.OpenExistingAsync"/>, <c>cftNone</c>), and
/// <c>list_grants</c> then shows the grant <c>active</c>.</item>
/// <item>The guard refuses MCP Test: nothing is synced or closed.</item>
/// </list>
/// The output carries IDs, codes and statuses only.
/// </summary>
public sealed class CloseNotebookIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private const string ThrowawayB = "MCP Test Throwaway 1112-034716 b";

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a temporary settings file.
        }
    }

    [Fact]
    public async Task CloseNotebook_ThrowawayB_ClosesIt_ReopensByPath_AndItsWriteGrantIsActiveAgain()
    {
        var (com, store) = Start();
        using var _ = com;
        var b = await TestNotebook.AdoptThrowawayNotebookAsync(com, ThrowawayB, Ct);
        Assert.SkipWhen(b is null, $"Open the throwaway notebook \"{ThrowawayB}\" in OneNote first.");
        var shown = XDocument.Parse(await com.GetHierarchyAsync(b!, CoreScope.Pages, Ct)).Root!
            .DescendantsAndSelf().Any(e => (string?)e.Attribute("isCurrentlyViewed") == "true");
        Assert.SkipWhen(shown, "Show an MCP Test page in OneNote first: the throwaway must not be on screen when it closes.");

        var path = HierarchyNode.Root(await com.GetHierarchyAsync(b!, CoreScope.Self, Ct)).Path;
        var key = GrantKeys.NotebookKey(path);
        new SettingsWriter(store, TimeProvider.System).SaveWriteGrant(
            new NotebookGrant(key, ThrowawayB, b!, Write: true, PermanentDelete: false, DateTimeOffset.UtcNow, GrantedVia.Cli));
        Assert.Equal("active", await GrantStatusAsync(com, store, key));

        var result = await UseCase(com).ExecuteAsync(new ToolCallContext("close-b"), b!, Ct);

        Assert.Equal(b, result.Meta.ObjectId);
        Assert.Equal(path, result.Meta.Path);
        Assert.Equal(ToolWarnings.NotebookClosed, Assert.Single(result.Warnings).Code);
        Assert.Equal(b, (string?)XElement.Parse(result.Xml!).Attribute("ID"));
        output.WriteLine($"close {b}: ok, {result.Warnings[0].Code}; grant after close: {await GrantStatusAsync(com, store, key)}");

        // Reopen by the recorded path (cftNone): the guard allows only throwaway b's, c's or the cloud throwaway's.
        var reopened = await TestNotebook.OpenExistingAsync(com, path, runProbeRoot: null, recordedPaths: [path], allowOpenTestNotebooks: false, Ct);
        output.WriteLine($"reopen: new ID {reopened} (old {b})");

        // OneNote's list can lag about a minute after a reopen.
        var status = await GrantStatusAsync(com, store, key);
        for (var i = 0; i < 30 && status != "active"; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            status = await GrantStatusAsync(com, store, key);
        }

        output.WriteLine($"grant after reopen: {status}");
        Assert.Equal("active", status);
    }

    [Fact]
    public async Task CloseNotebook_McpTest_IsRefusedByTheGuard_NothingSyncedOrClosed()
    {
        var (com, _) = Start();
        using var _ = com;
        var notebook = await TestNotebook.EnsureAsync(com, Ct);
        var guarded = new GuardedGateway(com);

        await Assert.ThrowsAsync<InvalidOperationException>(() => UseCase(guarded).ExecuteAsync(new ToolCallContext("close-mcp-test"), notebook.NotebookId, Ct));

        Assert.Equal(0, guarded.Sent);
        output.WriteLine($"close MCP Test {notebook.NotebookId}: refused by the guard");
    }

    private (ComGateway Com, SettingsStore Store) Start()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");
        File.WriteAllText(settingsPath, """{ "schemaVersion": 1 }""");
        var store = new SettingsStore(settingsPath, Path.Combine(tempDir, "journal"));
        return (new ComGateway(new ProcessProbe(), store), store);
    }

    /// <summary>The use case over <see cref="GuardedGateway"/> and a pipeline with a write lock of its own, the real 10 s settle.</summary>
    private static CloseNotebookUseCase UseCase(ComGateway com) => UseCase(new GuardedGateway(com));

    private static CloseNotebookUseCase UseCase(GuardedGateway gateway) =>
        new(gateway, new AppStatePipeline(gateway, writeLock: new WriteLock(@"Local\OneNoteMcp.Test." + Guid.NewGuid().ToString("N"))));

    private static async Task<string?> GrantStatusAsync(ComGateway com, SettingsStore store, string key)
    {
        var listed = await new ListGrantsUseCase(com, store.Read, TimeProvider.System).ExecuteAsync(new ToolCallContext("list-grants"), Ct);
        return listed.Meta.Grants?.SingleOrDefault(g => g.Kind == "writeNotebook" && string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase))?.Status;
    }

    /// <summary>
    /// The real gateway, except that <c>SyncHierarchy</c> and <c>CloseNotebook</c> are refused unless
    /// <see cref="TestNotebookGuard.CheckClose"/> passes on a live read of the open notebooks, and every other write is
    /// refused. Reads go straight through.
    /// </summary>
    private sealed class GuardedGateway(ComGateway inner) : IOneNoteGateway
    {
        private int sent;

        /// <summary>How many <c>SyncHierarchy</c> and <c>CloseNotebook</c> calls passed the guard.</summary>
        public int Sent => Volatile.Read(ref sent);

        public async Task SyncHierarchyAsync(string hierarchyId, CancellationToken ct)
        {
            await CheckAsync("SyncHierarchy(close_notebook)", hierarchyId, ct);
            await inner.SyncHierarchyAsync(hierarchyId, ct);
        }

        public async Task CloseNotebookAsync(string notebookId, CancellationToken ct)
        {
            await CheckAsync("CloseNotebook(close_notebook)", notebookId, ct);
            await inner.CloseNotebookAsync(notebookId, ct);
        }

        private async Task CheckAsync(string method, string id, CancellationToken ct)
        {
            var notebooks = await inner.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, ct);
            TestNotebookGuard.CheckClose(method, notebooks, id, TestNotebook.ResolveFolder(Environment.GetEnvironmentVariable), TestNotebook.ThrowawayRoot, runProbeRoot: null);
            Interlocked.Increment(ref sent);
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

        public Task UpdateHierarchyAsync(string changesXml, CancellationToken ct) => Refused("UpdateHierarchy");

        public Task DeleteHierarchyAsync(string objectId, string expectedLastModified, bool permanent, CancellationToken ct) => Refused("DeleteHierarchy");

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => inner.WaitForAbandonedWritesAsync(ct);

        private static Task Refused(string method) =>
            Task.FromException(new InvalidOperationException($"close_notebook never calls {method}."));

        private static Task<T> Refused<T>(string method) =>
            Task.FromException<T>(new InvalidOperationException($"close_notebook never calls {method}."));
    }
}
