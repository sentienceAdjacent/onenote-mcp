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
/// Opt-in (story 2.10): <c>delete_page_content</c>'s use case through the real <see cref="ComGateway"/> and the write
/// pipeline, on fresh pages in MCP Test's <c>Seed</c> section, with a temporary <c>settings.json</c> that grants writes
/// to MCP Test only and a temporary journal folder. Every write runs after
/// <see cref="TestNotebook.EnsureTestTargetAsync"/> shows its page inside MCP Test. Live sitting only; never run by the
/// build. The fresh delete also settles the spine's Deferred row "<c>DeletePageContent</c> and dates" for a local page:
/// it sends the page XML's <c>lastModifiedTime</c>, as AD-17 says.
/// </summary>
public sealed class DeletePageContentIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

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
    public async Task FreshDelete_RemovesTheOutline_WithOneJournalEntry()
    {
        var (gateway, useCase) = await StartAsync();
        using var _ = gateway;
        var pageId = await FreshPageAsync(gateway);
        var marker = "story 2.10 " + Guid.NewGuid().ToString("N");
        var outlineId = await AddOutlineAsync(gateway, pageId, marker);

        var read = await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct);
        await TestNotebook.EnsureTestTargetAsync(gateway, pageId, "DeletePageContent", Ct);
        var result = await useCase.ExecuteAsync(new ToolCallContext("it-delete"), pageId, outlineId, Basis(read), Ct);
        output.WriteLine($"delete: warnings=[{string.Join("; ", result.Warnings.Select(w => w.Code + ": " + w.Message))}]");

        var after = XDocument.Parse(await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct));
        Assert.DoesNotContain(after.Descendants(One + "T"), t => t.Value == marker);
        Assert.NotNull(result.Meta.Basis);
        Assert.Single(Directory.GetFiles(JournalFolder));
    }

    [Fact]
    public async Task StaleHash_OnAPageThatIsntDisplayed_IsStaleWrite()
    {
        // Spike 2.2 constraint 4: on a page that isn't displayed, lastModifiedTime doesn't advance, so the hash catches it.
        var (gateway, useCase) = await StartAsync();
        using var _ = gateway;
        var pageId = await FreshPageAsync(gateway);
        var outlineId = await AddOutlineAsync(gateway, pageId, "first");

        var read = await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct);
        var basis = Basis(read);
        var outline = XDocument.Parse(read).Root!.Elements(One + "Outline").Single();
        var oe = outline.Descendants(One + "OE").Single();

        // Another writer changes the Outline after the caller's read.
        await TestNotebook.EnsureTestTargetAsync(gateway, pageId, "UpdatePageContent", Ct);
        await gateway.UpdatePageContentAsync(
            ProbeXml.Page(pageId, ProbeXml.Outline(outlineId, ProbeXml.Oe(ProbeXml.ObjectId(oe), "second writer"))),
            LastModified(read),
            Ct);
        output.WriteLine($"date before={LastModified(read)} after={LastModified(await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct))}");

        await TestNotebook.EnsureTestTargetAsync(gateway, pageId, "DeletePageContent", Ct);
        var ex = await Assert.ThrowsAsync<OneNoteException>(() => useCase.ExecuteAsync(new ToolCallContext("it-stale"), pageId, outlineId, basis, Ct));

        Assert.Equal(ErrorCode.StaleWrite, ex.Code);

        // The hash refusal names the Outline; a date refusal wouldn't, so this proves the hash caught it.
        Assert.Contains(outlineId, ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(JournalFolder) && Directory.EnumerateFiles(JournalFolder).Any());
        var after = XDocument.Parse(await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct));
        Assert.Contains(after.Root!.Elements(One + "Outline"), o => (string?)o.Attribute("objectID") == outlineId);
    }

    private async Task<(ComGateway Gateway, DeletePageContentUseCase UseCase)> StartAsync()
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
            return (gateway, new DeletePageContentUseCase(services, new WritePipeline(new WriteLock(), gateway, PageTextCache.WithDefaults(), store.Read)));
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
            "CreateNewPage(story 2.10 page)",
            Ct);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"page {pageId}"));
        return pageId;
    }

    /// <summary>Adds an Outline holding one OE with <paramref name="text"/> through the gateway; returns its objectID.</summary>
    private async Task<string> AddOutlineAsync(ComGateway gateway, string pageId, string text)
    {
        await TestNotebook.EnsureTestTargetAsync(gateway, pageId, "UpdatePageContent", Ct);
        await gateway.UpdatePageContentAsync(
            ProbeXml.Page(pageId, ProbeXml.Outline(null, ProbeXml.Oe(null, text))),
            LastModified(await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct)),
            Ct);
        var page = XDocument.Parse(await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct)).Root!;
        return ProbeXml.ObjectId(page.Elements(One + "Outline").Single(o => o.Descendants(One + "T").Any(t => t.Value == text)))
            ?? throw new InvalidOperationException("The added Outline came back without an objectID.");
    }

    private static PageWriteBasis Basis(string pageXml)
    {
        var basis = PageBasis.From(pageXml);
        return new PageWriteBasis(basis.LastModifiedTime, basis.PageHash, basis.ElementHashes);
    }

    private static string LastModified(string pageXml) =>
        (string?)XDocument.Parse(pageXml).Root!.Attribute("lastModifiedTime")
        ?? throw new InvalidOperationException("The page XML has no lastModifiedTime.");
}
