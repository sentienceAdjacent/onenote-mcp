using System.Globalization;
using System.Xml.Linq;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Integration.WriteProbes;
using OneNoteMcp.Xml;
using Xunit;
using CorePageInfo = OneNoteMcp.Core.PageInfo;
using InteropNewPageStyle = OneNoteMcp.Com.Interop.NewPageStyle;

namespace OneNoteMcp.Integration;

/// <summary>
/// Opt-in (story 2.7): the real <see cref="ComGateway"/>'s page writes against a fresh page in MCP Test's <c>Seed</c>
/// section. Every write runs only after <see cref="TestNotebook.EnsureTestTargetAsync"/> shows its target inside MCP
/// Test. Each write passes the page XML's own <c>lastModifiedTime</c> string, as the pipeline will (AD-17).
/// </summary>
public sealed class PageWriteGatewayIntegrationTests(ITestOutputHelper output)
{
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UpdateThenDelete_AnOutline_OnAFreshPage()
    {
        using var gateway = Start();
        var pageId = await FreshPageAsync(gateway);
        var marker = "story 2.7 " + Guid.NewGuid().ToString("N");

        // Update: add an Outline, with the page XML's date.
        var before = await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct);
        await TestNotebook.EnsureTestTargetAsync(gateway, pageId, "UpdatePageContent", Ct);
        await gateway.UpdatePageContentAsync(ProbeXml.Page(pageId, ProbeXml.Outline(null, ProbeXml.Oe(null, marker))), LastModified(before), Ct);

        var updated = await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct);
        var outline = XDocument.Parse(updated).Descendants(One + "Outline")
            .Single(o => o.Descendants(One + "T").Any(t => t.Value == marker));
        var outlineId = (string)outline.Attribute("objectID")!;
        output.WriteLine($"update ok: before={LastModified(before)} after={LastModified(updated)}");

        // Delete that Outline, with the re-read date.
        await TestNotebook.EnsureTestTargetAsync(gateway, pageId, "DeletePageContent", Ct);
        await gateway.DeletePageContentAsync(pageId, outlineId, LastModified(updated), Ct);

        var deleted = await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct);
        Assert.DoesNotContain(XDocument.Parse(deleted).Descendants(One + "T"), t => t.Value == marker);
        output.WriteLine($"delete ok: after={LastModified(deleted)}");
    }

    [Fact]
    public async Task Update_WithAStaleDate_IsStaleWrite()
    {
        using var gateway = Start();
        var pageId = await FreshPageAsync(gateway);

        // Spike 2.2: a date an hour older than the page XML's is rejected with 0x80042010.
        var current = OneNoteDates.ToUtc(LastModified(await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct)))!.Value;
        var stale = ProbeXml.FormatUtc(current.AddHours(-1));
        await TestNotebook.EnsureTestTargetAsync(gateway, pageId, "UpdatePageContent", Ct);

        var ex = await Assert.ThrowsAsync<OneNoteException>(() =>
            gateway.UpdatePageContentAsync(ProbeXml.Page(pageId, ProbeXml.Outline(null, ProbeXml.Oe(null, "stale"))), stale, Ct));

        Assert.Equal(ErrorCode.StaleWrite, ex.Code);
        Assert.Equal(unchecked((int)0x80042010), ex.ComHResult);
        Assert.DoesNotContain(
            XDocument.Parse(await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct)).Descendants(One + "T"),
            t => t.Value == "stale");
    }

    private static ComGateway Start()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        return new ComGateway(new ProcessProbe(), new SettingsStore());
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
            "CreateNewPage(story 2.7 page)",
            Ct);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"page {pageId}"));
        return pageId;
    }

    /// <summary>The page root's <c>lastModifiedTime</c>, exactly as OneNote wrote it.</summary>
    private static string LastModified(string pageXml) =>
        (string?)XDocument.Parse(pageXml).Root!.Attribute("lastModifiedTime")
        ?? throw new InvalidOperationException("The page XML has no lastModifiedTime.");
}
