using System.Diagnostics;
using System.Xml.Linq;
using OneNoteMcp.Com;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;
using Xunit;
using CoreScope = OneNoteMcp.Core.HierarchyScope;
using CoreSpecialLocation = OneNoteMcp.Core.SpecialLocation;
using PageInfo = OneNoteMcp.Core.PageInfo;

namespace OneNoteMcp.Integration;

/// <summary>
/// AD-3 against a real OneNote (story 5). Opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c>; OneNote must already be running.
/// Unattended tests run in every opt-in run. Each hitl scenario also needs <c>ONENOTE_MCP_SCENARIO=&lt;name&gt;</c>
/// and a state Phill sets up first: <c>not-running</c>, <c>modal-dialog</c>, <c>restart</c>, <c>elevated</c>.
/// Writes go only through <see cref="TestNotebook"/>; reads of other notebooks are read-only.
/// </summary>
public sealed class LifecycleScenarioTests : IDisposable
{
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string _settingsDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- Unattended -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ForcedHang_TimesOut_ReplacementReadSucceeds_AbandonedWorkerReleased()
    {
        RequireRunningOneNote();
        using var gateway = Gateway("""{ "schemaVersion": 1, "timeouts": { "callSeconds": 2 } }""");

        // Warm up so the hang runs on an activated worker.
        await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct);

        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<OneNoteException>(() => gateway.InvokeAsync(
            _ =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(5)); // past the 2 s call timeout; never touches COM
                return true;
            },
            Ct,
            method: "forced hang"));
        stopwatch.Stop();

        Assert.Equal(ErrorCode.Timeout, ex.Code);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4));
        Assert.Equal(1, gateway.AbandonedWorkers);

        // A fresh worker and COM reference serve the next read.
        var xml = await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct);
        Assert.Equal(One + "Notebooks", XDocument.Parse(xml).Root!.Name);

        // The abandoned call returns after its sleep: the worker releases its reference and exits.
        var waited = Stopwatch.StartNew();
        while (gateway.AbandonedWorkers > 0)
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(15), "The abandoned worker did not exit.");
            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task ReadMethods_Smoke_OverMcpTest()
    {
        RequireRunningOneNote();
        using var gateway = Gateway("""{ "schemaVersion": 1 }""");
        var notebook = await TestNotebook.EnsureAsync(gateway, Ct);

        // MCP Test exists at the scratch folder.
        var notebooks = await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct);
        Assert.Equal(notebook.NotebookId, TestNotebookGuard.FindNotebookId(notebooks, notebook.Folder));
        Assert.True(Directory.Exists(notebook.Folder), $"{notebook.Folder} should exist.");

        var tree = XDocument.Parse(await gateway.GetHierarchyAsync(notebook.NotebookId, CoreScope.Pages, Ct)).Root!;
        Assert.Equal(notebook.NotebookId, (string?)tree.Attribute("ID"));
        Assert.Contains(tree.Descendants(One + "Page"), p => (string?)p.Attribute("ID") == notebook.PageId);

        Assert.Equal(notebook.SectionId, await gateway.GetHierarchyParentAsync(notebook.PageId, Ct));

        var page = XDocument.Parse(await gateway.GetPageContentAsync(notebook.PageId, PageInfo.Basic, Ct)).Root!;
        Assert.Equal(One + "Page", page.Name);
        Assert.Equal(notebook.PageId, (string?)page.Attribute("ID"));

        Assert.StartsWith("onenote:", await gateway.GetHyperlinkToObjectAsync(notebook.PageId, string.Empty, Ct), StringComparison.OrdinalIgnoreCase);

        foreach (var location in Enum.GetValues<CoreSpecialLocation>())
        {
            Assert.False(string.IsNullOrWhiteSpace(await gateway.GetSpecialLocationAsync(location, Ct)), location.ToString());
        }

        // Search may lag or miss new pages (AD-12): only the XML shape is checked.
        var found = XDocument.Parse(await gateway.FindPagesAsync(notebook.NotebookId, "MCP", includeUnindexed: true, Ct)).Root!;
        Assert.Equal(One + "Notebook", found.Name);
        var meta = XDocument.Parse(await gateway.FindMetaAsync(notebook.NotebookId, "onenote-mcp-none", includeUnindexed: true, Ct)).Root!;
        Assert.Equal(One + "Notebook", meta.Name);

        // The seed page has no binary object and MCP Test is local: both fail, mapped (never a raw COMException).
        await Assert.ThrowsAsync<OneNoteException>(() => gateway.GetBinaryPageContentAsync(notebook.PageId, "{00000000-0000-0000-0000-000000000000}", Ct));
        // Observed 2026-11-04: for a local notebook OneNote returns an empty link rather than an error.
        Assert.Equal(string.Empty, await gateway.GetWebHyperlinkToObjectAsync(notebook.PageId, string.Empty, Ct));
    }

    // --- Hitl -----------------------------------------------------------------------------------------------------

    /// <summary>Phill ends ONENOTE.EXE in Task Manager first.</summary>
    [Fact]
    public async Task Scenario_NotRunning_IsOneNoteNotRunning_AndNothingStarts()
    {
        RequireScenario("not-running");
        Assert.False(new ProcessProbe().IsOneNoteRunning(), "End ONENOTE.EXE in Task Manager first.");
        using var gateway = Gateway("""{ "schemaVersion": 1, "allowSilentActivation": false }""");

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct));

        Assert.Equal(ErrorCode.OneNoteNotRunning, ex.Code);
        Assert.False(new ProcessProbe().IsOneNoteRunning(), "The gateway must not have started OneNote.");
    }

    /// <summary>Phill opens a modal dialog in OneNote first (for example File > Options) and leaves it open.</summary>
    [Fact]
    public async Task Scenario_ModalDialog_ReadsWork_GuardedWriteIsModalDialogOpen()
    {
        RequireScenario("modal-dialog");
        RequireRunningOneNote();
        using var gateway = Gateway("""{ "schemaVersion": 1 }""");

        // Reads work while the dialog is open (the guard's own reads included).
        var notebooks = await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct);
        var folder = TestNotebook.ResolveFolder(Environment.GetEnvironmentVariable);
        var notebookId = TestNotebookGuard.FindNotebookId(notebooks, folder);
        Assert.SkipWhen(notebookId is null, "Run the unattended suite once first so MCP Test exists.");
        var tree = await gateway.GetHierarchyAsync(notebookId!, CoreScope.Pages, Ct);
        var sectionId = TestNotebookGuard.FindSectionId(tree, TestNotebook.SeedSectionName);
        Assert.SkipWhen(sectionId is null, "MCP Test has no Seed section. Run the unattended suite once first to seed it.");

        var pageId = TestNotebookGuard.FirstPageId(tree, sectionId!);
        Assert.SkipWhen(pageId is null, "MCP Test's Seed section has no page. Run the unattended suite once first to seed it.");

        // UpdatePageContent is the call the spike saw blocked (0x80042030). Observed 2026-11-04: CreateNewPage is not
        // blocked by File > Options, so it can't probe this.
        var xml = $"""<?xml version="1.0"?><one:Page xmlns:one="{OneNoteXml.Namespace2013}" ID="{pageId}"><one:Outline><one:OEChildren><one:OE><one:T><![CDATA[modal-dialog probe]]></one:T></one:OE></one:OEChildren></one:Outline></one:Page>""";
        var ex = await Assert.ThrowsAsync<OneNoteException>(() => TestNotebook.WriteAsync(
            gateway,
            pageId!,
            app =>
            {
                app.UpdatePageContent(xml, DateTime.MinValue, XMLSchema.xs2013, force: false);
                return true;
            },
            "UpdatePageContent(modal-dialog probe)",
            Ct));

        Assert.Equal(ErrorCode.ModalDialogOpen, ex.Code);
    }

    /// <summary>Phill closes OneNote and starts it again within 120 s of the test starting.</summary>
    [Fact]
    public async Task Scenario_Restart_ReadSucceedsViaReconnect()
    {
        RequireScenario("restart");
        RequireRunningOneNote();
        using var gateway = Gateway("""{ "schemaVersion": 1 }""");
        await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct);
        var before = OneNoteProcessIds();

        var waited = Stopwatch.StartNew();
        while (OneNoteProcessIds() is var now && (now.Count == 0 || now.Overlaps(before)))
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(120), "No new OneNote process within 120 s. Close OneNote, then start it again.");
            await Task.Delay(500, Ct);
        }

        await Task.Delay(TimeSpan.FromSeconds(5), Ct); // let OneNote finish starting

        var xml = await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct);
        Assert.Equal(One + "Notebooks", XDocument.Parse(xml).Root!.Name);
    }

    /// <summary>Phill runs the suite from an elevated shell while OneNote runs normally.</summary>
    [Fact]
    public async Task Scenario_Elevated_IsIntegrityMismatch_WithoutActivation()
    {
        RequireScenario("elevated");
        RequireRunningOneNote();
        var report = new ProcessProbe().CheckIntegrity();
        Assert.False(report.Matches, $"Expected a mismatch from an elevated shell: {report.Describe()}");
        using var gateway = Gateway("""{ "schemaVersion": 1 }""");

        var stopwatch = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<OneNoteException>(() => gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct));
        stopwatch.Stop();

        Assert.Equal(ErrorCode.IntegrityMismatch, ex.Code);
        Assert.Null(ex.ComHResult); // refused before COM was created, not 0x80080005 after a hang
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"took {stopwatch.Elapsed}");
        Assert.False(gateway.HasApplication);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_settingsDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    // --- Helpers --------------------------------------------------------------------------------------------------

    private ComGateway Gateway(string settingsJson)
    {
        Directory.CreateDirectory(_settingsDir);
        var path = Path.Combine(_settingsDir, "settings.json");
        File.WriteAllText(path, settingsJson);
        return new ComGateway(new ProcessProbe(), new SettingsStore(path));
    }

    private static void RequireRunningOneNote()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
    }

    private static void RequireScenario(string name)
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("ONENOTE_MCP_SCENARIO") == name,
            $"Hitl scenario: set ONENOTE_MCP_SCENARIO={name} after setting up its state.");
    }

    private static HashSet<int> OneNoteProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("ONENOTE"))
        {
            using (process)
            {
                ids.Add(process.Id);
            }
        }

        return ids;
    }
}
