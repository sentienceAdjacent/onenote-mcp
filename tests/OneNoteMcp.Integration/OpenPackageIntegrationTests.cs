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
using OneNoteMcp.Integration.WriteProbes;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 3.8's live check for sitting S1, opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c>, with the throwaway
/// <c>MCP Test Throwaway 1112-034716 a</c> open: <c>dotnet test tests/OneNoteMcp.Integration --filter
/// FullyQualifiedName~OpenPackageIntegrationTests</c>. A temporary <c>settings.json</c> makes a temporary folder the only
/// export root. The throwaway is packaged there (the guarded <c>Publish</c>), then <c>open_package</c> opens the package
/// into a new folder beside the marker file; a page of the copy is refused without a read grant and read with a CLI-style
/// one; the copy is closed through the guarded <see cref="TestNotebook.CloseOpenedCopyAsync"/> and the folder deleted.
/// Output carries IDs, codes and counts only.
/// </summary>
[Collection(ThrowawayOpenState.Name)]
public sealed class OpenPackageIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Root => Path.Combine(tempDir, "exports");

    public void Dispose()
    {
        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the copy may still be open if the test failed before closing it.
        }
    }

    [Fact]
    public async Task TheThrowawaysPackage_Opens_BesideTheMarker_ItsPageNeedsAReadGrant_ThenTheCopyIsClosed()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        Directory.CreateDirectory(Root);
        var settingsPath = Path.Combine(tempDir, "settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["schemaVersion"] = 1,
            ["exportRoots"] = new[] { Root },
            ["journal"] = new Dictionary<string, object> { ["path"] = Path.Combine(tempDir, "journal") },
        }));
        // The run's temp root is also its opened-copies folder (3.8 Q3), so the copy never lands in the user's AppData.
        var store = new SettingsStore(settingsPath, Path.Combine(tempDir, "journal"), Root);
        using var com = new ComGateway(new ProcessProbe(), store);

        var throwaway = await TestNotebook.AdoptThrowawayNotebookAsync(com, Round2.LocalThrowawayName, Ct);
        Assert.SkipWhen(throwaway is null, $"Open the throwaway notebook \"{Round2.LocalThrowawayName}\" in OneNote first.");
        var package = Path.Combine(Root, "packages", "throwaway.onepkg");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        await TestNotebook.PublishAsync(com, throwaway!, package, PublishFormat.pfOneNotePackage, Root, new Dictionary<string, IReadOnlySet<string>> { [throwaway!] = new HashSet<string>([Round2.LocalThrowawayName]) }, Ct);
        await WaitForFileAsync(package);

        var pipeline = new WritePipeline(new WriteLock(), com, PageTextCache.WithDefaults(), store.Read);
        var tool = new OpenPackageTool(new ToolRunner(NullLogger<ToolRunner>.Instance), new OpenPackageUseCase(com, store.Read, pipeline));
        var result = Ok(await tool.OpenPackage(package, "opened", Ct));
        var folder = result.GetProperty("meta").GetProperty("outputPath").GetString()!;
        var copyId = result.GetProperty("meta").GetProperty("objectId").GetString()!;
        var warnings = string.Join(",", result.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()));
        output.WriteLine($"RESULT open_package: copy {copyId}, folder under the root: {folder.StartsWith(Root, StringComparison.OrdinalIgnoreCase)}, warnings [{warnings}]");
        try
        {
            Assert.NotEqual(throwaway, copyId);
            Assert.True(File.Exists(Path.Combine(folder, ExportRootGuard.MarkerFileName)));

            // AC 1: get_hierarchy lists the copy while it is open.
            var listed = XDocument.Parse(await com.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct)).Root!.Elements(One + "Notebook").Select(n => (string?)n.Attribute("ID")).ToList();
            Assert.Contains(copyId, listed);

            // A page of the copy: protected by its path (entry 15), so refused without a grant, read with one.
            var pages = XDocument.Parse(await com.GetHierarchyAsync(copyId, CoreScope.Pages, Ct)).Root!;
            var page = pages.Descendants(One + "Page").FirstOrDefault();
            Assert.SkipWhen(page is null, "The copy listed no page; nothing to read.");
            var section = page!.Parent!;
            var access = new ReadAccess(com, store.Read, new SettingsWriter(store, TimeProvider.System), TimeProvider.System);
            var read = new GetPageContentUseCase(com, access);
            var runner = new ToolRunner(NullLogger<ToolRunner>.Instance);
            var refused = await runner.RunAsync("get_page_content", read, (c, ct) => read.ExecuteAsync(c, (string)page.Attribute("ID")!, false, ct), Ct);
            Assert.Equal("ReadNotGranted", Code(refused));

            new SettingsWriter(store, TimeProvider.System).SaveReadGrant(new ProtectedSectionGrant(
                GrantKeys.SectionKey((string)section.Attribute("path")!),
                "copy section",
                GrantKeys.NotebookKey(folder),
                (string)section.Attribute("ID")!,
                DateTimeOffset.UtcNow,
                GrantedVia.Cli,
                DateTimeOffset.UtcNow.AddHours(1)));
            var allowed = await runner.RunAsync("get_page_content", read, (c, ct) => read.ExecuteAsync(c, (string)page.Attribute("ID")!, false, ct), Ct);
            Assert.Null(Code(allowed));
            output.WriteLine("RESULT read: ReadNotGranted without a grant, ok with one");
        }
        finally
        {
            await TestNotebook.CloseOpenedCopyAsync(com, copyId, new HashSet<string>([copyId]), Root, Ct);
        }

        var open = XDocument.Parse(await com.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct)).Root!.Elements(One + "Notebook").Select(n => (string?)n.Attribute("ID")).ToList();
        Assert.DoesNotContain(copyId, open);
        Assert.Contains(throwaway, open);

        // AC 4: its folder goes too (OneNote may hold its files for a moment after the close).
        for (var i = 0; i < 50 && Directory.Exists(folder); i++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200, Ct);
            }
        }

        Assert.False(Directory.Exists(folder), "The copy's folder couldn't be deleted after the close.");
        output.WriteLine("RESULT close: the copy is closed and its folder deleted, the throwaway still open");
    }

    private static async Task WaitForFileAsync(string path)
    {
        // A package lands 250–530 ms after Publish returns (spike 3.1 X2).
        for (var i = 0; i < 100 && !(File.Exists(path) && new FileInfo(path).Length > 0); i++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.True(File.Exists(path), "The package didn't land.");
    }

    private static JsonElement Ok(CallToolResult result)
    {
        var root = result.StructuredContent!.Value;
        Assert.False(result.IsError, root.GetRawText());
        return root;
    }

    private static string? Code(CallToolResult result) =>
        result.IsError == true ? result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() : null;
}
