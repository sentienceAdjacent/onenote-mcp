using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>A clock tests move by hand (AD-20's TTL).</summary>
internal sealed class FakeClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}

/// <summary>A gateway that serves set strings for page reads, binaries and hierarchies, and nothing else.</summary>
internal sealed class StubGateway : IOneNoteGateway
{
    public string Basic { get; set; } = string.Empty;

    public string? BinaryData { get; set; }

    public string Binary { get; set; } = string.Empty;

    public string Hierarchy { get; set; } = string.Empty;

    public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct) => Task.FromResult(Hierarchy);

    public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct) =>
        Task.FromResult(pageInfo == PageInfo.Basic ? Basic : BinaryData ?? Basic);

    public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) => Task.FromResult(Binary);

    public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => throw new NotSupportedException();

    public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => throw new NotSupportedException();

    public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>The real runner, store and tools, with settings and a clock the test controls.</summary>
internal sealed class LargeResultHarness : IDisposable
{
    public LargeResultHarness(int inlineMaxChars = 20_000, int ttlMinutes = 30, int maxStoreMB = 16, ILogger<ToolRunner>? logger = null)
    {
        Settings = new ResultSettings(inlineMaxChars, ttlMinutes, maxStoreMB);
        Store = new ResultStore(Clock, () => Settings);
        Runner = new ToolRunner(logger ?? NullLogger<ToolRunner>.Instance, new ResultDelivery(Store, EnvelopeMeasure.Instance));
        ReadTool = new ReadResultTool(Runner, new ReadResultUseCase(Store, EnvelopeMeasure.Instance));
    }

    public FakeClock Clock { get; } = new();

    public ResultSettings Settings { get; set; }

    public ResultStore Store { get; }

    public ToolRunner Runner { get; }

    public ReadResultTool ReadTool { get; }

    public StubGateway Gateway { get; } = new();

    /// <summary>Every text block any call returned, for the "no block over inlineMaxChars" check.</summary>
    public List<int> TextBlockSizes { get; } = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Store.Dispose();

    public async Task<CallToolResult> Page(bool includeBinary = false) =>
        Track(await new GetPageContentTool(Runner, new GetPageContentUseCase(Gateway)).GetPageContent("{P}{1}{E1}", includeBinary, Ct));

    public async Task<CallToolResult> Hierarchy() =>
        Track(await new GetHierarchyTool(Runner, new GetHierarchyUseCase(Gateway)).GetHierarchy(cancellationToken: Ct));

    public async Task<CallToolResult> BinaryRead() =>
        Track(await new GetBinaryPageContentTool(Runner, new GetBinaryPageContentUseCase(Gateway)).GetBinaryPageContent("{P}{1}{E1}", "{CB}", Ct));

    public async Task<CallToolResult> Read(string resultId, string @ref = "", string[]? run = null, string expand = "", int? page = null, int? offset = null, int? length = null) =>
        Track(await ReadTool.ReadResult(resultId, @ref, run, expand, page, offset, length, Ct));

    public void AssertNoBlockOverTheThreshold() =>
        Assert.All(TextBlockSizes, size => Assert.True(size <= Settings.InlineMaxChars, $"A text block of {size} chars is over inlineMaxChars {Settings.InlineMaxChars}."));

    private CallToolResult Track(CallToolResult result)
    {
        TextBlockSizes.Add(TextBlockAssert.Matches(result).Length);
        return result;
    }
}

/// <summary>Reading envelopes in the large-result tests.</summary>
internal static class Envelope
{
    public static JsonElement Ok(CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        var root = result.StructuredContent.Value;
        Assert.False(result.IsError, root.GetRawText());
        return root;
    }

    public static JsonElement Error(CallToolResult result)
    {
        Assert.True(result.IsError);
        return result.StructuredContent!.Value.GetProperty("error");
    }

    public static string ResultId(JsonElement root) => root.GetProperty("meta").GetProperty("result").GetProperty("resultId").GetString()!;

    public static string? XmlOf(JsonElement root) => root.TryGetProperty("xml", out var x) ? x.GetString() : null;

    public static bool HasIndex(JsonElement root) => root.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Object;

    public static JsonElement Index(JsonElement root) => root.GetProperty("index");

    public static IEnumerable<JsonElement> Entries(JsonElement root) => Index(root).GetProperty("entries").EnumerateArray();
}

/// <summary>Synthetic 2013-schema XML big enough to go over a test's inline threshold.</summary>
internal static class BigXml
{
    public const string Ns = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    /// <summary>
    /// A page: a title, <paramref name="outlines"/> Outlines of <paramref name="oes"/> OEs each, an Outline holding a
    /// table, an Outline whose one OE holds a <c>one:T</c> of <paramref name="hugeT"/> characters (when given), and a
    /// page-level image with no objectID. With <paramref name="binary"/>, the image holds one:Data instead of a callback.
    /// </summary>
    public static string Page(int outlines, int oes, int hugeT = 0, bool binary = false, string newline = "\n")
    {
        var nl = newline;
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?>").Append(nl);
        sb.Append(CultureInfo.InvariantCulture, $"<one:Page xmlns:one=\"{Ns}\" ID=\"{{P}}{{1}}{{E1}}\" name=\"Ballast\" lastModifiedTime=\"2026-11-06T00:00:00.000Z\">").Append(nl);
        sb.Append("  <one:QuickStyleDef index=\"0\" name=\"p\" fontColor=\"automatic\" />").Append(nl);
        sb.Append("  <one:Title><one:OE objectID=\"{T}{1}{B0}\"><one:T><![CDATA[Ballast &amp; title]]></one:T></one:OE></one:Title>").Append(nl);
        for (var o = 0; o < outlines; o++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  <one:Outline objectID=\"{{O{o}}}{{1}}{{B0}}\" lastModifiedTime=\"2026-11-06T00:00:00.000Z\">").Append(nl);
            sb.Append("    <one:Position x=\"36.0\" y=\"86.4\" z=\"0\" />").Append(nl);
            sb.Append("    <one:OEChildren>").Append(nl);
            for (var e = 0; e < oes; e++)
            {
                sb.Append(CultureInfo.InvariantCulture, $"      <one:OE objectID=\"{{O{o}E{e}}}{{1}}{{B0}}\"><one:T><![CDATA[<span style='font-weight:bold'>Outline {o} line {e}</span> lorem ipsum dolor sit amet, consectetur adipiscing elit &lt;ok&gt;]]></one:T></one:OE>").Append(nl);
            }

            sb.Append("    </one:OEChildren>").Append(nl);
            sb.Append("  </one:Outline>").Append(nl);
        }

        sb.Append("  <one:Outline objectID=\"{TB}{1}{B0}\"><one:OEChildren><one:OE objectID=\"{TBOE}{1}{B0}\"><one:Table bordersVisible=\"true\"><one:Columns><one:Column index=\"0\" width=\"50\" /></one:Columns><one:Row><one:Cell><one:OEChildren><one:OE objectID=\"{C1}{1}{B0}\"><one:T><![CDATA[cell text]]></one:T></one:OE></one:OEChildren></one:Cell></one:Row></one:Table></one:OE></one:OEChildren></one:Outline>").Append(nl);
        if (hugeT > 0)
        {
            sb.Append("  <one:Outline objectID=\"{H}{1}{B0}\"><one:OEChildren><one:OE objectID=\"{HOE}{1}{B0}\"><one:T><![CDATA[");
            sb.Append(Filler(hugeT));
            sb.Append("]]></one:T></one:OE></one:OEChildren></one:Outline>").Append(nl);
        }

        sb.Append(binary
            ? "  <one:Image format=\"png\"><one:Size width=\"1.0\" height=\"1.0\" /><one:Data>iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk</one:Data></one:Image>"
            : "  <one:Image format=\"png\"><one:Size width=\"1.0\" height=\"1.0\" /><one:CallbackID callbackID=\"{CB}\" /></one:Image>").Append(nl);
        sb.Append("  <one:OE><one:T><![CDATA[LAST TEXT OF THE PAGE]]></one:T></one:OE>").Append(nl);
        sb.Append("</one:Page>");
        return sb.ToString();
    }

    /// <summary>A hierarchy: one notebook, <paramref name="sections"/> sections of <paramref name="pages"/> pages, with names <paramref name="nameChars"/> long.</summary>
    public static string Hierarchy(int sections, int pages, int nameChars = 20)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<?xml version=\"1.0\"?>\n<one:Notebooks xmlns:one=\"{Ns}\">");
        sb.Append("<one:Notebook name=\"Notebook A\" nickname=\"Notebook A\" ID=\"{NB}{1}{B0}\" path=\"C:\\notebooks\\A\">");
        for (var s = 0; s < sections; s++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<one:Section name=\"{Name("Section " + s, nameChars)}\" ID=\"{{S{s}}}{{1}}{{B0}}\" path=\"C:\\notebooks\\A\\S{s}.one\">");
            for (var p = 0; p < pages; p++)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<one:Page ID=\"{{S{s}P{p}}}{{1}}{{E1}}\" name=\"{Name($"Page {s}.{p}", nameChars)}\" pageLevel=\"1\" lastModifiedTime=\"2026-11-06T00:00:00.000Z\" />");
            }

            sb.Append("</one:Section>");
        }

        sb.Append("</one:Notebook></one:Notebooks>");
        return sb.ToString();
    }

    public static string Filler(int chars)
    {
        var sb = new StringBuilder(chars);
        var line = 0;
        while (sb.Length < chars)
        {
            sb.Append(CultureInfo.InvariantCulture, $"L{line:D6} the quick brown fox jumps over the lazy dog. ");
            line++;
        }

        return sb.ToString(0, chars);
    }

    private static string Name(string start, int chars) => start.Length >= chars ? start : start + new string('x', chars - start.Length);
}
