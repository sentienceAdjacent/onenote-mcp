using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Server;
using OneNoteMcp.Xml;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 4.5's live check, opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c> and never run in a build:
/// <c>dotnet test tests/OneNoteMcp.Integration --filter OpenNotebook</c>, with MCP Test and the throwaway
/// <c>MCP Test Throwaway 1112-034716 b</c> open, and an MCP Test page on screen.
/// <list type="bullet">
/// <item><c>open_notebook</c>'s use case runs through the <c>AppState</c> pipeline over a gateway that refuses
/// <c>OpenHierarchy</c> unless <see cref="TestNotebookGuard.CheckOpen"/> passes on a live read (this run's probe folder, or
/// throwaway b's recorded folder), and refuses every other write. Closes go through
/// <see cref="TestNotebook.CloseNotebookAsync"/> (<see cref="TestNotebookGuard.CheckClose"/>).</item>
/// <item>Throwaway b is closed, opened by its path (a new ID; <c>list_grants</c> shows its test grant <c>active</c>), opened
/// again (its current ID, <c>alreadyExisted</c>), closed, and opened through a junction made in the run's probe folder.
/// It ends open, as it started.</item>
/// <item>A URL, a missing path, a <c>.onepkg</c>, a OneDrive folder, a folder with no <c>.onetoc2</c> and a section group's
/// folder of a closed run notebook are refused, and nothing appears in them. With the run's probe folder as the test's
/// export root, a notebook made there (its <c>.onetoc2</c> waited for, up to 5 min), closed and opened reads
/// <c>ReadNotGranted</c> until granted. It is closed and its folder deleted at the end.</item>
/// </list>
/// The output carries IDs, codes and statuses only.
/// </summary>
[Collection(ThrowawayOpenState.Name)]
public sealed class OpenNotebookIntegrationTests(ITestOutputHelper output) : IDisposable
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
    public async Task OpenNotebook_ThrowawayB_ByPath_ThenAgain_ThenThroughAJunction()
    {
        var (com, store, _) = Start(exportRoot: null);
        using var comScope = com;
        var b = await TestNotebook.AdoptThrowawayNotebookAsync(com, ThrowawayB, Ct);
        Assert.SkipWhen(b is null, $"Open the throwaway notebook \"{ThrowawayB}\" in OneNote first.");
        var shown = XDocument.Parse(await com.GetHierarchyAsync(b!, CoreScope.Pages, Ct)).Root!
            .DescendantsAndSelf().Any(e => (string?)e.Attribute("isCurrentlyViewed") == "true");
        Assert.SkipWhen(shown, "Show an MCP Test page in OneNote first: the throwaway must not be on screen when it closes.");

        var path = HierarchyNode.Root(await com.GetHierarchyAsync(b!, CoreScope.Self, Ct)).Path;
        var canonical = FilePathGate.Canonical(path, out _)!;
        var key = GrantKeys.NotebookKey(path);
        new SettingsWriter(store, TimeProvider.System).SaveWriteGrant(
            new NotebookGrant(key, ThrowawayB, b!, Write: true, PermanentDelete: false, DateTimeOffset.UtcNow, GrantedVia.Cli));
        var run = NewRunProbeRoot();
        var junction = Path.Combine(run, "probe junction " + TestNotebookGuard.RunStamp(run));
        var guarded = new GuardedGateway(com, run, [canonical]);
        var current = b!;
        try
        {
            await TestNotebook.CloseNotebookAsync(com, current, runProbeRoot: null, Ct);
            output.WriteLine($"closed b {current}");

            // By its path: a new ID, and the grant keyed by its path is active again.
            var opened = await UseCase(guarded).ExecuteAsync(new ToolCallContext("open-b"), path, Ct);
            current = opened.Meta.ObjectId!;
            Assert.NotEqual(b, current);
            Assert.Equal(canonical, opened.Meta.Path);
            Assert.Equal(ToolWarnings.NotebookListMayLag, Assert.Single(opened.Warnings).Code);
            Assert.Equal(1, guarded.Opens);
            var status = await GrantStatusAsync(com, store, key);
            for (var i = 0; i < 30 && status != "active"; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), Ct);
                status = await GrantStatusAsync(com, store, key);
            }

            output.WriteLine($"open b by path: new ID {current} (old {b}), xml {(opened.Xml is null ? "absent" : "present")}, grant {status}");
            Assert.Equal("active", status);

            // Already open: its current ID, alreadyExisted, nothing sent.
            var again = await UseCase(guarded).ExecuteAsync(new ToolCallContext("open-b-again"), path, Ct);
            Assert.Equal(current, again.Meta.ObjectId);
            Assert.Equal(ToolWarnings.AlreadyExisted, Assert.Single(again.Warnings).Code);
            Assert.Equal(1, guarded.Opens);
            output.WriteLine($"open b again: {again.Meta.ObjectId}, {again.Warnings[0].Code}");

            // Through a junction in the run's probe folder: OneNote is sent the junction's target.
            await TestNotebook.CloseNotebookAsync(com, current, runProbeRoot: null, Ct);
            MakeJunction(junction, canonical, run);
            var viaJunction = await UseCase(guarded).ExecuteAsync(new ToolCallContext("open-b-junction"), junction, Ct);
            current = viaJunction.Meta.ObjectId!;
            Assert.Equal(canonical, viaJunction.Meta.Path);
            Assert.Equal(2, guarded.Opens);
            var reported = TestNotebookGuard.NotebookPath(await com.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct), current);
            output.WriteLine($"open b through a junction: {current}, path reported is b's folder: {string.Equals(GrantKeys.NotebookKey(reported ?? string.Empty), GrantKeys.NotebookKey(canonical), StringComparison.OrdinalIgnoreCase)}");
        }
        finally
        {
            var open = XDocument.Parse(await com.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, Ct)).Root!.Elements(One + "Notebook")
                .Any(n => string.Equals(GrantKeys.NotebookKey((string?)n.Attribute("path") ?? string.Empty), GrantKeys.NotebookKey(path), StringComparison.OrdinalIgnoreCase));
            if (!open)
            {
                output.WriteLine($"PHILL: throwaway b is closed; reopen it in OneNote (File > Open, its folder under %LOCALAPPDATA%\\OneNoteMcp-Test).");
            }

            RemoveJunction(junction, run);
            TryDelete(run);
        }
    }

    [Fact]
    public async Task OpenNotebook_Refusals_SendNothing_AndARunNotebookUnderAnExportRoot_NeedsAReadGrant()
    {
        var run = NewRunProbeRoot();
        var (com, store, _) = Start(exportRoot: run);
        using var comScope = com;
        await TestNotebook.EnsureAsync(com, Ct);
        var guarded = new GuardedGateway(com, run, []);
        string? notebookId = null;
        try
        {
            // Refused before COM: nothing is opened, and nothing appears in the folders.
            var empty = Path.Combine(run, "no toc");
            Directory.CreateDirectory(empty);
            await File.WriteAllTextAsync(Path.Combine(empty, "notes.txt"), "x", Ct);
            var package = Path.Combine(run, "package.onepkg");
            await File.WriteAllBytesAsync(package, [0], Ct);
            var before = Listing(run);
            await RefusedAsync(guarded, "https://d.docs.live.net/0000000000000000/Documents/probe " + TestNotebookGuard.RunStamp(run), "url");
            await RefusedAsync(guarded, Path.Combine(run, "missing"), "missing");
            await RefusedAsync(guarded, package, "onepkg");
            await RefusedAsync(guarded, empty, "no .onetoc2");
            Assert.Equal(before, Listing(run));
            if (OneDriveAccounts.ForThisUser().Current().Accounts.FirstOrDefault()?.Folder is { } oneDrive && Directory.Exists(oneDrive))
            {
                var top = Directory.GetFileSystemEntries(oneDrive).Order(StringComparer.OrdinalIgnoreCase).ToList();
                await RefusedAsync(guarded, oneDrive, "OneDrive folder");
                Assert.Equal(top, Directory.GetFileSystemEntries(oneDrive).Order(StringComparer.OrdinalIgnoreCase).ToList());
            }
            else
            {
                output.WriteLine("no OneDrive folder on this machine: that refusal wasn't tried live");
            }

            // A run notebook with a section group and a page, closed once its .onetoc2 has landed.
            var name = TestNotebook.ThrowawayNamePrefix + "Open " + TestNotebookGuard.RunStamp(run);
            notebookId = await TestNotebook.CreateThrowawayNotebookAsync(com, name, requireEmpty: true, Ct, root: run);
            var folder = Path.Combine(run, name);
            var groupId = await com.CreateSectionGroupAsync(notebookId, "Group", Ct);
            await com.CreateSectionAsync(groupId, "Inside.one", Ct);
            var sectionId = await com.CreateSectionAsync(notebookId, "Top.one", Ct);
            await com.CreateNewPageAsync(sectionId, NewPageStyle.BlankPageWithTitle, Ct);
            var landed = await WaitForTocAsync(folder, TimeSpan.FromMinutes(5));
            output.WriteLine($"run notebook {notebookId}: .onetoc2 {(landed ? "landed" : "not landed within 5 min")}");
            Assert.True(landed);
            await TestNotebook.CloseNotebookAsync(com, notebookId, run, Ct);
            notebookId = null;

            var group = Path.Combine(folder, "Group");
            if (Directory.Exists(group))
            {
                var groupBefore = Listing(group);
                await RefusedAsync(guarded, group, "section group's folder");
                Assert.Equal(groupBefore, Listing(group));
            }
            else
            {
                output.WriteLine("the section group's folder wasn't on disk: that refusal wasn't tried live");
            }

            Assert.Equal(0, guarded.Opens);

            // Opened from under the export root: its sections count as protected by path (AD-21), so a read needs a grant.
            var opened = await UseCase(guarded).ExecuteAsync(new ToolCallContext("open-run"), folder, Ct);
            notebookId = opened.Meta.ObjectId!;
            Assert.Equal(1, guarded.Opens);
            output.WriteLine($"open run notebook: {notebookId}, {opened.Warnings[0].Code}");
            var page = await FirstPageAsync(com, notebookId);
            Assert.SkipWhen(page is null, "The reopened notebook listed no page within 30 s; nothing to read.");
            var access = new ReadAccess(com, store.Read, new SettingsWriter(store, TimeProvider.System), TimeProvider.System);
            var read = new GetPageContentUseCase(com, access);
            var runner = new ToolRunner(NullLogger<ToolRunner>.Instance);
            var refused = await runner.RunAsync("get_page_content", read, (c, ct) => read.ExecuteAsync(c, (string)page!.Attribute("ID")!, false, ct), Ct);
            Assert.Equal("ReadNotGranted", Code(refused));
            var section = page!.Parent!;
            new SettingsWriter(store, TimeProvider.System).SaveReadGrant(new ProtectedSectionGrant(
                GrantKeys.SectionKey((string)section.Attribute("path")!),
                "run section",
                GrantKeys.NotebookKey(folder),
                (string)section.Attribute("ID")!,
                DateTimeOffset.UtcNow,
                GrantedVia.Cli,
                DateTimeOffset.UtcNow.AddHours(1)));
            var allowed = await runner.RunAsync("get_page_content", read, (c, ct) => read.ExecuteAsync(c, (string)page.Attribute("ID")!, false, ct), Ct);
            Assert.Null(Code(allowed));
            output.WriteLine("read: ReadNotGranted without a grant, ok with one");
        }
        finally
        {
            if (notebookId is not null)
            {
                await TestNotebook.CloseNotebookAsync(com, notebookId, run, Ct);
            }

            await DeleteWithRetryAsync(run);
        }
    }

    private async Task RefusedAsync(GuardedGateway guarded, string path, string what)
    {
        var refused = await Assert.ThrowsAsync<OneNoteException>(() => UseCase(guarded).ExecuteAsync(new ToolCallContext("refuse"), path, Ct));
        Assert.Equal(ErrorCode.InvalidArgument, refused.Code);
        output.WriteLine($"{what}: {refused.Code}");
    }

    private (ComGateway Com, SettingsStore Store, string SettingsPath) Start(string? exportRoot)
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var settings = new Dictionary<string, object> { ["schemaVersion"] = 1 };
        if (exportRoot is not null)
        {
            settings["exportRoots"] = new[] { exportRoot };
        }

        File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings));
        var store = new SettingsStore(settingsPath, Path.Combine(tempDir, "journal"));
        return (new ComGateway(new ProcessProbe(), store), store, settingsPath);
    }

    /// <summary>A new run probe folder, <c>…\probes\app-state\&lt;MMdd-HHmmss&gt;</c>, which the guards allow.</summary>
    private static string NewRunProbeRoot()
    {
        string run;
        do
        {
            run = Path.Combine(TestNotebook.AppStateProbesDir, DateTime.Now.ToString("MMdd-HHmmss", CultureInfo.InvariantCulture));
        }
        while (Directory.Exists(run) && WaitASecond());

        Assert.True(TestNotebookGuard.IsRunProbeRoot(run, TestNotebook.ThrowawayRoot));
        Directory.CreateDirectory(run);
        return run;
    }

    private static bool WaitASecond()
    {
        Thread.Sleep(1000);
        return true;
    }

    private static OpenNotebookUseCase UseCase(GuardedGateway gateway) =>
        new(gateway, new AppStatePipeline(gateway, writeLock: new WriteLock(@"Local\OneNoteMcp.Test." + Guid.NewGuid().ToString("N"))));

    private static async Task<string?> GrantStatusAsync(ComGateway com, SettingsStore store, string key)
    {
        var listed = await new ListGrantsUseCase(com, store.Read, TimeProvider.System).ExecuteAsync(new ToolCallContext("list-grants"), Ct);
        return listed.Meta.Grants?.SingleOrDefault(g => g.Kind == "writeNotebook" && string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase))?.Status;
    }

    private static async Task<XElement?> FirstPageAsync(ComGateway com, string notebookId)
    {
        for (var i = 0; i < 15; i++)
        {
            var page = XDocument.Parse(await com.GetHierarchyAsync(notebookId, CoreScope.Pages, Ct)).Root!.Descendants(One + "Page").FirstOrDefault();
            if (page is not null)
            {
                return page;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        }

        return null;
    }

    private static async Task<bool> WaitForTocAsync(string folder, TimeSpan wait)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < wait)
        {
            if (NotebookFolders.HoldsANotebookToc(folder))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), Ct);
        }

        return NotebookFolders.HoldsANotebookToc(folder);
    }

    private static string[] Listing(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder, "*", new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true }).Order(StringComparer.Ordinal)];

    private static string? Code(ModelContextProtocol.Protocol.CallToolResult result) =>
        result.StructuredContent is { } content && content.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            ? error.GetProperty("code").GetString()
            : null;

    /// <summary><c>mklink /J</c> of <paramref name="link"/> (strictly inside the run's probe folder) to <paramref name="target"/>.</summary>
    private static void MakeJunction(string link, string target, string run)
    {
        TestNotebookGuard.EnsureUnder("mklink", link, run);
        if (Directory.Exists(link) || File.Exists(link) || !Directory.Exists(target))
        {
            throw new InvalidOperationException("Refusing mklink: the link exists or the target isn't a folder.");
        }

        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe didn't start.");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000) || process.ExitCode != 0 || !new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("mklink /J failed.");
        }
    }

    /// <summary>Removes the junction without following it; its target stays.</summary>
    private static void RemoveJunction(string link, string run)
    {
        TestNotebookGuard.EnsureUnder("remove junction", link, run);
        var info = new DirectoryInfo(link);
        if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(link, recursive: false);
        }
    }

    private static void TryDelete(string run)
    {
        try
        {
            if (Directory.Exists(run) && !Directory.EnumerateFileSystemEntries(run).Any())
            {
                Directory.Delete(run);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the user: an empty probe folder.
        }
    }

    /// <summary>Deletes the run's folder (its notebook closed first); OneNote may hold files for a moment after a close.</summary>
    private async Task DeleteWithRetryAsync(string run)
    {
        foreach (var dir in Directory.Exists(run) ? Directory.EnumerateDirectories(run).Where(d => new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)).ToList() : [])
        {
            Directory.Delete(dir, recursive: false);
        }

        for (var i = 0; i < 50 && Directory.Exists(run); i++)
        {
            try
            {
                Directory.Delete(run, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200, Ct);
            }
        }

        if (Directory.Exists(run))
        {
            output.WriteLine($"PHILL: delete the run folder {run}");
        }
    }

    /// <summary>
    /// The real gateway, except that <c>OpenHierarchy</c> is refused unless <see cref="TestNotebookGuard.CheckOpen"/>
    /// passes on a live read (a path in this run's probe folder, or one of the recorded throwaway paths), and every other
    /// write, <c>SyncHierarchy</c> and <c>CloseNotebook</c> included, is refused. Reads go straight through.
    /// </summary>
    private sealed class GuardedGateway(ComGateway inner, string run, IReadOnlyCollection<string> recordedPaths) : IOneNoteGateway
    {
        private int opens;

        /// <summary>How many <c>OpenHierarchy</c> calls passed the guard.</summary>
        public int Opens => Volatile.Read(ref opens);

        public async Task<string> OpenHierarchyAsync(string path, CancellationToken ct)
        {
            var notebooks = await inner.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, ct);
            TestNotebookGuard.CheckOpen("OpenHierarchy(open_notebook)", path, notebooks, TestNotebook.ResolveFolder(Environment.GetEnvironmentVariable), TestNotebook.ThrowawayRoot, run, recordedPaths, allowOpenTestNotebooks: false);
            Interlocked.Increment(ref opens);
            return await inner.OpenHierarchyAsync(path, ct);
        }

        public Task SyncHierarchyAsync(string hierarchyId, CancellationToken ct) => Refused("SyncHierarchy");

        public Task CloseNotebookAsync(string notebookId, CancellationToken ct) => Refused("CloseNotebook");

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

        public Task<string> CreateSectionAsync(string parentId, string sectionFileName, CancellationToken ct) => Refused<string>("OpenHierarchy(cftSection)");

        public Task<string> CreateSectionGroupAsync(string parentId, string groupName, CancellationToken ct) => Refused<string>("OpenHierarchy(cftFolder)");

        public Task<string> CreateNotebookAsync(string folderPath, CancellationToken ct) => Refused<string>("OpenHierarchy(cftNotebook)");

        public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct) => Refused("UpdatePageContent");

        public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct) => Refused("DeletePageContent");

        public Task UpdateHierarchyAsync(string changesXml, CancellationToken ct) => Refused("UpdateHierarchy");

        public Task DeleteHierarchyAsync(string objectId, string expectedLastModified, bool permanent, CancellationToken ct) => Refused("DeleteHierarchy");

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => inner.WaitForAbandonedWritesAsync(ct);

        private static Task Refused(string method) =>
            Task.FromException(new InvalidOperationException($"open_notebook never calls {method}."));

        private static Task<T> Refused<T>(string method) =>
            Task.FromException<T>(new InvalidOperationException($"open_notebook never calls {method}."));
    }
}
