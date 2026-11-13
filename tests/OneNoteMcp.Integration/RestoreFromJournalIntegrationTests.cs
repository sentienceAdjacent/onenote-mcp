using System.Globalization;
using System.Xml.Linq;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Integration.WriteProbes;
using OneNoteMcp.Xml;
using Xunit;
using CorePageInfo = OneNoteMcp.Core.PageInfo;
using CoreScope = OneNoteMcp.Core.HierarchyScope;
using InteropNewPageStyle = OneNoteMcp.Com.Interop.NewPageStyle;

namespace OneNoteMcp.Integration;

/// <summary>
/// Opt-in (story 2.11): <c>restore_from_journal</c>'s use case through the real <see cref="ComGateway"/> and the write
/// pipeline, on a fresh page in MCP Test's <c>Seed</c> section, with a temporary <c>settings.json</c> that grants writes to
/// MCP Test only and a temporary journal folder. The page gets an Outline and an image, <c>update_page_content</c> adds
/// another Outline (its snapshot is the entry), and the entry is restored: the new page holds the earlier Outline and the
/// image, and not the later one. Live sitting only; never run by the build.
/// </summary>
public sealed class RestoreFromJournalIntegrationTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>A 1×1 transparent PNG.</summary>
    internal const string PngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateThenRestore_MakesANewPage_WithTheEarlierOutlineAndImage()
    {
        var rig = await RestoreRig.StartAsync(tempDir, Ct);
        using var _ = rig.Gateway;
        var notebook = await TestNotebook.EnsureAsync(rig.Gateway, Ct);
        var pageId = await FreshPageAsync(rig.Gateway, notebook.SectionId, "story 2.11 page", output, Ct);
        var marker = "story 2.11 " + Guid.NewGuid().ToString("N");

        // The earlier version: one Outline with a text OE and an image OE, written through the guarded raw seam.
        var earlier = ProbeXml.Page(pageId, ProbeXml.Outline(null, ProbeXml.Oe(null, marker), ProbeXml.OeRaw(null, $"<one:Image format=\"png\"><one:Data>{PngBase64}</one:Data></one:Image>")));
        await TestNotebook.WriteAsync(
            rig.Gateway,
            pageId,
            app =>
            {
                app.UpdatePageContent(earlier, DateTime.MinValue, Com.Interop.XMLSchema.xs2013, false);
                return true;
            },
            "UpdatePageContent(story 2.11 earlier)",
            Ct);

        // update_page_content adds a later Outline; its step 6 snapshot is the entry.
        var later = "later " + marker;
        var basic = await rig.Gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct);
        var basis = PageBasis.From(basic);
        await TestNotebook.EnsureTestTargetAsync(rig.Gateway, pageId, "UpdatePageContent", Ct);
        var updated = await rig.Update.ExecuteAsync(
            new ToolCallContext("it-2.11-update"),
            pageId,
            ProbeXml.Page(pageId, ProbeXml.Outline(null, ProbeXml.Oe(null, later))),
            new PageWriteBasis(basis.LastModifiedTime, basis.PageHash, basis.ElementHashes),
            null,
            Ct);
        var entryId = updated.Meta.JournalEntryId;
        Assert.NotNull(entryId);
        output.WriteLine($"entry {entryId}");

        // Restore: the target is the entry's own section in MCP Test, the only notebook the settings grant.
        await TestNotebook.EnsureTestTargetAsync(rig.Gateway, notebook.SectionId, "restore_from_journal", Ct);
        var restored = await rig.Restore.ExecuteAsync(new ToolCallContext("it-2.11-restore"), entryId, Ct);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"restored page {restored.Meta.PageId} in {restored.Meta.ParentId}; warnings=[{string.Join(", ", restored.Warnings.Select(w => w.Code))}]"));

        Assert.NotEqual(pageId, restored.Meta.PageId);
        Assert.Equal(notebook.SectionId, restored.Meta.ParentId);
        var page = XDocument.Parse(await rig.Gateway.GetPageContentAsync(restored.Meta.PageId!, CorePageInfo.BinaryData, Ct)).Root!;
        Assert.Contains(page.Descendants(One + "T"), t => t.Value == marker);
        Assert.DoesNotContain(page.Descendants(One + "T"), t => t.Value == later);
        Assert.Contains(page.Descendants(One + "Image"), i => i.Element(One + "Data") is not null);

        // The original page still holds both Outlines: restore never overwrites.
        var original = XDocument.Parse(await rig.Gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct)).Root!;
        Assert.Contains(original.Descendants(One + "T"), t => t.Value == later);
    }

    /// <summary>A new page in <paramref name="sectionId"/> of MCP Test, created through the guarded write. It stays there afterwards.</summary>
    internal static async Task<string> FreshPageAsync(ComGateway gateway, string sectionId, string what, ITestOutputHelper output, CancellationToken ct)
    {
        var pageId = await TestNotebook.WriteAsync(
            gateway,
            sectionId,
            app =>
            {
                app.CreateNewPage(sectionId, out var id, InteropNewPageStyle.npsBlankPageWithTitle);
                return id;
            },
            $"CreateNewPage({what})",
            ct);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"page {pageId}"));
        return pageId;
    }
}

/// <summary>
/// The opt-in story 2.11 tests' setup: a real <see cref="ComGateway"/>, a temporary <c>settings.json</c> granting writes
/// to MCP Test only, a temporary journal folder, and the <c>update_page_content</c> and <c>restore_from_journal</c> use
/// cases over one write pipeline.
/// </summary>
internal sealed record RestoreRig(ComGateway Gateway, UpdatePageContentUseCase Update, RestoreFromJournalUseCase Restore, string JournalFolder)
{
    public static async Task<RestoreRig> StartAsync(string tempDir, CancellationToken ct)
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");
        var journal = Path.Combine(tempDir, "journal");
        File.WriteAllText(settingsPath, """{ "schemaVersion": 1 }""");
        var store = new SettingsStore(settingsPath, journal);
        var gateway = new ComGateway(new ProcessProbe(), store);
        try
        {
            var notebook = await TestNotebook.EnsureAsync(gateway, ct);

            // Keyed by MCP Test's live path, read back from GetHierarchy (AD-6).
            var self = HierarchyNode.Root(await gateway.GetHierarchyAsync(notebook.NotebookId, CoreScope.Self, ct));
            Assert.Equal(TestNotebook.NotebookName, self.Name);
            new SettingsWriter(store, TimeProvider.System).SaveWriteGrant(new NotebookGrant(
                GrantKeys.NotebookKey(self.Path), TestNotebook.NotebookName, notebook.NotebookId, Write: true, PermanentDelete: false, DateTimeOffset.UtcNow, GrantedVia.Cli));

            var access = new WriteAccess(gateway, store.Read);
            var pipeline = new WritePipeline(new WriteLock(), gateway, PageTextCache.WithDefaults(), store.Read);
            var services = new PageWriteServices(
                gateway,
                access,
                new ReadAccess(gateway, store.Read, writer: null, TimeProvider.System),
                new PageSnapshot(gateway, new JournalStore(store.Read)));
            return new RestoreRig(
                gateway,
                new UpdatePageContentUseCase(services, pipeline),
                new RestoreFromJournalUseCase(gateway, access, pipeline, store.Read),
                journal);
        }
        catch
        {
            gateway.Dispose();
            throw;
        }
    }
}
