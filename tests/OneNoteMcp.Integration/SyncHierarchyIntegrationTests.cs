using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using Xunit;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 4.3: <c>sync_hierarchy</c>'s use case through the <c>AppState</c> pipeline and the real <see cref="ComGateway"/>.
/// Opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c>. It syncs MCP Test, a section and a page in it, each only after the guard's
/// live read shows it inside MCP Test, and a made-up ID that a full live read finds in no notebook. Settings go to a
/// temporary <c>settings.json</c>, never the user's. Whether a sync really ran isn't observable (spike 4.1 S1): the live
/// sitting checks OneNote's sync status. It prints IDs and codes only.
/// </summary>
public sealed class SyncHierarchyIntegrationTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>A made-up hierarchy ID, in OneNote's shape, that no notebook holds.</summary>
    private const string MadeUpId = "{5C0FFEE0-0000-4000-8000-000000004303}{1}{B0}";

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
            // Best effort: only a temporary settings file.
        }
    }

    [Fact]
    public async Task SyncHierarchy_McpTest_ASection_AndAPage_AreOk_WithSyncRequested()
    {
        using var gateway = Start();
        var notebook = await TestNotebook.EnsureAsync(gateway, Ct);
        var useCase = UseCase(gateway);

        foreach (var (what, id) in new[] { ("notebook", notebook.NotebookId), ("section", notebook.SectionId), ("page", notebook.PageId) })
        {
            await TestNotebook.EnsureTestTargetAsync(gateway, id, $"SyncHierarchy({what})", Ct);

            var result = await useCase.ExecuteAsync(new ToolCallContext("sync-" + what), id, Ct);

            var warning = Assert.Single(result.Warnings);
            Assert.Equal(Core.Results.ToolWarnings.SyncRequested, warning.Code);
            output.WriteLine($"sync {what} {id}: ok, {warning.Code}");
        }
    }

    [Fact]
    public async Task SyncHierarchy_AMadeUpId_IsObjectNotFound()
    {
        using var gateway = Start();
        var everything = await gateway.GetHierarchyAsync(string.Empty, CoreScope.Pages, Ct);
        Assert.False(TestNotebookGuard.AnyContains(everything, MadeUpId), $"Refusing SyncHierarchy: {MadeUpId} is in an open notebook.");

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => UseCase(gateway).ExecuteAsync(new ToolCallContext("sync-made-up"), MadeUpId, Ct));

        Assert.Equal(ErrorCode.ObjectNotFound, ex.Code);
        Assert.False(ex.OutcomeUnknown);
        output.WriteLine($"sync made-up {MadeUpId}: {ex.Code} {(ex.ComHResult is { } hr ? Core.Results.ToolErrors.FormatHResult(hr) : "-")}");
    }

    private ComGateway Start()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");
        File.WriteAllText(settingsPath, """{ "schemaVersion": 1 }""");
        return new ComGateway(new ProcessProbe(), new SettingsStore(settingsPath, Path.Combine(tempDir, "journal")));
    }

    private static SyncHierarchyUseCase UseCase(ComGateway gateway) => new(gateway, new AppStatePipeline(gateway));
}
