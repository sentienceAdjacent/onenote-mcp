using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Com;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Logging;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 2.4: <c>create_page</c> against a real OneNote, on MCP Test only, in its <c>Create tests</c> section (created
/// through the guard when missing). Opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c>. Grants go to a temporary
/// <c>settings.json</c>, never the user's: it grants MCP Test alone, keyed by its live path.
/// <list type="bullet">
/// <item>A granted create through the real gateway adds a page there.</item>
/// <item>An ungranted create is <c>WriteNotGranted</c> and adds no page.</item>
/// <item>Two <c>OneNoteMcp.exe</c> processes over stdio fire concurrent creates; their <c>write lock</c> log lines
/// (AD-18) never overlap.</item>
/// </list>
/// At the end the pages this run created are soft-deleted (Phill, 2026-11-10, 2.4 plan Q6), each only after a fresh
/// <c>GetHierarchy</c> shows it is a <c>one:Page</c> inside MCP Test. A failed clean-up is logged and never fails the
/// test. The output records what each soft delete did, as evidence for epic 5. It prints IDs, codes and times only.
/// </summary>
public sealed partial class CreatePageIntegrationTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The section this suite's pages go in.</summary>
    public const string SectionName = "Create tests";

    /// <summary>Overrides the server exe the two-process test starts (default: this configuration's build of OneNoteMcp.Server).</summary>
    public const string ExeOverrideVariable = "ONENOTE_MCP_TEST_EXE";

    private const int CallsPerProcess = 5;

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));
    private readonly List<string> created = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: only a temporary settings file and logs.
        }
    }

    [Fact]
    public async Task Granted_CreatesAPage_InCreateTests_ThroughTheRealGateway()
    {
        await using var run = await StartAsync(grant: true);

        try
        {
            var result = await Tool(run).CreatePage(run.SectionId, cancellationToken: Ct);

            var root = result.StructuredContent!.Value;
            Assert.False(result.IsError, $"create_page failed: {(result.IsError == true ? root.GetProperty("error").GetProperty("code").GetString() : null)}");
            var pageId = root.GetProperty("meta").GetProperty("pageId").GetString()!;
            created.Add(pageId);
            output.WriteLine($"created {pageId} in {run.SectionId}");

            var pages = await PageIdsAsync(run.Gateway, run.SectionId);
            Assert.Contains(pageId, pages);
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    [Fact]
    public async Task Ungranted_IsWriteNotGranted_AndAddsNoPage()
    {
        await using var run = await StartAsync(grant: false);
        var before = await PageIdsAsync(run.Gateway, run.SectionId);

        var result = await Tool(run).CreatePage(run.SectionId, cancellationToken: Ct);

        Assert.True(result.IsError);
        Assert.Equal("WriteNotGranted", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(before, await PageIdsAsync(run.Gateway, run.SectionId));
    }

    [Fact]
    public async Task TwoServerProcesses_ConcurrentCreates_TheirWriteLockHoldsNeverOverlap()
    {
        await using var run = await StartAsync(grant: true);
        var exe = ServerExe();
        Assert.True(File.Exists(exe), $"Build OneNoteMcp.Server first, or set {ExeOverrideVariable}: {exe} not found.");

        try
        {
            await RunTwoProcessesAsync(run, exe);
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    private async Task RunTwoProcessesAsync(Run run, string exe)
    {
        var stderr = new[] { new ConcurrentQueue<string>(), new ConcurrentQueue<string>() };
        var logDirs = new[] { Path.Combine(tempDir, "logs-a"), Path.Combine(tempDir, "logs-b") };
        await using var a = await ClientAsync(exe, run.SettingsPath, logDirs[0], stderr[0]);
        await using var b = await ClientAsync(exe, run.SettingsPath, logDirs[1], stderr[1]);

        // Fire every call at once, from both processes.
        var calls = Enumerable.Range(0, CallsPerProcess)
            .SelectMany(_ => new[] { (Process: 0, Client: a), (Process: 1, Client: b) })
            .Select(async c => (c.Process, Result: await c.Client.CallToolAsync(
                CreatePageTool.Name,
                new Dictionary<string, object?> { ["sectionId"] = run.SectionId },
                cancellationToken: Ct)))
            .ToList();
        var results = await Task.WhenAll(calls);

        var ok = 0;
        foreach (var (process, result) in results)
        {
            var root = result.StructuredContent!.Value;
            if (result.IsError != true && root.GetProperty("ok").GetBoolean())
            {
                created.Add(root.GetProperty("meta").GetProperty("pageId").GetString()!);
                ok++;
            }
            else
            {
                output.WriteLine($"process {process}: {root.GetProperty("error").GetProperty("code").GetString()}");
            }
        }

        Assert.Equal(2 * CallsPerProcess, ok);

        // Each pipeline that held the lock logs one line on release; wait for all of them.
        var holds = await HoldsAsync(stderr, ok);
        Assert.Equal(ok, holds.Count);
        Assert.Contains(holds, h => h.Process == 0);
        Assert.Contains(holds, h => h.Process == 1);

        var ordered = holds.OrderBy(h => h.Acquired).ToList();
        foreach (var h in ordered)
        {
            output.WriteLine($"process {h.Process} hold {h.CorrelationId}: {WriteLockHeld.Format(h.Acquired)} to {WriteLockHeld.Format(h.Released)}, waited {h.WaitMs} ms");
        }

        for (var i = 1; i < ordered.Count; i++)
        {
            Assert.True(
                ordered[i - 1].Released <= ordered[i].Acquired,
                $"holds overlap: {ordered[i - 1].CorrelationId} released at {WriteLockHeld.Format(ordered[i - 1].Released)}, {ordered[i].CorrelationId} acquired at {WriteLockHeld.Format(ordered[i].Acquired)}");
        }

        var pages = await PageIdsAsync(run.Gateway, run.SectionId);
        Assert.All(created, id => Assert.Contains(id, pages));
    }

    // --- Setup ----------------------------------------------------------------------------------------------------

    private sealed record Run(ComGateway Gateway, string NotebookId, string SectionId, string SettingsPath) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Gateway.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private async Task<Run> StartAsync(bool grant)
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
            var sectionId = await EnsureSectionAsync(gateway, notebook.NotebookId);
            if (grant)
            {
                // Keyed by MCP Test's live path, read back from GetHierarchy (AD-6), never by the folder the suite chose.
                var self = HierarchyNode.Root(await gateway.GetHierarchyAsync(notebook.NotebookId, CoreScope.Self, Ct));
                Assert.Equal(TestNotebook.NotebookName, self.Name);
                new SettingsWriter(store, TimeProvider.System).SaveWriteGrant(new NotebookGrant(
                    GrantKeys.NotebookKey(self.Path), TestNotebook.NotebookName, notebook.NotebookId, Write: true, PermanentDelete: false, DateTimeOffset.UtcNow, GrantedVia.Cli));
            }

            return new Run(gateway, notebook.NotebookId, sectionId, settingsPath);
        }
        catch
        {
            gateway.Dispose();
            throw;
        }
    }

    /// <summary><c>Create tests</c> directly in MCP Test, created through the guard when it's missing.</summary>
    private static async Task<string> EnsureSectionAsync(ComGateway gateway, string notebookId)
    {
        var tree = await gateway.GetHierarchyAsync(notebookId, CoreScope.Sections, Ct);
        return TestNotebookGuard.FindSectionId(tree, SectionName)
            ?? await TestNotebook.WriteAsync(
                gateway,
                notebookId,
                app =>
                {
                    app.OpenHierarchy(SectionName + ".one", notebookId, out var id, CreateFileType.cftSection);
                    return id;
                },
                "OpenHierarchy(Create tests section)",
                Ct);
    }

    private static CreatePageTool Tool(Run run)
    {
        var store = new SettingsStore(run.SettingsPath);
        var useCase = new CreatePageUseCase(
            run.Gateway,
            new WriteAccess(run.Gateway, store.Read),
            new WritePipeline(new WriteLock(), PageTextCache.WithDefaults(), store.Read));
        return new CreatePageTool(new ToolRunner(NullLogger<ToolRunner>.Instance), useCase);
    }

    private static async Task<List<string>> PageIdsAsync(ComGateway gateway, string sectionId) =>
        [.. XDocument.Parse(await gateway.GetHierarchyAsync(sectionId, CoreScope.Pages, Ct))
            .Descendants(One + "Page")
            .Where(p => (string?)p.Attribute("isInRecycleBin") != "true")
            .Select(p => (string)p.Attribute("ID")!)];

    // --- The two server processes ---------------------------------------------------------------------------------

    /// <summary>This configuration's build of the server, or <see cref="ExeOverrideVariable"/>.</summary>
    private static string ServerExe()
    {
        var overridden = Environment.GetEnvironmentVariable(ExeOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        var repoRoot = typeof(CreatePageIntegrationTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RepoRoot").Value!;
#if DEBUG
        const string Configuration = "Debug";
#else
        const string Configuration = "Release";
#endif
        return Path.Combine(repoRoot, "src", "OneNoteMcp.Server", "bin", Configuration, "net10.0-windows", "OneNoteMcp.exe");
    }

    private static async Task<McpClient> ClientAsync(string exe, string settingsPath, string logDir, ConcurrentQueue<string> stderr)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "onenote-test",
            Command = exe,
            Arguments = [],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                [SettingsStore.PathOverrideVariable] = settingsPath,
                [FileLog.DirectoryOverrideVariable] = logDir,
            },
            StandardErrorLines = stderr.Enqueue,
        });
        return await McpClient.CreateAsync(transport, cancellationToken: Ct);
    }

    private sealed record Hold(int Process, string CorrelationId, DateTime Acquired, DateTime Released, long WaitMs);

    [GeneratedRegex(@"write lock correlationId=(?<id>\S+) acquiredUtc=(?<acquired>\S+) releasedUtc=(?<released>\S+) waitMs=(?<wait>\d+)")]
    private static partial Regex HoldLine();

    /// <summary>Every <c>write lock</c> line from both servers' stderr, waiting up to 15 s for <paramref name="expected"/> of them.</summary>
    private static async Task<List<Hold>> HoldsAsync(ConcurrentQueue<string>[] stderr, int expected)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var holds = stderr
                .SelectMany((lines, process) => lines.Select(line => (process, match: HoldLine().Match(line))))
                .Where(x => x.match.Success)
                .Select(x => new Hold(
                    x.process,
                    x.match.Groups["id"].Value,
                    ParseUtc(x.match.Groups["acquired"].Value),
                    ParseUtc(x.match.Groups["released"].Value),
                    long.Parse(x.match.Groups["wait"].Value, CultureInfo.InvariantCulture)))
                .ToList();
            if (holds.Count >= expected || stopwatch.Elapsed > TimeSpan.FromSeconds(15))
            {
                return holds;
            }

            await Task.Delay(100, Ct);
        }
    }

    private static DateTime ParseUtc(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    // --- Clean-up (Q6) --------------------------------------------------------------------------------------------

    /// <summary>
    /// Soft-deletes (<c>DeleteHierarchy</c>, <c>deletePermanently: false</c>) only the pages this run's create_page calls
    /// returned. Before each delete, a fresh <c>GetHierarchy</c> of MCP Test must show the ID as a <c>one:Page</c> inside
    /// it, not in its recycle bin; <see cref="TestNotebookGuard.Contains"/> alone would also accept the notebook or a
    /// section. The delete passes that read's <c>lastModifiedTime</c>. Every outcome is logged as evidence for epic 5:
    /// the HRESULT, whether the old ID still resolves, and which pages are new in MCP Test's recycle bin. Nothing here
    /// fails the test.
    /// </summary>
    private async Task CleanUpAsync(Run run)
    {
        foreach (var pageId in created.Distinct(StringComparer.Ordinal).ToList())
        {
            try
            {
                var binBefore = await RecycleBinPageIdsAsync(run);
                var tree = XDocument.Parse(await run.Gateway.GetHierarchyAsync(run.NotebookId, CoreScope.Pages, Ct));
                var page = tree.Root is { } root && root.Name == One + "Notebook" && (string?)root.Attribute("ID") == run.NotebookId
                    ? root.Descendants(One + "Page").SingleOrDefault(p => (string?)p.Attribute("ID") == pageId)
                    : null;
                if (page is null || (string?)page.Attribute("isInRecycleBin") == "true")
                {
                    output.WriteLine($"clean-up skipped {pageId}: not a live page inside MCP Test");
                    continue;
                }

                var lastModified = ParseUtc((string)page.Attribute("lastModifiedTime")!);
                var hresult = 0;
                try
                {
                    await TestNotebook.WriteAsync(
                        run.Gateway,
                        pageId,
                        app =>
                        {
                            app.DeleteHierarchy(pageId, lastModified, deletePermanently: false);
                            return true;
                        },
                        "DeleteHierarchy(created page, soft)",
                        Ct);
                }
                catch (OneNoteException ex)
                {
                    hresult = ex.ComHResult ?? -1;
                    output.WriteLine($"clean-up of {pageId} failed: {ex.Code} {Hex(ex.ComHResult)}");
                    continue;
                }

                var oldIdResolves = await ResolvesAsync(run.Gateway, pageId);
                var binAfter = await RecycleBinPageIdsAsync(run);
                var newInBin = binAfter.Except(binBefore, StringComparer.Ordinal).ToList();
                output.WriteLine(
                    $"soft delete of {pageId}: hresult={Hex(hresult)}, old ID resolves={oldIdResolves.Resolves} (isInRecycleBin={oldIdResolves.InRecycleBin}), "
                    + $"new in the recycle bin: [{string.Join(", ", newInBin)}]");
            }
#pragma warning disable CA1031 // A failed clean-up is logged and never fails the test (Q6).
            catch (Exception ex)
#pragma warning restore CA1031
            {
                output.WriteLine($"clean-up of {pageId} failed: {ex.GetType().Name}");
            }
        }
    }

    private static async Task<(bool Resolves, bool InRecycleBin)> ResolvesAsync(ComGateway gateway, string id)
    {
        try
        {
            var node = XDocument.Parse(await gateway.GetHierarchyAsync(id, CoreScope.Self, Ct)).Root;
            return (true, (string?)node?.Attribute("isInRecycleBin") == "true");
        }
        catch (OneNoteException)
        {
            return (false, false);
        }
    }

    private static async Task<HashSet<string>> RecycleBinPageIdsAsync(Run run) =>
        [.. XDocument.Parse(await run.Gateway.GetHierarchyAsync(run.NotebookId, CoreScope.Pages, Ct))
            .Descendants(One + "Page")
            .Where(p => (string?)p.Attribute("isInRecycleBin") == "true")
            .Select(p => (string)p.Attribute("ID")!)];

    private static string Hex(int? hresult) =>
        hresult is { } h ? "0x" + h.ToString("X8", CultureInfo.InvariantCulture) : "-";
}
