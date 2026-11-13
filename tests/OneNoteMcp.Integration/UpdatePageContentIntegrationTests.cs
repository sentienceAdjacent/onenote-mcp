using System.Globalization;
using System.Xml.Linq;
using OneNoteMcp.Com;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Integration.WriteProbes;
using OneNoteMcp.Xml;
using Xunit;
using CorePageInfo = OneNoteMcp.Core.PageInfo;
using CoreScope = OneNoteMcp.Core.HierarchyScope;
using InteropNewPageStyle = OneNoteMcp.Com.Interop.NewPageStyle;
using InteropPageInfo = OneNoteMcp.Com.Interop.PageInfo;

namespace OneNoteMcp.Integration;

/// <summary>
/// Opt-in (story 2.9): <c>update_page_content</c>'s use case through the real <see cref="ComGateway"/> and the write
/// pipeline, on fresh pages in MCP Test's <c>Seed</c> section, with a temporary <c>settings.json</c> that grants writes
/// to MCP Test only and a temporary journal folder. Every write runs after
/// <see cref="TestNotebook.EnsureTestTargetAsync"/> shows its page inside MCP Test. Live sitting only; never run by the
/// build.
/// </summary>
public sealed class UpdatePageContentIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private static readonly XNamespace One = OneNoteXml.Namespace2013;
    private static readonly XNamespace One2026 = "http://schemas.microsoft.com/office/onenote/2026/onenote";

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string JournalFolder => Path.Combine(tempDir, "journal");

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task FreshEdit_Lands_AndItsBasis_CarriesTheNextEdit_WithOneJournalEntryPerWrite()
    {
        var (gateway, useCase) = await StartAsync();
        using var _ = gateway;
        var pageId = await FreshPageAsync(gateway);
        var marker = "story 2.9 " + Guid.NewGuid().ToString("N");

        // 1. Add an Outline, based on a fresh read.
        var basis = Basis(await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct));
        await TestNotebook.EnsureTestTargetAsync(gateway, pageId, "UpdatePageContent", Ct);
        var added = await useCase.ExecuteAsync(new ToolCallContext("it-add"), pageId, ProbeXml.Page(pageId, ProbeXml.Outline(null, ProbeXml.Oe(null, marker))), basis, null, Ct);
        output.WriteLine($"add: warnings=[{string.Join(", ", added.Warnings.Select(w => w.Code))}]");
        Assert.NotNull(added.Meta.Basis);

        // 2. Edit its OE with the basis the first write returned (Q2), sending the whole Outline back.
        var page = XDocument.Parse(await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct)).Root!;
        var outline = page.Elements(One + "Outline").Single(o => o.Descendants(One + "T").Any(t => t.Value == marker));
        var oe = outline.Descendants(One + "OE").Single();
        var edited = marker + " edited";
        var sent = ProbeXml.Page(pageId, ProbeXml.Outline(ProbeXml.ObjectId(outline), ProbeXml.Oe(ProbeXml.ObjectId(oe), edited)));
        await TestNotebook.EnsureTestTargetAsync(gateway, pageId, "UpdatePageContent", Ct);
        var second = await useCase.ExecuteAsync(new ToolCallContext("it-edit"), pageId, sent, ToCore(added.Meta.Basis!), null, Ct);
        output.WriteLine($"edit: warnings=[{string.Join(", ", second.Warnings.Select(w => w.Code))}]");

        var after = XDocument.Parse(await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct));
        Assert.Contains(after.Descendants(One + "T"), t => t.Value == edited);
        Assert.Equal(2, Directory.GetFiles(JournalFolder).Length);
    }

    [Fact]
    public async Task StaleBasis_OnAPageThatIsntDisplayed_IsStaleWrite()
    {
        // Spike 2.2 constraint 4: on a page that isn't displayed, lastModifiedTime doesn't advance, so the hash catches it.
        var (gateway, useCase) = await StartAsync();
        using var _ = gateway;
        var pageId = await FreshPageAsync(gateway);
        await TestNotebook.EnsureTestTargetAsync(gateway, pageId, "UpdatePageContent", Ct);
        await gateway.UpdatePageContentAsync(ProbeXml.Page(pageId, ProbeXml.Outline(null, ProbeXml.Oe(null, "first"))), LastModified(await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct)), Ct);

        var read = await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct);
        var basis = Basis(read);
        var outline = XDocument.Parse(read).Root!.Elements(One + "Outline").Single();
        var oe = outline.Descendants(One + "OE").Single();

        // Another writer changes the Outline after the caller's read.
        await TestNotebook.EnsureTestTargetAsync(gateway, pageId, "UpdatePageContent", Ct);
        await gateway.UpdatePageContentAsync(
            ProbeXml.Page(pageId, ProbeXml.Outline(ProbeXml.ObjectId(outline), ProbeXml.Oe(ProbeXml.ObjectId(oe), "second writer"))),
            LastModified(read),
            Ct);
        output.WriteLine($"date before={LastModified(read)} after={LastModified(await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct))}");

        await TestNotebook.EnsureTestTargetAsync(gateway, pageId, "UpdatePageContent", Ct);
        var ex = await Assert.ThrowsAsync<OneNoteException>(() => useCase.ExecuteAsync(
            new ToolCallContext("it-stale"),
            pageId,
            ProbeXml.Page(pageId, ProbeXml.Outline(ProbeXml.ObjectId(outline), ProbeXml.Oe(ProbeXml.ObjectId(oe), "stale caller"))),
            basis,
            null,
            Ct));

        Assert.Equal(ErrorCode.StaleWrite, ex.Code);
        Assert.False(Directory.Exists(JournalFolder) && Directory.EnumerateFiles(JournalFolder).Any());
    }

    [Fact]
    public async Task MergedTableProbe_ACellEditedThroughThePipeline_RecordsWhetherTheMergeSurvives()
    {
        // Q6 / spine Deferred "Editing inside a merged table in xs2013": a merged table written through the raw seam in
        // xs2026, one cell edited through the pipeline in xs2013, then re-read in xs2026. If the merge is lost, a
        // follow-up amends AD-4. This records the outcome; it asserts only that the edit landed.
        var (gateway, useCase) = await StartAsync();
        using var _ = gateway;
        var pageId = await FreshPageAsync(gateway);
        var marker = "merged " + Guid.NewGuid().ToString("N");
        var table =
            "<one:Table bordersVisible=\"true\"><one:Columns><one:Column index=\"0\" width=\"80\" /><one:Column index=\"1\" width=\"80\" /></one:Columns>"
            + $"<one:Row><one:Cell colSpan=\"2\"><one:OEChildren>{ProbeXml.Oe(null, marker)}</one:OEChildren></one:Cell></one:Row>"
            + $"<one:Row><one:Cell><one:OEChildren>{ProbeXml.Oe(null, "below left")}</one:OEChildren></one:Cell><one:Cell><one:OEChildren>{ProbeXml.Oe(null, "below right")}</one:OEChildren></one:Cell></one:Row>"
            + "</one:Table>";
        await TestNotebook.WriteAsync(
            gateway,
            pageId,
            app =>
            {
                app.UpdatePageContent(ProbeXml.Page(pageId, ProbeXml.Outline(null, ProbeXml.OeRaw(null, table)), One2026.NamespaceName), DateTime.MinValue, XMLSchema.xs2026, false);
                return true;
            },
            "UpdatePageContent(xs2026 merged table)",
            Ct);
        output.WriteLine($"before: {Spans(await Read2026Async(gateway, pageId))}");

        // Edit the merged cell's text through the pipeline: the whole Outline as read in xs2013, one OE's text changed.
        var read = await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct);
        var page = XDocument.Parse(read).Root!;
        var outline = page.Elements(One + "Outline").Single(o => o.Descendants(One + "T").Any(t => t.Value == marker));
        outline.Descendants(One + "T").Single(t => t.Value == marker).ReplaceNodes(new XCData(marker + " edited"));
        var sent = ProbeXml.Page(pageId, outline.ToString(SaveOptions.DisableFormatting).Replace($" xmlns:one=\"{One.NamespaceName}\"", string.Empty, StringComparison.Ordinal));
        await TestNotebook.EnsureTestTargetAsync(gateway, pageId, "UpdatePageContent", Ct);
        var result = await useCase.ExecuteAsync(new ToolCallContext("it-merged"), pageId, sent, Basis(read), null, Ct);
        output.WriteLine($"edit: warnings=[{string.Join("; ", result.Warnings.Select(w => w.Code + ": " + w.Message))}]");

        var after = await Read2026Async(gateway, pageId);
        output.WriteLine($"after: {Spans(after)}");
        output.WriteLine(after.Descendants(One2026 + "Cell").Any(c => c.Attribute("colSpan") is not null)
            ? "RESULT merge kept: AD-4 needs no amendment."
            : "RESULT merge lost: raise the AD-4 follow-up (story 2.9 Q6).");
        Assert.Contains(after.Descendants(One2026 + "T"), t => t.Value == marker + " edited");
    }

    private async Task<(ComGateway Gateway, UpdatePageContentUseCase UseCase)> StartAsync()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        Directory.CreateDirectory(tempDir);
        var settingsPath = Path.Combine(tempDir, "settings.json");
        File.WriteAllText(settingsPath, """{ "schemaVersion": 1 }""");
        var store = new SettingsStore(settingsPath, JournalFolder);
        var gateway = new ComGateway(new ProcessProbe(), store);
        try
        {
            var notebook = await TestNotebook.EnsureAsync(gateway, Ct);

            // Keyed by MCP Test's live path, read back from GetHierarchy (AD-6).
            var self = HierarchyNode.Root(await gateway.GetHierarchyAsync(notebook.NotebookId, CoreScope.Self, Ct));
            Assert.Equal(TestNotebook.NotebookName, self.Name);
            new SettingsWriter(store, TimeProvider.System).SaveWriteGrant(new NotebookGrant(
                GrantKeys.NotebookKey(self.Path), TestNotebook.NotebookName, notebook.NotebookId, Write: true, PermanentDelete: false, DateTimeOffset.UtcNow, GrantedVia.Cli));

            var services = new PageWriteServices(
                gateway,
                new WriteAccess(gateway, store.Read),
                new ReadAccess(gateway, store.Read, writer: null, TimeProvider.System),
                new PageSnapshot(gateway, new JournalStore(store.Read)));
            return (gateway, new UpdatePageContentUseCase(services, new WritePipeline(new WriteLock(), gateway, PageTextCache.WithDefaults(), store.Read)));
        }
        catch
        {
            gateway.Dispose();
            throw;
        }
    }

    /// <summary>A new page in MCP Test's Seed section, created through the guarded write. It stays there afterwards.</summary>
    private async Task<string> FreshPageAsync(ComGateway gateway)
    {
        var notebook = await TestNotebook.EnsureAsync(gateway, Ct);
        var pageId = await TestNotebook.WriteAsync(
            gateway,
            notebook.SectionId,
            app =>
            {
                app.CreateNewPage(notebook.SectionId, out var id, InteropNewPageStyle.npsBlankPageWithTitle);
                return id;
            },
            "CreateNewPage(story 2.9 page)",
            Ct);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"page {pageId}"));
        return pageId;
    }

    private static PageWriteBasis Basis(string pageXml)
    {
        var basis = PageBasis.From(pageXml);
        return new PageWriteBasis(basis.LastModifiedTime, basis.PageHash, basis.ElementHashes);
    }

    private static PageWriteBasis ToCore(PageBasisResult basis) => new(basis.LastModifiedTime, basis.PageHash, basis.ElementHashes);

    private static string LastModified(string pageXml) =>
        (string?)XDocument.Parse(pageXml).Root!.Attribute("lastModifiedTime")
        ?? throw new InvalidOperationException("The page XML has no lastModifiedTime.");

    private static async Task<XElement> Read2026Async(ComGateway gateway, string pageId) =>
        XDocument.Parse(await gateway.InvokeAsync(
            app =>
            {
                app.GetPageContent(pageId, out var xml, InteropPageInfo.piBasic, XMLSchema.xs2026);
                return xml;
            },
            Ct)).Root!;

    /// <summary>Each row's cells as their <c>colSpan</c> (1 when absent), e.g. <c>[2] [1,1]</c>.</summary>
    private static string Spans(XElement page2026) =>
        string.Join(" ", page2026.Descendants(One2026 + "Row").Select(r =>
            "[" + string.Join(",", r.Elements(One2026 + "Cell").Select(c => (string?)c.Attribute("colSpan") ?? "1")) + "]"));
}
