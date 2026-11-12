using System.Xml.Linq;
using OneNoteMcp.Com.Interop;
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

    // --- Story 5.1 ------------------------------------------------------------------------------------------------

    [Fact]
    public void Hierarchy_BuildsAnUpdateHierarchyDocument()
    {
        var xml = ProbeXml.Hierarchy("SectionGroup", "{G}", ProbeXml.A("name", "a \"b\" & c"), ProbeXml.Node("Section", "{S}"), ProbeXml.Node("Section", null, ProbeXml.A("name", "new")));
        var root = XDocument.Parse(xml).Root!;

        Assert.Equal(ProbeXml.One + "SectionGroup", root.Name);
        Assert.Equal("{G}", (string?)root.Attribute("ID"));
        Assert.Equal("a \"b\" & c", (string?)root.Attribute("name"));
        Assert.Equal(["{S}", null], root.Elements().Select(e => (string?)e.Attribute("ID")).ToArray());
        Assert.Equal("new", (string?)root.Elements().Last().Attribute("name"));
        Assert.EndsWith("/>", ProbeXml.Hierarchy("Section", "{S}", ProbeXml.A("name", "x")), StringComparison.Ordinal);
    }

    private const string BinTree = $$"""
        <one:Notebook xmlns:one="{{Ns}}" name="nb" ID="{N}">
          <one:Section name="a" ID="{A}" path="C:\nb\a.one" encrypted="true" locked="true" lastModifiedTime="2026-11-10T03:19:04.000Z"><one:Page ID="{P}" name="p" /></one:Section>
          <one:SectionGroup name="g" ID="{G}"><one:Section name="b" ID="{B}" /></one:SectionGroup>
          <one:SectionGroup name="OneNote_RecycleBin" ID="{RB}" isRecycleBin="true">
            <one:Section name="Deleted Pages" ID="{DP}" isInRecycleBin="true" isDeletedPages="true"><one:Page ID="{OLD}" name="p" /></one:Section>
            <one:Section name="c" ID="{C}" isInRecycleBin="true" />
          </one:SectionGroup>
        </one:Notebook>
        """;

    [Fact]
    public void NodeInfo_ReadsTheNodeAndSeesTheRecycleBin()
    {
        var root = XDocument.Parse(BinTree).Root!;
        var a = NodeInfo.From(ProbeXml.ById(root, "{A}")!);
        var oldPage = NodeInfo.From(ProbeXml.ById(root, "{OLD}")!);

        Assert.Equal(("Section", "a", "Notebook", "nb", false, "true", "true", 1), (a.Kind, a.Name, a.ParentKind, a.ParentName, a.InRecycleBin, a.Encrypted, a.Locked, a.Pages));
        Assert.True(oldPage.InRecycleBin);
        Assert.Equal("Deleted Pages", oldPage.ParentName);
        Assert.True(NodeInfo.From(ProbeXml.ById(root, "{C}")!).InRecycleBin);
    }

    [Fact]
    public void Children_LeavesOutTheRecycleBin() =>
        Assert.Equal(["a", "g"], ProbeXml.Children(XDocument.Parse(BinTree).Root!).Select(c => c.Name).ToArray());

    [Fact]
    public void Summary_ListsPageNames_RecycleBinLeftOut()
    {
        var summary = HierarchyProbes.Summary(XDocument.Parse(BinTree).Root!);

        Assert.Equal(["p"], summary.PageNames);
        Assert.Equal(("Notebook", "nb"), (summary.Kind, summary.Name));
    }

    [Fact]
    public void PublishTries_PfOneNoteFirst_PackageLast()
    {
        Assert.Equal(
            [(PublishFormat.pfOneNote, "x.one"), (PublishFormat.pfOneNotePackage, "x.onepkg")],
            HierarchyProbes.SectionTries("x"));
        Assert.Equal(
            [(PublishFormat.pfOneNote, "x.one"), (PublishFormat.pfOneNote, "x"), (PublishFormat.pfOneNotePackage, "x.onepkg")],
            HierarchyProbes.GroupTries("x"));
    }

    [Fact]
    public void Disk_ReportsFileFolderAbsentAndUrl()
    {
        var folder = Path.Combine(Path.GetTempPath(), "onenote-mcp-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "a.one");
            File.WriteAllText(file, "abc");

            Assert.Contains("\"kind\":\"file\"", Json(ProbeSession.Disk(file)), StringComparison.Ordinal);
            Assert.Contains("\"kind\":\"folder\"", Json(ProbeSession.Disk(folder)), StringComparison.Ordinal);
            Assert.Contains("\"kind\":\"absent\"", Json(ProbeSession.Disk(Path.Combine(folder, "b.one"))), StringComparison.Ordinal);
            Assert.Contains("\"local\":false", Json(ProbeSession.Disk("https://d.docs.live.net/0123456789ABCDEF/Documents/x/")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        static string Json(object value) => System.Text.Json.JsonSerializer.Serialize(value);
    }

    // --- Story 5.1, after run 1: what must be off screen before a hierarchy-level call ------------------------------

    private const string ShownTree = $$"""
        <one:Notebook xmlns:one="{{Ns}}" name="MCP Test" ID="{N}">
          <one:Section name="Seed" ID="{SEED}"><one:Page ID="{SP}" name="seed" /></one:Section>
          <one:SectionGroup name="probe run 1111-0331" ID="{RUN}">
            <one:SectionGroup name="probe dates 1111-0331" ID="{G}">
              <one:Section name="probe dates s 1111-0331" ID="{S}"><one:Page ID="{P}" name="probe date shown stale" isCurrentlyViewed="true" /></one:Section>
              <one:Section name="probe dates sec stale 1111-0331" ID="{SEC}" />
            </one:SectionGroup>
          </one:SectionGroup>
          <one:SectionGroup name="OneNote_RecycleBin" ID="{RB}" isRecycleBin="true">
            <one:Section name="probe vault 1111-0331" ID="{V}" isInRecycleBin="true" isCurrentlyViewed="true" />
          </one:SectionGroup>
        </one:Notebook>
        """;

    [Fact]
    public void FindProbeArea_IsTheRunGroup_SoASiblingsShownPageCounts()
    {
        var (area, target) = ProbeSession.FindProbeArea(XDocument.Parse(ShownTree).Root!, "{SEC}");

        Assert.Equal("{RUN}", (string?)area!.Attribute("ID"));
        Assert.Equal("{SEC}", (string?)target!.Attribute("ID"));
        Assert.Equal(["probe date shown stale"], ProbeSession.Shown(area));
    }

    [Fact]
    public void FindProbeArea_RecycledProbeSection_IsItself_AndTheNotebookRootHasNone()
    {
        var root = XDocument.Parse(ShownTree).Root!;

        Assert.Equal("{V}", (string?)ProbeSession.FindProbeArea(root, "{V}").Area!.Attribute("ID"));
        Assert.Null(ProbeSession.FindProbeArea(root, "{N}").Area); // no probe area, but the shown check now covers the whole notebook
        Assert.Null(ProbeSession.FindProbeArea(root, "{SEED}").Area); // a fixture is never a probe area
        Assert.Equal((null, null), ProbeSession.FindProbeArea(root, "{ELSEWHERE}"));
    }

    [Fact]
    public void Shown_OnTheNotebook_CountsAFixturePageOutsideEveryProbeArea()
    {
        // The 15:09 retry blocked creating the run group at MCP Test's root while only Seed was on screen.
        var root = XDocument.Parse(ShownTree.Replace("""name="seed" />""", """name="seed" isCurrentlyViewed="true" />""", StringComparison.Ordinal)).Root!;

        Assert.Contains("seed", ProbeSession.Shown(root));
    }

    [Fact]
    public void SafePage_IsAlwaysInTheOtherTestNotebook()
    {
        Assert.StartsWith("MCP Test Cloud >", ProbeSession.SafePage(cloud: false), StringComparison.Ordinal);
        Assert.StartsWith("MCP Test >", ProbeSession.SafePage(cloud: true), StringComparison.Ordinal);
    }

    [Fact]
    public void FindProbeArea_ThrowawayNotebook_IsTheWholeNotebook()
    {
        var root = XDocument.Parse($$"""<one:Notebook xmlns:one="{{Ns}}" name="MCP Test Throwaway 1111 a" ID="{A}"><one:Section name="probe nb a" ID="{X}" /></one:Notebook>""").Root!;

        Assert.Equal("{A}", (string?)ProbeSession.FindProbeArea(root, "{X}").Area!.Attribute("ID"));
        Assert.Equal("{A}", (string?)ProbeSession.FindProbeArea(root, "{A}").Area!.Attribute("ID"));
    }

    [Fact]
    public void RootId_ReadsTheRoot_OrNullWhenMalformed()
    {
        Assert.Equal("{G}", ProbeSession.RootId(ProbeXml.Hierarchy("SectionGroup", "{G}", string.Empty, ProbeXml.Node("Section", "{S}"))));
        Assert.Null(ProbeSession.RootId($$"""<one:Section xmlns:one="{{Ns}}" ID="{S}">"""));
    }

    [Fact]
    public void ProbeCallSeconds_IsShort() => Assert.Equal(60, ProbeSession.ProbeCallSeconds);

    [Fact]
    public void CloudProbeCallSeconds_AllowsAOneDriveRoundTrip() => Assert.Equal(180, ProbeSession.CloudProbeCallSeconds);

    [Fact]
    public void Blocked_IsATimeout_OrABusyThatTookTheWholeTimeout()
    {
        Assert.True(ProbeSession.Blocked(new Attempt("error", null, null, "Timeout", 60_000, null)));
        Assert.True(ProbeSession.Blocked(new Attempt("error", null, null, "ComBusy", 59_400, null)));
        Assert.False(ProbeSession.Blocked(new Attempt("error", null, null, "ComBusy", 2_000, null))); // a quick busy (hrNotYetSynchronized) isn't a block
        Assert.False(ProbeSession.Blocked(new Attempt("error", null, null, "ComBusy", 59_400, null), ProbeSession.CloudProbeCallSeconds)); // under the cloud limit
        Assert.False(ProbeSession.Blocked(new Attempt("error", "0x80042010", null, "StaleWrite", 100, null)));
        Assert.False(ProbeSession.Blocked(new Attempt("ok", null, null, null, 120, null)));
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
