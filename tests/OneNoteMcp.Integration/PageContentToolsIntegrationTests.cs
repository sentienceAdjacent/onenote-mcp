using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Com;
using OneNoteMcp.Com.Interop;
using CorePageInfo = OneNoteMcp.Core.PageInfo;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Logging;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Serilog;
using Xunit;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 8's page content tools against a real OneNote, through tool, use case and the real <see cref="ComGateway"/>,
/// logging to a temporary file log (AD-13). Opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c>; OneNote must already be
/// running. The only write is the sentinel paragraph on MCP Test's seed page, made through the guarded
/// <see cref="TestNotebook.WriteAsync"/>; a later run rewrites that same paragraph rather than adding another.
/// </summary>
public sealed class PageContentToolsIntegrationTests : IDisposable
{
    private const string SentinelPrefix = "onenote-mcp-sentinel-";

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PageContent_ReadsTheSentinel_AndTheLogNeverHoldsIt()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        using var gateway = Gateway();
        var notebook = await TestNotebook.EnsureAsync(gateway, Ct);
        var pageId = notebook.PageId;

        var sentinel = SentinelPrefix + Guid.NewGuid().ToString("N");
        var update = SentinelUpdate(pageId, await gateway.GetPageContentAsync(pageId, CorePageInfo.Basic, Ct), sentinel);
        await TestNotebook.WriteAsync(
            gateway,
            pageId,
            app =>
            {
                app.UpdatePageContent(update, DateTime.MinValue, XMLSchema.xs2013, force: false);
                return true;
            },
            "UpdatePageContent(sentinel)",
            Ct);

        var log = FileLog.Start(new FileLogOptions(Path.Combine(_dir, "logs")));
        Assert.NotNull(log.Logger);
        using (var factory = LoggerFactory.Create(b => b.AddSerilog(log.Logger, dispose: true)))
        {
            var tool = new GetPageContentTool(new ToolRunner(factory.CreateLogger<ToolRunner>()), new GetPageContentUseCase(gateway));
            foreach (var includeBinary in new[] { false, true })
            {
                var call = $"get_page_content(includeBinary: {includeBinary})";
                var root = Ok(await tool.GetPageContent(pageId, includeBinary, Ct), call);
                Assert.Contains(sentinel, root.GetProperty("xml").GetString()!, StringComparison.Ordinal);
                Assert.False(string.IsNullOrEmpty(root.GetProperty("meta").GetProperty("basis").GetProperty("pageHash").GetString()), call);
                Assert.False(root.GetProperty("meta").TryGetProperty("binaryUnavailable", out _), call); // MCP Test is local
            }
        }

        var text = File.ReadAllText(log.FilePath!);
        Assert.Equal(2, CountOf(text, "tool=get_page_content result=ok"));
        Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelPrefix, text, StringComparison.Ordinal);
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
    /// A partial page update: the earlier sentinel paragraph rewritten in place when the page has one, else a new
    /// Outline holding only the sentinel. IDs come from the page XML and are passed through unchanged.
    /// </summary>
    private static string SentinelUpdate(string pageId, string pageXml, string sentinel)
    {
        var existing = XDocument.Parse(pageXml).Descendants(One + "OE")
            .FirstOrDefault(oe => oe.Elements(One + "T").Any(t => t.Value.StartsWith(SentinelPrefix, StringComparison.Ordinal)));
        var outlineId = (string?)existing?.Ancestors(One + "Outline").FirstOrDefault()?.Attribute("objectID");
        var oeId = (string?)existing?.Attribute("objectID");

        var oe = oeId is not null && outlineId is not null
            ? $"""<one:OE objectID="{oeId}"><one:T><![CDATA[{sentinel}]]></one:T></one:OE>"""
            : $"""<one:OE><one:T><![CDATA[{sentinel}]]></one:T></one:OE>""";
        var outline = oeId is not null && outlineId is not null
            ? $"""<one:Outline objectID="{outlineId}"><one:OEChildren>{oe}</one:OEChildren></one:Outline>"""
            : $"""<one:Outline><one:OEChildren>{oe}</one:OEChildren></one:Outline>""";
        return $"""<?xml version="1.0"?><one:Page xmlns:one="{OneNoteXml.Namespace2013}" ID="{pageId}">{outline}</one:Page>""";
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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
