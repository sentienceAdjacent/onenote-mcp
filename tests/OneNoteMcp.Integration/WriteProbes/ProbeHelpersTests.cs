using System.Xml.Linq;
using Xunit;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>Story 2.2's probe scaffolding, pure parts. These run without OneNote, whenever the integration project runs.</summary>
public sealed class ProbeHelpersTests
{
    private const string Ns = OneNoteMcp.Xml.OneNoteXml.Namespace2013;

    // Made-up account IDs; the real one never lands in the repo.
    [Theory]
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Cloud/", "https://d.docs.live.net/<cid>/Documents/MCP Test Cloud/")]
    [InlineData("https://d.docs.live.net/0123456789abcdef/Documents/MCP Test Cloud/Probes.one", "https://d.docs.live.net/<cid>/Documents/MCP Test Cloud/Probes.one")]
    [InlineData("""{"path":"https://D.DOCS.LIVE.NET/0123456789ABCDEF/"}""", """{"path":"https://D.DOCS.LIVE.NET/<cid>/"}""")]
    [InlineData("https://onedrive.live.com/view.aspx?cid=0123456789abcdef&resid=0123456789ABCDEF!123", "https://onedrive.live.com/view.aspx?cid=<cid>&resid=<cid>!123")]
    [InlineData(@"C:\Users\me\AppData\Local\OneNoteMcp-Test\MCP Test", @"C:\Users\me\AppData\Local\OneNoteMcp-Test\MCP Test")]
    [InlineData("{8E1AB522-1370-4579-98BC-7032B2A95A5E}{1}{B0}", "{8E1AB522-1370-4579-98BC-7032B2A95A5E}{1}{B0}")] // IDs stay
    public void Redact_ReplacesTheAccountId(string input, string expected) =>
        Assert.Equal(expected, ProbeRecorder.Redact(input));

    [Fact]
    public void Pattern_LettersInFirstSeenOrder()
    {
        Assert.Equal((2, "ABAB"), ProbeXml.Pattern(["x", "y", "x", "y"]));
        Assert.Equal((1, "AAA"), ProbeXml.Pattern(["x", "x", "x"]));
    }

    [Fact]
    public void ParseUtc_IsUtcKind()
    {
        var value = ProbeXml.ParseUtc("2026-11-10T03:19:04.000Z");

        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(new DateTime(2026, 11, 10, 3, 19, 4, DateTimeKind.Utc), value);
        Assert.Equal("2026-11-10T03:19:04.000Z", ProbeXml.FormatUtc(value));
    }

    [Fact]
    public void PageOf_UsesTheOnePrefixAndCopiesChildren()
    {
        var outline = XElement.Parse($$"""<one:Outline xmlns:one="{{Ns}}" objectID="{O}"><one:OEChildren><one:OE objectID="{A}"><one:T><![CDATA[a]]></one:T></one:OE></one:OEChildren></one:Outline>""");

        var xml = LocalWriteProbes.PageOf("{P}", ProbeXml.One, outline);

        Assert.StartsWith($$"""<?xml version="1.0"?><one:Page xmlns:one="{{Ns}}" ID="{P}"><one:Outline objectID="{O}">""", xml, StringComparison.Ordinal);
        Assert.Contains("<![CDATA[a]]>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_BuildsAPartialUpdate()
    {
        var xml = ProbeXml.Page("{P}", ProbeXml.Outline(null, ProbeXml.Oe("{A}", "a&nbsp;b")));
        var root = XDocument.Parse(xml).Root!;

        Assert.Equal(ProbeXml.One + "Page", root.Name);
        Assert.Equal("a&nbsp;b", ProbeXml.Text(ProbeXml.OeByText(root, "a&nbsp;")!));
        Assert.Equal("{A}", ProbeXml.ObjectId(ProbeXml.OeByText(root, "a")));
    }

    [Fact]
    public void PageHashWithout_IgnoresOnlyTheNamedAttribute()
    {
        static string Page(string unlocked, string locked) =>
            $$"""<one:Page xmlns:one="{{Ns}}" ID="{P}"><one:Outline><one:OEChildren><one:OE><one:Table><one:Columns><one:Column index="0" width="{{locked}}" isLocked="true" /><one:Column index="1" width="{{unlocked}}" /></one:Columns></one:Table></one:OE></one:OEChildren></one:Outline></one:Page>""";

        bool Unlocked(XElement c) => (string?)c.Attribute("isLocked") != "true";

        Assert.Equal(
            ProbeXml.PageHashWithout(Page("80.0", "105.5"), "Column", "width", Unlocked),
            ProbeXml.PageHashWithout(Page("81.5", "105.5"), "Column", "width", Unlocked));
        Assert.NotEqual(
            ProbeXml.PageHashWithout(Page("80.0", "105.5"), "Column", "width", Unlocked),
            ProbeXml.PageHashWithout(Page("80.0", "106.0"), "Column", "width", Unlocked));
    }
}
