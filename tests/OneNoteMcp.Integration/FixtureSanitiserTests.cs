using Xunit;

namespace OneNoteMcp.Integration;

/// <summary>The fixture sanitiser and probe logic, against XML strings. Runs without OneNote.</summary>
public sealed class FixtureSanitiserTests
{
    private const string Ns = "http://schemas.microsoft.com/office/onenote/2013/onenote";
    private const string NbId = "{11111111-1111-1111-1111-111111111111}{1}{B0}";

    [Fact]
    public void KeepOnlyNotebook_DropsOtherNotebooksUnfiledAndOpenSections_KeepingText()
    {
        var xml = "<?xml version=\"1.0\"?>\r\n"
            + $"<one:Notebooks xmlns:one=\"{Ns}\">\r\n"
            + "  <one:Notebook name=\"Private\" ID=\"{22222222-2222-2222-2222-222222222222}{1}{B0}\" path=\"C:\\x\" />\r\n"
            + $"  <one:Notebook name=\"MCP Test\" ID=\"{NbId}\" path=\"C:\\y\" />\r\n"
            + "  <one:UnfiledNotes><one:Section name=\"Quick Notes\" /></one:UnfiledNotes>\r\n"
            + "  <one:OpenSections><one:Section name=\"Loose\" /></one:OpenSections>\r\n"
            + "</one:Notebooks>";

        var kept = FixtureSanitiser.KeepOnlyNotebook(xml, NbId);

        Assert.Equal(
            "<?xml version=\"1.0\"?>\r\n"
            + $"<one:Notebooks xmlns:one=\"{Ns}\">\r\n"
            + $"  <one:Notebook name=\"MCP Test\" ID=\"{NbId}\" path=\"C:\\y\" />\r\n"
            + "</one:Notebooks>",
            kept);
    }

    [Fact]
    public void KeepOnlyNotebook_RefusesWhenMcpTestIsAbsent()
    {
        Assert.Throws<InvalidOperationException>(() => FixtureSanitiser.KeepOnlyNotebook($"<one:Notebooks xmlns:one=\"{Ns}\" />", NbId));
    }

    [Theory]
    [InlineData(@"path=""C:\Users\Jo\AppData\x""", @"path=""C:\Users\mcp-test\AppData\x""")]
    [InlineData(@"path=""c:\users\jo\AppData\x""", @"path=""C:\Users\mcp-test\AppData\x""")]
    [InlineData("onenote:///C:/Users/Jo/x.one", "onenote:///C:/Users/mcp-test/x.one")]
    [InlineData("onenote:///C:%5CUsers%5CJo%5Cx.one", "onenote:///C:%5CUsers%5Cmcp-test%5Cx.one")]
    [InlineData(@"author=""Jo Smith"" authorInitials=""JS"" lastModifiedBy=""Jo"" lastModifiedByInitials=""J""", @"author=""MCP Test"" authorInitials=""MT"" lastModifiedBy=""MCP Test"" lastModifiedByInitials=""MT""")]
    [InlineData(@"authorResolutionID=""&lt;x&gt;"" lastModifiedByResolutionID=""y""", @"authorResolutionID=""MCP Test"" lastModifiedByResolutionID=""MCP Test""")]
    [InlineData(@"coauthor=""keep""", @"coauthor=""keep""")]
    public void Scrub_ReplacesProfileAndAuthors(string input, string expected)
    {
        Assert.Equal(expected, FixtureSanitiser.Scrub(input, @"C:\Users\Jo"));
    }

    [Fact]
    public void SanitiseCloud_MapsGuidsConsistently_ReplacesText_KeepsData()
    {
        var pageId = "{AAAAAAAA-0000-0000-0000-000000000001}{1}{E1}";
        var xml = "<?xml version=\"1.0\"?>\r\n"
            + $"<one:Page xmlns:one=\"{Ns}\" ID=\"{pageId}\" name=\"Secret title\" author=\"Jo\">"
            + "<one:Meta name=\"k\" content=\"secret\" />"
            + "<one:Title><one:OE objectID=\"{AAAAAAAA-0000-0000-0000-000000000001}{15}{B0}\"><one:T><![CDATA[Secret title]]></one:T></one:OE></one:Title>"
            + "<one:Image alt=\"secret alt\"><one:CallbackID callbackID=\"{BBBBBBBB-0000-0000-0000-000000000002}{1}{B0}\" /><one:OCRData><one:OCRText><![CDATA[secret ocr]]></one:OCRText></one:OCRData></one:Image>"
            + "<one:Image><one:Data>QUJD</one:Data></one:Image>"
            + "</one:Page>";
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        var clean = FixtureSanitiser.SanitiseCloud(xml, map);

        Assert.DoesNotContain("ecret", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AAAAAAAA", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("BBBBBBBB", clean, StringComparison.Ordinal);
        Assert.Equal(2, map.Keys.Count(k => k.StartsWith("guid:", StringComparison.Ordinal))); // the page's GUID is shared by its objects, and stays shared
        Assert.DoesNotContain("{15}", clean, StringComparison.Ordinal); // counter segments are mapped too, keeping their length
        Assert.Contains("ID=\"{C10D0000-0000-4000-8000-000000000001}{1}{E1}\"", clean, StringComparison.Ordinal);
        Assert.Contains("objectID=\"{C10D0000-0000-4000-8000-000000000001}{12}{B2}\"", clean, StringComparison.Ordinal);
        Assert.Contains("<one:Data>QUJD</one:Data>", clean, StringComparison.Ordinal);
        Assert.Contains("name=\"Cloud Test Page\"", clean, StringComparison.Ordinal);
        Assert.Contains("<one:Meta name=\"k\" content=\"cloud-placeholder\" />", clean, StringComparison.Ordinal);
        Assert.StartsWith("<?xml version=\"1.0\"?>\r\n<one:Page", clean, StringComparison.Ordinal);
        Assert.Equal("{C10D0000-0000-4000-8000-000000000001}{1}{E1}", FixtureSanitiser.MapGuids(pageId, map));
    }

    [Theory]
    [InlineData("https://d.docs.live.net/ABC123/Documents/Jo's Notebook/Quick Notes.one", "https://d.docs.live.net/0000000000000000/Documents/MCP Cloud Notebook/Quick Notes.one")]
    [InlineData(@"C:\Users\mcp-test\Documents\OneNote Notebooks", @"C:\Users\mcp-test\Documents\OneNote Notebooks")]
    public void ScrubCloudUrl_KeepsShape_DropsAccountAndFolders(string input, string expected)
    {
        Assert.Equal(expected, FixtureSanitiser.ScrubCloudUrl(input));
    }

    [Fact]
    public void MapGuids_MapsTheLongThirdSegment_KeepingItsShape()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var real = "{AAAAAAAA-0000-0000-0000-000000000001}{10}{E1862149849007297801119697583047006248504914}";

        var mapped = FixtureSanitiser.MapGuids(real, map);

        Assert.Equal("{C10D0000-0000-4000-8000-000000000001}{11}{E1000000000000000000000000000000000000000001}", mapped);
        Assert.Equal(mapped, FixtureSanitiser.MapGuids(real, map));
        Assert.Equal(real.Length, mapped.Length);
    }

    [Fact]
    public void EnsureClean_RefusesTheUserName()
    {
        Assert.Throws<InvalidOperationException>(() => FixtureSanitiser.EnsureClean("Jo's Notebook", "jo's", "f.xml"));
        FixtureSanitiser.EnsureClean("MCP Test", "jo's", "f.xml");
    }

    [Fact]
    public void ChangedAttributesOnUntouched_ChecksEverythingButTheEditedOe_IdLessElementsToo()
    {
        var before = $"<one:Page xmlns:one=\"{Ns}\" lastModifiedTime=\"1\">"
            + "<one:Outline objectID=\"A\" lastModifiedTime=\"1\"><one:OEChildren><one:OE objectID=\"A1\" lastModifiedTime=\"1\" /><one:OE objectID=\"A2\" lastModifiedTime=\"1\" /></one:OEChildren></one:Outline>"
            + "<one:Outline objectID=\"B\" selected=\"all\"><one:OEChildren><one:OE objectID=\"B1\"><one:T>b</one:T></one:OE></one:OEChildren></one:Outline></one:Page>";
        var after = $"<one:Page xmlns:one=\"{Ns}\" lastModifiedTime=\"2\">"
            + "<one:Outline objectID=\"A\" lastModifiedTime=\"2\"><one:OEChildren><one:OE objectID=\"A1\" lastModifiedTime=\"2\" /><one:OE objectID=\"A2\" lastModifiedTime=\"2\" /></one:OEChildren></one:Outline>"
            + "<one:Outline objectID=\"B\"><one:OEChildren><one:OE objectID=\"B1\" newThing=\"x\"><one:T lang=\"fr\">b</one:T></one:OE></one:OEChildren></one:Outline></one:Page>";

        Assert.Equal(
            ["OE@lastModifiedTime", "OE@newThing", "Outline@lastModifiedTime", "Outline@selected", "Page@lastModifiedTime", "T@lang"],
            FixtureCapture.ChangedAttributesOnUntouched(before, after, "A1"));
    }
}
