using System.Text.RegularExpressions;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 9's byte-preserving cut (AD-4): the output is the input with exactly the removed <c>one:Page</c> spans cut out.
/// The expected text is built here by an independent regex over the input, never by the code under test.
/// </summary>
public sealed partial class SearchResultFilterTests
{
    private const string Ns = "xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\"";

    public static string Fixture => FakeGateway.ReadFixture("mcp-test/search/findpages-fixture.xml");

    [Fact]
    public void Pages_Fixture_SeventeenInDocumentOrder_TenInTheRecycleBin()
    {
        var pages = SearchResultFilter.Pages(Fixture);

        Assert.Equal(17, pages.Count);
        Assert.Equal(Enumerable.Range(0, 17), pages.Select(p => p.Index));
        Assert.Equal(PageIdsByRegex(Fixture), pages.Select(p => p.Id));
        Assert.Equal(10, pages.Count(p => p.InRecycleBin));
        Assert.All(pages.Take(7), p => Assert.False(p.InRecycleBin));
        Assert.Equal(["Untitled", "Untitled", "Fixture Nested"], pages.Take(3).Select(p => p.Name));
        Assert.Equal(["onenote-mcp-fixture"], pages[2].MetaNames);
        Assert.Empty(pages[0].MetaNames);
    }

    [Fact]
    public void Pages_LastModifiedTime_ParsedWhenPresent_NullWhenMissingOrUnparsable()
    {
        var xml = "<one:Section " + Ns + " ID=\"s\">"
            + "<one:Page ID=\"utc\" name=\"n\" lastModifiedTime=\"2026-11-05T02:40:26.000Z\"/>"
            + "<one:Page ID=\"missing\" name=\"n\"/>"
            + "<one:Page ID=\"empty\" name=\"n\" lastModifiedTime=\"\"/>"
            + "<one:Page ID=\"garbage\" name=\"n\" lastModifiedTime=\"yesterday\"/>"
            + "<one:Page ID=\"tooEarly\" name=\"n\" lastModifiedTime=\"0001-01-01T00:00:00+14:00\"/>"
            + "<one:Page ID=\"tooLate\" name=\"n\" lastModifiedTime=\"9999-12-31T23:59:59-14:00\"/>"
            + "</one:Section>";

        var pages = SearchResultFilter.Pages(xml);

        Assert.Equal(new DateTimeOffset(2026, 11, 5, 2, 40, 26, TimeSpan.Zero), pages[0].LastModifiedTime);
        Assert.All(pages.Skip(1), p => Assert.Null(p.LastModifiedTime));
        Assert.All(SearchResultFilter.Pages(Fixture), p => Assert.NotNull(p.LastModifiedTime));
    }

    [Fact]
    public void Remove_Fixture_RecycleBinPages_CutsExactlyThoseSpans()
    {
        var cut = SearchResultFilter.Remove(Fixture, p => p.InRecycleBin);

        var binIds = SearchResultFilter.Pages(Fixture).Where(p => p.InRecycleBin).Select(p => p.Id).ToList();
        Assert.Equal(binIds, cut.RemovedIds);
        Assert.Equal(CutByRegex(Fixture, binIds), cut.Xml);
        Assert.Equal(7, SearchResultFilter.Pages(cut.Xml).Count);

        // The emptied recycle-bin sections and the group stay (no removal of non-Page elements).
        Assert.Contains("name=\"Fixtures 2\"", cut.Xml, StringComparison.Ordinal);
        Assert.Contains("isRecycleBin=\"true\"", cut.Xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Remove_Nothing_ReturnsTheExactInputString()
    {
        var xml = Fixture;

        var cut = SearchResultFilter.Remove(xml, _ => false);

        Assert.Same(xml, cut.Xml);
        Assert.Empty(cut.RemovedIds);
    }

    [Fact]
    public void Remove_EveryPage_LeavesEverySectionAndGroup()
    {
        var cut = SearchResultFilter.Remove(Fixture, _ => true);

        Assert.Equal(17, cut.RemovedIds.Count);
        Assert.Equal(CutByRegex(Fixture, PageIdsByRegex(Fixture)), cut.Xml);
        Assert.DoesNotContain("<one:Page", cut.Xml, StringComparison.Ordinal);
        Assert.Equal(Regex.Count(Fixture, "<one:Section "), Regex.Count(cut.Xml, "<one:Section "));
    }

    [Fact]
    public void AncestorOnlyFlags_MarkThePage()
    {
        var xml = "<?xml version=\"1.0\"?>\n<one:Notebook " + Ns + " ID=\"n\">"
            + "<one:Section ID=\"s1\" isInRecycleBin=\"true\"><one:Page ID=\"underBinSection\" name=\"a\"/></one:Section>"
            + "<one:SectionGroup ID=\"g\" isRecycleBin=\"true\"><one:Section ID=\"s2\"><one:Page ID=\"underBinGroup\" name=\"b\"/></one:Section></one:SectionGroup>"
            + "<one:SectionGroup ID=\"g2\" isRecycleBin=\"1\"><one:SectionGroup ID=\"g3\"><one:Section ID=\"s3\"><one:Page ID=\"deep\" name=\"c\"/></one:Section></one:SectionGroup></one:SectionGroup>"
            + "<one:Section ID=\"s4\" isInRecycleBin=\"false\"><one:Page ID=\"live\" name=\"d\"/></one:Section>"
            + "</one:Notebook>";

        var pages = SearchResultFilter.Pages(xml);

        Assert.Equal(["underBinSection", "underBinGroup", "deep", "live"], pages.Select(p => p.Id));
        Assert.Equal([true, true, true, false], pages.Select(p => p.InRecycleBin));
    }

    [Fact]
    public void Remove_KeepsCrLfWhitespaceCommentsAndOddAttributes_ByteForByte()
    {
        // Pretty-printed with CRLF, a comment, a '>' inside an attribute value, single quotes, a surrogate pair before
        // a page on the same line, a page with children and a self-closed page.
        var xml = "<?xml version=\"1.0\"?>\r\n<one:Notebook " + Ns + " ID=\"n\">\r\n"
            + "  <!-- a <one:Page ID=\"comment\"/> in a comment -->\r\n"
            + "  <one:Section ID=\"s\" name=\"a > b \U0001F600\">\r\n"
            + "    <one:Page ID=\"p1\" name='x > y'   />\r\n"
            + "    <one:Page ID=\"p2\" name=\"\U0001F600\"><one:Meta name=\"m\" content=\"1\"/>\r\n      <one:Meta name=\"n\" content=\"2\"></one:Meta>\r\n    </one:Page >\r\n"
            + "    <one:Page\r\n      ID=\"p3\"\r\n      name=\"z\"/><one:Page ID=\"p4\" name=\"w\"></one:Page>\n"
            + "  </one:Section>\r\n</one:Notebook>\r\n";

        var pages = SearchResultFilter.Pages(xml);
        Assert.Equal(["p1", "p2", "p3", "p4"], pages.Select(p => p.Id));
        Assert.Equal(["m", "n"], pages[1].MetaNames);

        foreach (var remove in new[] { new[] { "p1" }, ["p2"], ["p3"], ["p4"], ["p1", "p3"], ["p2", "p4"], ["p1", "p2", "p3", "p4"] })
        {
            var cut = SearchResultFilter.Remove(xml, p => remove.Contains(p.Id));
            Assert.Equal(remove, cut.RemovedIds);
            Assert.Equal(CutByRegex(xml, remove), cut.Xml);
        }
    }

    [Fact]
    public void Remove_OnlyOutermostPages_MetaOfTheOuterPageOnly()
    {
        // The schema has no nested pages; if one ever came, the outer page is the unit cut.
        var xml = "<one:Notebook " + Ns + "><one:Page ID=\"outer\"><one:Meta name=\"own\"/><one:X><one:Meta name=\"deeper\"/></one:X></one:Page></one:Notebook>";

        var page = Assert.Single(SearchResultFilter.Pages(xml));

        Assert.Equal(["own"], page.MetaNames);
        Assert.Equal("<one:Notebook " + Ns + "></one:Notebook>", SearchResultFilter.Remove(xml, _ => true).Xml);
    }

    [Fact]
    public void PageInAnotherNamespace_IsNotAPage()
    {
        var xml = "<one:Notebook " + Ns + " xmlns:x=\"urn:x\"><x:Page ID=\"foreign\"/></one:Notebook>";

        Assert.Empty(SearchResultFilter.Pages(xml));
    }

    [Theory]
    [InlineData("<one:Notebook")]
    [InlineData("<one:Notebook xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\"><one:Page ID=\"secret-text\"></one:Notebook>")]
    [InlineData("")]
    public void Malformed_ThrowsOneNoteXmlException_WithoutContent(string xml)
    {
        var ex = Assert.Throws<OneNoteXmlException>(() => SearchResultFilter.Remove(xml, _ => true));

        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
    }

    // --- PageText ---------------------------------------------------------------------------------------------------

    [Fact]
    public void PageText_StripsMarkup_DecodesEntities_OneLinePerT()
    {
        var xml = "<one:Page " + Ns + " ID=\"p\"><one:Title><one:OE><one:T><![CDATA[Ti<span style='x'>tle</span>]]></one:T></one:OE></one:Title>"
            + "<one:Outline><one:OEChildren><one:OE><one:T><![CDATA[a &amp; <a href=\"u\">b</a>]]></one:T></one:OE>"
            + "<one:OE><one:T><![CDATA[next]]></one:T></one:OE></one:OEChildren></one:Outline></one:Page>";

        Assert.Equal("Title\na & b\nnext", PageText.Of(xml));
    }

    [Theory]
    [InlineData("alpha beta", "Alpha", "x BETA y", true)]
    [InlineData("alpha beta", "Alpha", "x y", false)]
    [InlineData("  alpha\tbeta  ", "", "alphabeta", true)]
    [InlineData("span", "", "text", false)]
    [InlineData("", "", "", true)]
    public void PageText_EveryTerm_InNameOrText_IgnoringCase(string query, string name, string text, bool expected)
    {
        Assert.Equal(expected, PageText.ContainsAll(PageText.Terms(query), name, text));
    }

    [Fact]
    public void PageText_TermNeverMatchesAcrossTwoTs()
    {
        var xml = "<one:Page " + Ns + "><one:OE><one:T>foo</one:T></one:OE><one:OE><one:T>bar</one:T></one:OE></one:Page>";

        Assert.False(PageText.ContainsAll(PageText.Terms("foobar"), string.Empty, PageText.Of(xml)));
    }

    // --- Independent oracle -----------------------------------------------------------------------------------------

    public static List<string> PageIdsByRegex(string xml) =>
        [.. PageTag().Matches(StripComments(xml)).Select(m => m.Groups["id"].Value)];

    /// <summary>The input with each listed page's span cut out, found by regex: the self-closed tag, or start to end tag.</summary>
    public static string CutByRegex(string xml, IReadOnlyCollection<string> ids) =>
        PageSpan().Replace(xml, m => ids.Contains(m.Groups["id"].Value) && !InComment(xml, m.Index) ? string.Empty : m.Value);

    private static string StripComments(string xml) => Regex.Replace(xml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static bool InComment(string xml, int index)
    {
        var open = xml.LastIndexOf("<!--", index, StringComparison.Ordinal);
        return open >= 0 && xml.IndexOf("-->", open, StringComparison.Ordinal) > index;
    }

    [GeneratedRegex("<one:Page\\s[^>]*?\\bID=\"(?<id>[^\"]*)\"")]
    private static partial Regex PageTag();

    [GeneratedRegex("<one:Page\\s(?:[^>\"']|\"[^\"]*\"|'[^']*')*?\\bID=\"(?<id>[^\"]*)\"(?:[^>\"']|\"[^\"]*\"|'[^']*')*?(?:/>|>.*?</one:Page\\s*>)", RegexOptions.Singleline)]
    private static partial Regex PageSpan();
}
