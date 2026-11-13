using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Com;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
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
/// Story 5.3: <c>create_section</c>, <c>create_section_group</c> and <c>create_notebook</c> against a real OneNote, opt-in
/// with <c>ONENOTE_MCP_INTEGRATION=1</c>. Grants go to a temporary <c>settings.json</c>, never the user's.
/// <list type="bullet">
/// <item>Sections and section groups go in MCP Test only, under stamped names, and are soft-deleted at the end, each only
/// after a fresh read shows it is one this run made inside MCP Test. A failed clean-up is logged and never fails the test.</item>
/// <item>The notebook is a throwaway, <c>MCP Test Throwaway …</c>, in <see cref="TestNotebook.ThrowawayRoot"/> (Phill,
/// 2026-11-13, 5.3 Q6). COM can't delete a notebook, so it stays open: the output names it for Phill to close and delete.</item>
/// <item>The cloud probe (SEAM(5.3 cloud probe), <see cref="CloudNotebookCreate"/>) tries <c>OpenHierarchy</c>
/// <c>cftNotebook</c> at a new URL beside MCP Test Cloud and records what came back. It never runs in a build.</item>
/// </list>
/// The output carries IDs, codes and the throwaway names only.
/// </summary>
public sealed class CreateHierarchyIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));
    /// <summary>What this run made, in order; only these may be deleted.</summary>
    private readonly List<string> created = [];

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
            // Best effort: only a temporary settings file.
        }
    }

    [Fact]
    public async Task SectionAndGroup_AreCreatedInMcpTest_AnExistingNameIsReturned_ThenTheyAreSoftDeleted()
    {
        await using var run = await StartAsync();
        var useCase = UseCase(run);
        var stamp = Stamp;

        try
        {
            var section = Ok(await new CreateSectionTool(Runner(), useCase).CreateSection(run.NotebookId, $"Create 5.3 {stamp}", Ct));
            var sectionId = ObjectId(section);
            created.Add(sectionId);
            output.WriteLine($"section {sectionId} under {run.NotebookId}");
            Assert.True(await IsLiveChildAsync(run.Gateway, run.NotebookId, sectionId, "Section"));

            var again = Ok(await new CreateSectionTool(Runner(), useCase).CreateSection(run.NotebookId, $"create 5.3 {stamp}.one", Ct));
            Assert.Equal(sectionId, ObjectId(again));
            Assert.Equal("alreadyExisted", again.GetProperty("warnings")[0].GetProperty("code").GetString());

            var group = Ok(await new CreateSectionGroupTool(Runner(), useCase).CreateSectionGroup(run.NotebookId, $"Create 5.3 Group {stamp}", Ct));
            var groupId = ObjectId(group);
            created.Add(groupId);
            output.WriteLine($"section group {groupId} under {run.NotebookId}");
            Assert.True(await IsLiveChildAsync(run.Gateway, run.NotebookId, groupId, "SectionGroup"));

            var inner = Ok(await new CreateSectionTool(Runner(), useCase).CreateSection(groupId, "Inside", Ct));
            created.Add(ObjectId(inner));
            Assert.True(await IsLiveChildAsync(run.Gateway, groupId, ObjectId(inner), "Section"));

            var clash = await new CreateSectionTool(Runner(), useCase).CreateSection(run.NotebookId, $"Create 5.3 Group {stamp}", Ct);
            Assert.Equal("InvalidArgument", clash.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    [Fact]
    public async Task Notebook_IsCreatedAsAThrowaway_WithItsCreatedGrant_AndListGrantsShowsIt()
    {
        await using var run = await StartAsync();
        var name = TestNotebook.ThrowawayNamePrefix + "Create " + Stamp;
        TestNotebookGuard.EnsureThrowawayLeaf(name);
        var root = TestNotebook.ThrowawayRoot;
        TestNotebookGuard.EnsureFolderIsOurs(Path.Combine(root, name));
        Directory.CreateDirectory(root);
        var prompt = new ChosenFolder(root);
        var useCase = UseCase(run);

        var result = await Runner().RunAsync(
            CreateNotebookTool.Name,
            useCase,
            (context, ct) => useCase.CreateNotebookAsync(context, name, root, prompt, ct),
            Ct);

        var envelope = Ok(result);
        var notebookId = ObjectId(envelope);
        var path = envelope.GetProperty("meta").GetProperty("path").GetString()!;
        output.WriteLine($"PHILL: close the notebook \"{name}\" in OneNote, then delete its folder {path}");
        Assert.Equal(0, envelope.GetProperty("warnings").GetArrayLength());

        // A live read shows it is the throwaway this run named, in the throwaway root.
        var notebooks = await run.Gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct);
        Assert.Single(TestNotebookGuard.ThrowawayNotebookIds(notebooks, root, new Dictionary<string, IReadOnlySet<string>> { [notebookId] = new HashSet<string>([name]) }));

        var store = new SettingsStore(run.SettingsPath);
        var listed = await new ListGrantsUseCase(run.Gateway, store.Read, TimeProvider.System).ExecuteAsync(new ToolCallContext("it"), Ct);
        var grant = Assert.Single(listed.Meta.Grants!, g => g.Key == GrantKeys.NotebookKey(path));
        Assert.Equal(ListGrantsUseCase.WriteNotebookKind, grant.Kind);
        Assert.Equal(GrantedVia.Created, grant.GrantedVia);
        Assert.Equal("active", grant.Status);
    }

    /// <summary>
    /// SEAM(5.3 cloud probe): what <c>OpenHierarchy(url, "", cftNotebook)</c> does at a new OneDrive URL beside MCP Test
    /// Cloud. It records the ID or HRESULT, the read-back <c>path</c> and whether its key matches the URL sent, and what
    /// <c>SyncHierarchy</c> answers; whether it really syncs, Phill checks in OneNote and OneDrive. It asserts nothing about
    /// the outcome: it is evidence for opening the seam. Skipped when MCP Test Cloud isn't open.
    /// </summary>
    [Fact]
    public async Task CloudNotebookProbe_RecordsWhatOpenHierarchyDoesAtAOneDriveUrl()
    {
        await using var run = await StartAsync();
        var notebooks = await run.Gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct);
        var cloudId = TestNotebookGuard.FindCloudNotebookId(notebooks);
        Assert.SkipWhen(cloudId is null, "MCP Test Cloud isn't open in OneNote.");
        var cloudPath = TestNotebookGuard.NotebookPath(notebooks, cloudId!)!;
        Assert.True(TestNotebookGuard.IsCloudTestPath(cloudPath));

        var parent = cloudPath.TrimEnd('/')[..cloudPath.TrimEnd('/').LastIndexOf('/')];
        var name = TestNotebook.ThrowawayNamePrefix + "Cloud Create " + Stamp;
        var url = parent + "/" + name + "/";
        Assert.True(TestNotebookGuard.IsCloudPathNamed(url, name), "the probe URL must be a OneDrive folder beside MCP Test Cloud");

        string id;
        try
        {
            id = await run.Gateway.InvokeAsync(
                app =>
                {
                    app.OpenHierarchy(url, string.Empty, out var notebookId, CreateFileType.cftNotebook);
                    return notebookId;
                },
                Ct,
                retryOnDisconnect: false,
                method: "OpenHierarchy(cloud throwaway notebook)");
        }
        catch (OneNoteException ex)
        {
            output.WriteLine($"RESULT cloud-notebook-create: error {ex.Code} {Hex(ex.ComHResult)}");
            return;
        }
        catch (COMException ex)
        {
            output.WriteLine($"RESULT cloud-notebook-create: error {Hex(ex.HResult)}");
            return;
        }

        output.WriteLine($"PHILL: close the notebook \"{name}\" in OneNote, then delete it from OneDrive > Documents");
        string? readBack = null;
        try
        {
            readBack = HierarchyNode.Root(await run.Gateway.GetHierarchyAsync(id, CoreScope.Self, Ct)).Path;
        }
        catch (OneNoteException ex)
        {
            output.WriteLine($"read-back failed: {ex.Code} {Hex(ex.ComHResult)}");
        }

        var keyMatches = readBack is not null && GrantKeys.Comparer.Equals(GrantKeys.NotebookKey(readBack), GrantKeys.NotebookKey(url));
        output.WriteLine($"RESULT cloud-notebook-create: id {id}, read-back path {readBack ?? "(none)"}, key matches the URL sent: {keyMatches}");

        try
        {
            await run.Gateway.InvokeAsync(
                app =>
                {
                    app.SyncHierarchy(id);
                    return true;
                },
                Ct,
                retryOnDisconnect: false,
                method: "SyncHierarchy(cloud throwaway notebook)");
            output.WriteLine("RESULT cloud-notebook-sync: SyncHierarchy returned; check OneNote's sync status and OneDrive > Documents");
        }
        catch (OneNoteException ex)
        {
            output.WriteLine($"RESULT cloud-notebook-sync: error {ex.Code} {Hex(ex.ComHResult)}");
        }
    }

    // --- Setup ----------------------------------------------------------------------------------------------------

    private sealed record Run(ComGateway Gateway, string NotebookId, string SettingsPath) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Gateway.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A real gateway over a temporary <c>settings.json</c> that grants MCP Test alone, keyed by its live path.</summary>
    private async Task<Run> StartAsync()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");
        File.WriteAllText(settingsPath, """{ "schemaVersion": 1 }""");
        var store = new SettingsStore(settingsPath);
        var gateway = new ComGateway(new ProcessProbe(), store);
        try
        {
            var notebook = await TestNotebook.EnsureAsync(gateway, Ct);
            var self = HierarchyNode.Root(await gateway.GetHierarchyAsync(notebook.NotebookId, CoreScope.Self, Ct));
            Assert.Equal(TestNotebook.NotebookName, self.Name);
            new SettingsWriter(store, TimeProvider.System).SaveWriteGrant(new NotebookGrant(
                GrantKeys.NotebookKey(self.Path), TestNotebook.NotebookName, notebook.NotebookId, Write: true, PermanentDelete: false, DateTimeOffset.UtcNow, GrantedVia.Cli));
            return new Run(gateway, notebook.NotebookId, settingsPath);
        }
        catch
        {
            gateway.Dispose();
            throw;
        }
    }

    private static CreateHierarchyUseCase UseCase(Run run)
    {
        var store = new SettingsStore(run.SettingsPath);
        return new CreateHierarchyUseCase(
            run.Gateway,
            new WriteAccess(run.Gateway, store.Read),
            new WritePipeline(new WriteLock(), run.Gateway, PageTextCache.WithDefaults(), store.Read),
            store.Read,
            new SettingsWriter(store, TimeProvider.System),
            TimeProvider.System);
    }

    private static ToolRunner Runner() => new(NullLogger<ToolRunner>.Instance);

    /// <summary>The location dialog, answered with the throwaway root: the test stands in for the user.</summary>
    private sealed class ChosenFolder(string folder) : INotebookLocationPrompt
    {
        public Task<NotebookLocationResult> AskAsync(NotebookLocationQuestion question, CancellationToken ct) =>
            Task.FromResult(new NotebookLocationResult(GrantPromptStatus.Accepted, NotebookLocation.Local, folder));
    }

    private static JsonElement Ok(CallToolResult result)
    {
        var root = result.StructuredContent!.Value;
        Assert.False(result.IsError, $"the create failed: {(result.IsError == true ? root.GetProperty("error").GetProperty("code").GetString() : null)}");
        return root;
    }

    private static string ObjectId(JsonElement root) => root.GetProperty("meta").GetProperty("objectId").GetString()!;

    private static async Task<bool> IsLiveChildAsync(ComGateway gateway, string parentId, string childId, string kind) =>
        XDocument.Parse(await gateway.GetHierarchyAsync(parentId, CoreScope.Children, Ct)).Root!
            .Elements(One + kind)
            .Any(e => (string?)e.Attribute("ID") == childId && (string?)e.Attribute("isInRecycleBin") != "true");

    /// <summary>
    /// Soft-deletes what this run made, innermost first, each through the guarded delete (only IDs in
    /// <see cref="created"/>, only inside MCP Test). A failure is logged and never fails the test.
    /// </summary>
    private async Task CleanUpAsync(Run run)
    {
        var ours = created.ToHashSet(StringComparer.Ordinal);
        foreach (var id in Enumerable.Reverse(created).ToList())
        {
            try
            {
                var self = XDocument.Parse(await run.Gateway.GetHierarchyAsync(id, CoreScope.Self, Ct)).Root!;
                if ((string?)self.Attribute("isInRecycleBin") == "true")
                {
                    continue;
                }

                var date = OneNoteDates.ToUtc((string?)self.Attribute("lastModifiedTime")) ?? DateTime.MinValue;
                await TestNotebook.DeleteHierarchyAsync(run.Gateway, id, date, permanently: false, ours, new Dictionary<string, IReadOnlySet<string>>(), Ct);
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

    private static string Hex(int? hresult) =>
        hresult is { } hr ? "0x" + hr.ToString("X8", CultureInfo.InvariantCulture) : "none";
}
