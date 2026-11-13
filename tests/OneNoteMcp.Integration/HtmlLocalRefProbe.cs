using System.Globalization;
using System.Xml.Linq;
using OneNoteMcp.Com;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Integration.WriteProbes;
using OneNoteMcp.Xml;
using Xunit;
using CoreScope = OneNoteMcp.Core.HierarchyScope;
using InteropNewPageStyle = OneNoteMcp.Com.Interop.NewPageStyle;
using InteropPageInfo = OneNoteMcp.Com.Interop.PageInfo;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 2.9's HTML probe, hitl and opt-in: <c>ONENOTE_MCP_INTEGRATION=1</c> plus
/// <c>ONENOTE_MCP_SCENARIO=html-local-ref</c> (Phill, 2026-11-12). It asks whether OneNote fetches a local file that page
/// HTML refers to, which decides whether the HTML stopgap in <c>update_page_content</c> (refusing <c>one:HTMLBlock</c>
/// and text with local references) stays or goes. It writes a text file holding a unique marker and a 1×1 PNG under
/// <c>%TEMP%</c>, then, on a fresh page in MCP Test's <c>Probes</c> section per case, calls <c>UpdatePageContent</c>
/// through the raw seam behind <see cref="TestNotebook"/>'s guard:
/// <list type="bullet">
/// <item>(a) <c>htmlblock</c>: an <c>one:HTMLBlock</c> whose <c>one:Data</c> HTML is <c>&lt;img src="file:///…png"&gt;</c>
/// and a link to the text file;</item>
/// <item>(b) <c>t-cdata</c>: a <c>one:T</c> whose CDATA holds <c>&lt;img src="file:///…png"&gt;</c>.</item>
/// </list>
/// It reads each page back with piBinaryData and prints <c>RESULT html-local-ref &lt;case&gt;: fetched|not-fetched</c>:
/// fetched when the page holds an image or inserted file, or the marker text. The pages stay in Probes; the temp files
/// are deleted. Never run by the build.
/// </summary>
public sealed class HtmlLocalRefProbe(ITestOutputHelper output) : IDisposable
{
    public const string ScenarioName = "html-local-ref";
    private const string ProbesSectionName = "Probes";

    /// <summary>A 1×1 transparent PNG.</summary>
    private const string PngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-html-local-ref", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task HtmlWithLocalReferences_RecordsWhetherOneNoteFetchesTheFiles()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONENOTE_MCP_SCENARIO") == ScenarioName, $"Hitl scenario: set ONENOTE_MCP_SCENARIO={ScenarioName}.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");

        Directory.CreateDirectory(tempDir);
        var marker = "html-local-ref-" + Guid.NewGuid().ToString("N");
        var textFile = Path.Combine(tempDir, marker + ".txt");
        var pngFile = Path.Combine(tempDir, "probe.png");
        await File.WriteAllTextAsync(textFile, marker, Ct);
        await File.WriteAllBytesAsync(pngFile, Convert.FromBase64String(PngBase64), Ct);
        var pngUrl = new Uri(pngFile).AbsoluteUri;
        var textUrl = new Uri(textFile).AbsoluteUri;

        var settingsPath = Path.Combine(tempDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """{ "schemaVersion": 1 }""", Ct);
        using var gateway = new ComGateway(new ProcessProbe(), new SettingsStore(settingsPath, Path.Combine(tempDir, "journal")));
        var notebook = await TestNotebook.EnsureAsync(gateway, Ct);
        var sectionId = await EnsureProbesAsync(gateway, notebook.NotebookId);

        var cases = new (string Name, string Body)[]
        {
            ("htmlblock", "<one:Outline><one:OEChildren><one:HTMLBlock><one:Data>"
                + ProbeXml.Cdata($"<p>{marker} block</p><img src=\"{pngUrl}\"><p><a href=\"{textUrl}\">text file</a></p>")
                + "</one:Data></one:HTMLBlock></one:OEChildren></one:Outline>"),
            ("t-cdata", ProbeXml.Outline(null, ProbeXml.Oe(null, $"{marker} t <img src=\"{pngUrl}\">"))),
        };

        foreach (var (name, body) in cases)
        {
            var pageId = await TestNotebook.WriteAsync(
                gateway,
                sectionId,
                app =>
                {
                    app.CreateNewPage(sectionId, out var id, InteropNewPageStyle.npsBlankPageWithTitle);
                    return id;
                },
                $"CreateNewPage(html-local-ref {name})",
                Ct);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: page {pageId}"));

            string? error = null;
            try
            {
                await TestNotebook.WriteAsync(
                    gateway,
                    pageId,
                    app =>
                    {
                        app.UpdatePageContent(ProbeXml.Page(pageId, ProbeXml.Title($"html-local-ref {name}") + body), DateTime.MinValue, XMLSchema.xs2013, false);
                        return true;
                    },
                    $"UpdatePageContent(html-local-ref {name})",
                    Ct);
            }
#pragma warning disable CA1031 // A probe records the failure as its result.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                error = ex.GetType().Name + ": " + ex.Message;
            }

            var after = XDocument.Parse(await gateway.InvokeAsync(
                app =>
                {
                    app.GetPageContent(pageId, out var xml, InteropPageInfo.piBinaryData, XMLSchema.xs2013);
                    return xml;
                },
                Ct)).Root!;
            var images = after.Descendants(One + "Image").Count();
            var imageData = after.Descendants(One + "Image").Count(i => i.Element(One + "Data") is not null);
            var files = after.Descendants(One + "InsertedFile").Count();
            var markerTexts = after.Descendants(One + "T").Count(t => t.Value.Contains(marker, StringComparison.Ordinal));
            var markerFromFile = after.Descendants(One + "T").Any(t => t.Value.Trim() == marker);
            var fetched = images > 0 || files > 0 || markerFromFile;

            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{name}: error={error ?? "none"} images={images} imagesWithData={imageData} insertedFiles={files} textsWithMarker={markerTexts} markerFromFile={markerFromFile}"));
            output.WriteLine($"RESULT html-local-ref {name}: {(fetched ? "fetched" : "not-fetched")}");
        }
    }

    /// <summary>MCP Test's <c>Probes</c> section, created through the guard when it's missing.</summary>
    private static async Task<string> EnsureProbesAsync(ComGateway gateway, string notebookId)
    {
        var tree = await gateway.GetHierarchyAsync(notebookId, CoreScope.Sections, Ct);
        return TestNotebookGuard.FindSectionId(tree, ProbesSectionName)
            ?? await TestNotebook.WriteAsync(
                gateway,
                notebookId,
                app =>
                {
                    app.OpenHierarchy(ProbesSectionName + ".one", notebookId, out var id, CreateFileType.cftSection);
                    return id;
                },
                "OpenHierarchy(Probes)",
                Ct);
    }
}
