using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Backlog 2: <c>z</c> is cut from every sent <c>one:Position</c> by text span (AD-4), so resending an Outline doesn't
/// make OneNote re-stack the page's Outlines (Z probe 57, 2026-11-15 sitting). Every other byte stays.
/// </summary>
public sealed class SentPositionsTests
{
    private const string Ns = "xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\"";

    private static string Page(string body) => $"<?xml version=\"1.0\"?><one:Page {Ns} ID=\"{{P}}\">{body}</one:Page>";

    [Fact]
    public void AResentOutline_LosesZ_AndKeepsXAndY()
    {
        var sent = Page("<one:Outline objectID=\"{O}\"><one:Position x=\"36.0\" y=\"200.0\" z=\"1\" /><one:OEChildren /></one:Outline>");

        Assert.Equal(
            Page("<one:Outline objectID=\"{O}\"><one:Position x=\"36.0\" y=\"200.0\" /><one:OEChildren /></one:Outline>"),
            SentPositions.WithoutZ(sent));
    }

    [Fact]
    public void ANewOutline_LosesZ()
    {
        var sent = Page("<one:Outline><one:Position x=\"1\" y=\"2\" z=\"0\"/><one:OEChildren /></one:Outline>");

        Assert.Equal(Page("<one:Outline><one:Position x=\"1\" y=\"2\"/><one:OEChildren /></one:Outline>"), SentPositions.WithoutZ(sent));
    }

    [Theory]
    [InlineData("<one:Outline><one:Position x=\"1\" y=\"2\" /></one:Outline>")]
    [InlineData("<one:Outline><one:OEChildren /></one:Outline>")]
    [InlineData("")]
    public void WithNoZ_TheSameStringComesBack(string body)
    {
        var sent = Page(body);

        Assert.Same(sent, SentPositions.WithoutZ(sent));
    }

    [Theory]
    [InlineData("<one:Position z='1' x='1' y='2'/>", "<one:Position x='1' y='2'/>")]
    [InlineData("<one:Position x=\"1\" y=\"2\" z = \"3\" />", "<one:Position x=\"1\" y=\"2\" />")]
    [InlineData("<one:Position x=\"1\"\r\n    z=\"3\"\r\n    y=\"2\"/>", "<one:Position x=\"1\"\r\n    y=\"2\"/>")]
    [InlineData("<one:Position x=\"1\" y=\"2\"\n z=\"3\"></one:Position>", "<one:Position x=\"1\" y=\"2\"></one:Position>")]
    [InlineData("<one:Position\tz=\"3\"/>", "<one:Position/>")]
    public void OddSpellings_AreCutCleanly(string position, string expected)
    {
        var sent = Page($"<one:Outline>{position}</one:Outline>");

        var result = SentPositions.WithoutZ(sent);

        Assert.Equal(Page($"<one:Outline>{expected}</one:Outline>"), result);
        Assert.Null(System.Xml.Linq.XDocument.Parse(result).Descendants().Single(e => e.Name.LocalName == "Position").Attribute("z"));
    }

    [Fact]
    public void EveryPosition_IsCovered_PageLevelImagesToo()
    {
        var sent = Page(
            "<one:Outline><one:Position x=\"1\" y=\"2\" z=\"0\"/></one:Outline>"
            + "<one:Image><one:Position x=\"3\" y=\"4\" z=\"1\"/><one:Size width=\"5\" height=\"6\"/></one:Image>");

        Assert.Equal(
            Page("<one:Outline><one:Position x=\"1\" y=\"2\"/></one:Outline><one:Image><one:Position x=\"3\" y=\"4\"/><one:Size width=\"5\" height=\"6\"/></one:Image>"),
            SentPositions.WithoutZ(sent));
    }

    [Fact]
    public void AnyPrefix_ForTheOneNoteNamespace()
    {
        var sent = "<o:Page xmlns:o=\"http://schemas.microsoft.com/office/onenote/2013/onenote\" ID=\"{P}\"><o:Outline><o:Position x=\"1\" z=\"2\"/></o:Outline></o:Page>";

        Assert.Equal(
            "<o:Page xmlns:o=\"http://schemas.microsoft.com/office/onenote/2013/onenote\" ID=\"{P}\"><o:Outline><o:Position x=\"1\"/></o:Outline></o:Page>",
            SentPositions.WithoutZ(sent));
    }

    [Theory]
    [InlineData("<one:Outline><one:OEChildren><one:OE><one:T><![CDATA[<one:Position x=\"1\" z=\"2\"/>]]></one:T></one:OE></one:OEChildren></one:Outline>")]
    [InlineData("<one:Outline><one:Position xmlns:f=\"urn:f\" x=\"1\" f:z=\"2\"/></one:Outline>")]
    [InlineData("<one:Outline><f:Position xmlns:f=\"urn:f\" x=\"1\" z=\"2\"/></one:Outline>")]
    [InlineData("<one:Outline><one:Size z=\"2\"/></one:Outline>")]
    public void LookAlikes_AreUntouched(string body)
    {
        var sent = Page(body);

        Assert.Same(sent, SentPositions.WithoutZ(sent));
    }

    [Fact]
    public void BrokenXml_ThrowsWithItsPositionOnly()
    {
        var error = Assert.Throws<OneNoteXmlException>(() => SentPositions.WithoutZ("<one:Page " + Ns + "><one:Position z=\"secret"));

        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
    }
}
