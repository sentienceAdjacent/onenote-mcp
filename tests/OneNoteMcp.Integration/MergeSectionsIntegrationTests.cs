using System.Diagnostics;
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
using OneNoteMcp.Integration.WriteProbes;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 3.6's live checks for sitting S1, opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c> and never run in a build:
/// <c>dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~MergeSectionsIntegrationTests</c>, with MCP Test
/// and the throwaway <c>MCP Test Throwaway 1112-034716 a</c> open and the Ballast Vault unlocked at the start.
/// <list type="bullet">
/// <item>Every merge goes through the tool, over a gateway whose <c>MergeSections</c> is refused unless the source was made
/// by this run and the destination was too, or sits in the adopted throwaway; every other write the tool could make is
/// refused. The run makes its own sections (stamped names) and pages, and soft-deletes the sections at the end; a failed
/// clean-up is logged and never fails the test.</item>
/// <item>Each case runs three times with fresh run sections. A <c>0x8004201F</c> is asserted as <c>Internal</c> with
/// <c>outcomeUnknown</c> and the re-read's findings, and counted, never retried. The pass rule is the plan's Q5 (a): at
/// least one clean merge per case.</item>
/// <item>Grants and the recovery journal live in a temporary folder, never the user's settings or journal.</item>
/// <item>The Vault check reads the Vault's ID while it is unlocked, then pauses (the probes' pause file) for Phill to lock it
/// with Ctrl+Alt+L; then both refusals run, and no <c>MergeSections</c> is sent.</item>
/// </list>
/// The output carries IDs, codes, counts and the run's own stamped names only.
/// </summary>
public sealed class MergeSectionsIntegrationTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>The local throwaway the cross-notebook merge adopts by its exact name (spike 3.1's, still open).</summary>
    private const string ThrowawayName = "MCP Test Throwaway 1112-034716 a";

    /// <summary>Each case's runs, each with fresh run sections (the plan's Q5).</summary>
    private const int Runs = 3;

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    /// <summary>What this run made, in order; only these may be written to or deleted.</summary>
    private readonly List<string> created = [];

    /// <summary>The adopted throwaway, by ID, with its name: the guard accepts a merge into a section there.</summary>
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
    /// S1.1, in MCP Test, three runs: a source with a page, its subpage and another page merges into a one-page destination.
    /// A clean run puts the copies at positions 2 to 4 under new IDs (<c>meta.pageIds</c>), the subpage's copy at level 2,
    /// leaves the source untouched, and takes one journal entry of the destination.
    /// </summary>
    [Fact]
    public async Task SameNotebook_ThreeRuns_AtLeastOneCleanMerge()
    {
        await using var run = await StartAsync();
        var outcomes = new List<string>();
        try
        {
            for (var i = 1; i <= Runs; i++)
            {
                outcomes.Add(await MergeOnceAsync(run, run.NotebookId, $"Probe 3.6 local {Stamp} r{i}"));
            }
        }
        finally
        {
            await CleanUpAsync(run);
        }

        output.WriteLine($"RESULT same notebook: {string.Join(", ", outcomes)}");
        Assert.Contains("clean", outcomes);
    }

    /// <summary>
    /// S1.2 and S1.3, to a run section in the adopted throwaway (skipped when it isn't open): three runs as S1.1; then, with
    /// the throwaway's write grant removed, a merge into it is <c>WriteNotGranted</c> and nothing is sent.
    /// </summary>
    [Fact]
    public async Task OtherNotebook_ToTheThrowaway_ThreeRuns_AtLeastOneClean_ThenWithoutItsGrant_IsWriteNotGranted()
    {
        await using var run = await StartAsync();
        var throwaway = await TestNotebook.AdoptThrowawayNotebookAsync(run.Com, ThrowawayName, Ct);
        Assert.SkipWhen(throwaway is null, $"Open the throwaway notebook \"{ThrowawayName}\" in OneNote first.");
        throwaways[throwaway!] = new HashSet<string>([ThrowawayName]);
        var throwawayKey = GrantKeys.NotebookKey(HierarchyNode.Root(await run.Com.GetHierarchyAsync(throwaway!, CoreScope.Self, Ct)).Path);
        var writer = new SettingsWriter(run.Store, TimeProvider.System);
        writer.SaveWriteGrant(new NotebookGrant(throwawayKey, ThrowawayName, throwaway!, Write: true, PermanentDelete: false, DateTimeOffset.UtcNow, GrantedVia.Cli));
        var outcomes = new List<string>();
        try
        {
            for (var i = 1; i <= Runs; i++)
            {
                outcomes.Add(await MergeOnceAsync(run, throwaway!, $"Probe 3.6 cross {Stamp} r{i}"));
            }

            // S1.3: the throwaway's grant removed, the merge is refused before anything is sent.
            var stamp = Stamp;
            var source = await CreateSectionAsync(run, run.NotebookId, $"Probe 3.6 ungranted {stamp}");
            await CreatePageAsync(run, source);
            var destination = await CreateSectionAsync(run, throwaway!, $"Probe 3.6 ungranted landing {stamp}");
            Assert.Equal(1, writer.RevokeWriteGrant(throwawayKey));
            var calls = run.Gateway.MergeSectionsCalls;
            var refused = await Tool(run).MergeSections(source, destination, null, Ct);
            Assert.Equal("WriteNotGranted", Code(refused));
            Assert.Equal(calls, run.Gateway.MergeSectionsCalls);
            output.WriteLine("RESULT not granted: WriteNotGranted, nothing sent");
        }
        finally
        {
            await CleanUpAsync(run);
        }

        output.WriteLine($"RESULT other notebook: {string.Join(", ", outcomes)}");
        Assert.Contains("clean", outcomes);
    }

    /// <summary>
    /// S1.4: the Ballast Vault, read unlocked and then locked by Phill at the pause: merging it into a run section is
    /// <c>NotSupported</c>, and merging a run section into it is <c>SectionLocked</c>. No <c>MergeSections</c> is sent.
    /// </summary>
    [Fact]
    public async Task TheBallastVault_Locked_AMergeOfItIsNotSupported_AMergeIntoItIsSectionLocked()
    {
        await using var run = await StartAsync();
        var children = XDocument.Parse(await run.Gateway.GetHierarchyAsync(run.NotebookId, CoreScope.Children, Ct));
        var vault = children.Root!.Elements(One + "Section").FirstOrDefault(s => (string?)s.Attribute("name") == BallastContent.VaultSectionName);
        Assert.SkipWhen(vault is null, "MCP Test has no Ballast Vault: run the Ballast scenario (story 17).");
        Assert.SkipUnless((string?)vault!.Attribute("encrypted") is "true" or "1", "The Ballast Vault isn't password-protected yet (story 17's owner step).");
        var vaultId = (string)vault.Attribute("ID")!;
        var stamp = Stamp;
        try
        {
            var section = await CreateSectionAsync(run, run.NotebookId, $"Probe 3.6 vault {stamp}");
            await CreatePageAsync(run, section);

            if ((string?)vault.Attribute("locked") is not ("true" or "1"))
            {
                Assert.True(
                    await PauseAsync("vault-lock-3.6", "In OneNote, press Ctrl+Alt+L to lock the password-protected sections (the Ballast Vault in MCP Test). Then continue."),
                    "The Vault must be locked for this check.");
            }

            var now = XDocument.Parse(await run.Gateway.GetHierarchyAsync(vaultId, CoreScope.Self, Ct)).Root!;
            Assert.True((string?)now.Attribute("locked") is "true" or "1", "The Ballast Vault still reads as unlocked.");

            var outOf = await Tool(run).MergeSections(vaultId, section, null, Ct);
            Assert.Equal("NotSupported", Code(outOf));
            Assert.Equal(MergeSectionsWrite.ProtectedUserAction, outOf.StructuredContent!.Value.GetProperty("error").GetProperty("userAction").GetString());
            Assert.DoesNotContain(BallastContent.VaultSectionName, outOf.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);

            Assert.Equal("SectionLocked", Code(await Tool(run).MergeSections(section, vaultId, null, Ct)));
            Assert.Equal(0, run.Gateway.MergeSectionsCalls);
            output.WriteLine("RESULT vault: a merge of it is NotSupported, a merge into it is SectionLocked; nothing sent");
        }
        finally
        {
            await CleanUpAsync(run);
        }
    }

    /// <summary>
    /// One run of S1.1 or S1.2: fresh run sections under <paramref name="destinationParent"/> (the destination) and in MCP
    /// Test (the source), then one merge. Returns <c>clean</c>, or <c>mergeFailed</c> for a <c>0x8004201F</c> that the tool
    /// reported as the plan says; anything else fails the test. Never retried.
    /// </summary>
    private async Task<string> MergeOnceAsync(Run run, string destinationParent, string name)
    {
        var source = await CreateSectionAsync(run, run.NotebookId, $"{name} source");
        var destination = await CreateSectionAsync(run, destinationParent, $"{name} destination");
        var page = await CreatePageAsync(run, source);
        var subpage = await CreatePageAsync(run, source);
        var last = await CreatePageAsync(run, source);
        var resident = await CreatePageAsync(run, destination);

        // The subpage through the guarded raw UpdateHierarchy (the merge tool never changes pageLevel), as 3.4's check does.
        var level = $"<one:Section xmlns:one=\"{OneNoteXml.Namespace2013}\" ID=\"{source}\"><one:Page ID=\"{page}\" /><one:Page ID=\"{subpage}\" pageLevel=\"2\" /><one:Page ID=\"{last}\" /></one:Section>";
        await TestNotebook.UpdateHierarchyAsync(run.Com, level, created.ToHashSet(StringComparer.Ordinal), throwaways, "UpdateHierarchy(3.6 subpage level)", Ct);
        Assert.Equal(2, HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(source, CoreScope.Children, Ct)).Children.Single(c => c.Id == subpage).PageLevel);

        // AD-17: a node's date can move about a second after a hierarchy write; let it settle before reading the basis.
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        var calls = run.Gateway.MergeSectionsCalls;
        var entriesBefore = EntryCount(run);
        var result = await Tool(run).MergeSections(source, destination, await NodeBasisAsync(run, source), Ct);
        Assert.Equal(calls + 1, run.Gateway.MergeSectionsCalls);
        var root = result.StructuredContent!.Value;

        if (result.IsError == true)
        {
            // AD-7 Merges: hrMergeFailed is Internal with outcomeUnknown and the re-read's findings; counted, never retried.
            var error = root.GetProperty("error");
            Assert.Equal("Internal", error.GetProperty("code").GetString());
            Assert.Equal("0x8004201F", error.GetProperty("hresult").GetString());
            Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
            Assert.Contains("Re-read straight after the failure", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
            var after = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(destination, CoreScope.Children, Ct));
            output.WriteLine($"RESULT {name}: 0x8004201F, outcomeUnknown; destination {destination} now lists {after.Children.Count} page(s)");
            return "mergeFailed";
        }

        var meta = root.GetProperty("meta");
        Assert.Equal(source, meta.GetProperty("objectId").GetString());
        Assert.Equal(destination, meta.GetProperty("parentId").GetString());
        var copies = meta.GetProperty("pageIds").EnumerateArray().Select(e => e.GetString()!).ToList();
        created.AddRange(copies);
        Assert.Equal(3, copies.Count);
        Assert.DoesNotContain(copies, c => c == page || c == subpage || c == last);

        var landed = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(destination, CoreScope.Children, Ct));
        Assert.Equal([resident, .. copies], landed.Children.Select(c => c.Id));
        Assert.Equal([1, 1, 2, 1], landed.Children.Select(c => c.PageLevel));
        var kept = HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(source, CoreScope.Children, Ct));
        Assert.Equal([page, subpage, last], kept.Children.Select(c => c.Id));

        var entry = JournalStore.Read(JournalFiles.Find(run.JournalFolder, Guid.Parse(meta.GetProperty("journalEntryId").GetString()!))!);
        Assert.Equal(destination, entry.ObjectId);
        Assert.Equal(JournalEntry.HierarchyKind, entry.SnapshotKind);
        Assert.Equal(entriesBefore + 1, EntryCount(run)); // one entry, the destination's (S1.1)
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
        output.WriteLine($"RESULT {name}: clean; copies {string.Join(", ", copies)} at positions 2-4 of {destination}; source {source} untouched");
        return "clean";
    }

    private static int EntryCount(Run run) =>
        Directory.Exists(run.JournalFolder)
            ? Directory.GetFiles(run.JournalFolder).Count(f => JournalFiles.IsEntry(Path.GetFileName(f)))
            : 0;

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

    /// <summary>The tool over <see cref="GuardedGateway"/>, so every <c>MergeSections</c> passes the run's guard. No elicitation.</summary>
    private static MergeSectionsTool Tool(Run run)
    {
        var services = new HierarchyWriteServices(
            run.Gateway,
            new WriteAccess(run.Gateway, run.Store.Read),
            new ReadAccess(run.Gateway, run.Store.Read, new SettingsWriter(run.Store, TimeProvider.System), TimeProvider.System),
            new HierarchySnapshot(run.Gateway, new JournalStore(run.Store.Read)));
        return new MergeSectionsTool(
            new ToolRunner(NullLogger<ToolRunner>.Instance),
            new MergeSectionsUseCase(services, new WritePipeline(new WriteLock(), run.Gateway, PageTextCache.WithDefaults(), run.Store.Read)));
    }

    /// <summary>A section under <paramref name="parentId"/> through the guarded raw write (test or adopted throwaway notebook only), recorded as this run's.</summary>
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
            "OpenHierarchy(3.6 probe section)",
            Ct,
            throwaways);
        created.Add(id);
        return id;
    }

    /// <summary>A page in a section this run made, after the guard confirms the section is in a test or adopted notebook.</summary>
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
            "CreateNewPage(3.6 probe page)",
            Ct,
            throwaways);
        created.Add(id);
        return id;
    }

    /// <summary>A section's own hierarchy <c>lastModifiedTime</c>, read at <c>self</c>, as the merge's basis (AD-17).</summary>
    private static async Task<PageBasisArgument> NodeBasisAsync(Run run, string id) =>
        new(HierarchyTarget.Read(await run.Gateway.GetHierarchyAsync(id, CoreScope.Self, Ct)).LastModifiedTime);

    /// <summary>
    /// The probes' hitl pause (<see cref="ProbeSession.PauseAsync"/>'s file and protocol): writes the instruction to the
    /// pause file and waits until the main session deletes it (true) or writes <c>skip</c> into it (false).
    /// </summary>
    private async Task<bool> PauseAsync(string step, string instruction)
    {
        Directory.CreateDirectory(ProbeRecorder.ProbesDir);
        var file = Path.Combine(ProbeRecorder.ProbesDir, ProbeSession.PauseFileName);
        await File.WriteAllTextAsync(file, $"[merge / {step}]\n{instruction}\n\nWhen done, delete this file. To skip this step, replace this file's content with: skip\n", Ct);
        output.WriteLine($"PAUSE ({file}): {instruction}");
        Console.WriteLine($"PAUSE [merge / {step}]: {instruction}");

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

    private static string? Code(CallToolResult result) =>
        result.IsError == true ? result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() : null;

    /// <summary>
    /// Soft-deletes the sections this run made, last first, through the guarded delete; the pages and copies go with them.
    /// A failure is logged only.
    /// </summary>
    private async Task CleanUpAsync(Run run)
    {
        var ours = created.ToHashSet(StringComparer.Ordinal);
        foreach (var id in Enumerable.Reverse(created).ToList())
        {
            try
            {
                var self = XDocument.Parse(await run.Com.GetHierarchyAsync(id, CoreScope.Self, Ct)).Root!;
                if ((self.Name != One + "Section" && self.Name != One + "SectionGroup") || (string?)self.Attribute("isInRecycleBin") == "true")
                {
                    continue;
                }

                var date = OneNoteDates.ToUtc((string?)self.Attribute("lastModifiedTime")) ?? DateTime.MinValue;
                await TestNotebook.DeleteHierarchyAsync(run.Com, id, date, permanently: false, ours, throwaways, Ct);
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
    /// The real gateway, except that <c>MergeSections</c> is refused unless the source was made by this run and the
    /// destination was too or sits in an adopted throwaway, and is counted; every other write is refused. Reads go straight
    /// through.
    /// </summary>
    private sealed class GuardedGateway(ComGateway inner, List<string> created, IReadOnlyDictionary<string, IReadOnlySet<string>> throwaways) : IOneNoteGateway
    {
        private int mergeSectionsCalls;

        /// <summary>How many <c>MergeSections</c> calls the tool sent (each passed the guard first).</summary>
        public int MergeSectionsCalls => Volatile.Read(ref mergeSectionsCalls);

        public async Task MergeSectionsAsync(string sourceId, string destinationId, CancellationToken ct)
        {
            Interlocked.Increment(ref mergeSectionsCalls);
            if (!created.Contains(sourceId))
            {
                throw new InvalidOperationException($"Refused: MergeSections source {sourceId} wasn't made by this run.");
            }

            if (!created.Contains(destinationId) && !await InThrowawayAsync(destinationId, ct))
            {
                throw new InvalidOperationException($"Refused: MergeSections destination {destinationId} wasn't made by this run and isn't in an adopted throwaway.");
            }

            await inner.MergeSectionsAsync(sourceId, destinationId, ct);
        }

        /// <summary>True when the walk from <paramref name="id"/> reaches an adopted throwaway notebook.</summary>
        private async Task<bool> InThrowawayAsync(string id, CancellationToken ct)
        {
            var current = id;
            for (var depth = 0; depth < 64; depth++)
            {
                var parent = await inner.GetHierarchyParentAsync(current, ct);
                if (string.IsNullOrEmpty(parent))
                {
                    return false;
                }

                if (throwaways.ContainsKey(parent))
                {
                    return true;
                }

                current = parent;
            }

            return false;
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
            Task.FromException(new InvalidOperationException($"The merge tool never calls {method}."));

        private static Task<T> Refused<T>(string method) =>
            Task.FromException<T>(new InvalidOperationException($"The merge tool never calls {method}."));
    }
}
