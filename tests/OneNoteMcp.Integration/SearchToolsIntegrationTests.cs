using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Com;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;
using CorePageInfo = OneNoteMcp.Core.PageInfo;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 9's search tools against a real OneNote, through tool, use case and the real <see cref="ComGateway"/>.
/// Opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c>; OneNote must already be running. The only write is a fresh token
/// paragraph and a <c>one:Meta name="onenote-mcp-search"</c> on MCP Test's seed page, through the guarded
/// <see cref="TestNotebook.WriteAsync"/>; a later run rewrites that same paragraph and Meta rather than adding more.
/// </summary>
public sealed class SearchToolsIntegrationTests : IDisposable
{
    private const string TokenPrefix = "onenotemcpsearch";
    private const string MetaName = "onenote-mcp-search";

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FindPagesAndFindMeta_FindTheSeedPage_PossiblyIncomplete_NoRecycleBinPages()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        using var gateway = Gateway();
        var notebook = await TestNotebook.EnsureAsync(gateway, Ct);
        var pageId = notebook.PageId;

        // One word, so FindPages and the text check both see a single term.
        var token = TokenPrefix + Guid.NewGuid().ToString("N");
        var update = TokenUpdate(pageId, await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct), token);
        await TestNotebook.WriteAsync(
            gateway,
            pageId,
            app =>
            {
                app.UpdatePageContent(update, DateTime.MinValue, XMLSchema.xs2013, force: false);
                return true;
            },
            "UpdatePageContent(search token)",
            Ct);

        var large = new LargeResults();
        var runner = large.Runner;
        var results = new (string Call, CallToolResult Result)[]
        {
            ("find_pages(token)", await new FindPagesTool(runner, new FindPagesUseCase(gateway)).FindPages(token, notebook.NotebookId, cancellationToken: Ct)),
            ("find_meta(onenote-mcp-search)", await new FindMetaTool(runner, new FindMetaUseCase(gateway)).FindMeta(MetaName, notebook.NotebookId, cancellationToken: Ct)),
        };

        foreach (var (call, result) in results)
        {
            var root = Ok(result, call);
            Assert.True(root.GetProperty("meta").GetProperty("possiblyIncomplete").GetBoolean(), call);
            var pages = SearchResultFilter.Pages(await large.XmlAsync(root, Ct));
            Assert.Contains(pages, p => p.Id == pageId);
            Assert.DoesNotContain(pages, p => p.InRecycleBin);
            Assert.All(
                root.GetProperty("meta").GetProperty("filtered").EnumerateArray(),
                f => Assert.NotEqual(pageId, f.GetProperty("pageId").GetString()));
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// A partial page update: the search Meta (set by name), plus the earlier token paragraph rewritten in place when
    /// the page has one, else a new Outline holding only the token. IDs come from the page XML, unchanged.
    /// </summary>
    private static string TokenUpdate(string pageId, string pageXml, string token)
    {
        var existing = XDocument.Parse(pageXml).Descendants(One + "OE")
            .FirstOrDefault(oe => oe.Elements(One + "T").Any(t => t.Value.StartsWith(TokenPrefix, StringComparison.Ordinal)));
        var outlineId = (string?)existing?.Ancestors(One + "Outline").FirstOrDefault()?.Attribute("objectID");
        var oeId = (string?)existing?.Attribute("objectID");
        var inPlace = oeId is not null && outlineId is not null;

        var oe = inPlace
            ? $"""<one:OE objectID="{oeId}"><one:T><![CDATA[{token}]]></one:T></one:OE>"""
            : $"""<one:OE><one:T><![CDATA[{token}]]></one:T></one:OE>""";
        var outline = inPlace
            ? $"""<one:Outline objectID="{outlineId}"><one:OEChildren>{oe}</one:OEChildren></one:Outline>"""
            : $"""<one:Outline><one:OEChildren>{oe}</one:OEChildren></one:Outline>""";
        return $"""<?xml version="1.0"?><one:Page xmlns:one="{OneNoteXml.Namespace2013}" ID="{pageId}"><one:Meta name="{MetaName}" content="{token}"/>{outline}</one:Page>""";
    }

    private ComGateway Gateway()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "schemaVersion": 1 }""");
        return new ComGateway(new ProcessProbe(), new SettingsStore(path));
    }

    private static JsonElement Ok(CallToolResult result, string call)
    {
        Assert.NotNull(result.StructuredContent);
        var root = result.StructuredContent.Value;
        Assert.False(result.IsError, $"{call} failed: {root.GetRawText()}");
        Assert.True(root.GetProperty("ok").GetBoolean());
        return root;
    }
}
