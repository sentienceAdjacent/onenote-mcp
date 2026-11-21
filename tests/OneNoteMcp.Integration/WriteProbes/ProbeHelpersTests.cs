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

    // --- Spike 3.1: the label scenario's URL redaction --------------------------------------------------------------

    // A made-up tenant; the real one never lands in the repo.
    private const string TenantPath = "https://contoso-my.sharepoint.com/sites/probes/Documents/Notebooks/MCP Test Tenant/";

    [Theory]
    [InlineData("""{"path":"https://contoso-my.sharepoint.com/sites/probes/Documents/MCP Test Tenant/probe label.one","x":1}""", """{"path":"tenant notebook","x":1}""")] // spaces inside a JSON value
    [InlineData("""{"path":"https://contoso.sharepoint.com/sites/a\u0026b/MCP Test Tenant/"}""", """{"path":"tenant notebook"}""")] // a JSON \u escape
    [InlineData("""{"m":"see https://contoso.sharepoint.com/x\"quoted\" here"}""", """{"m":"see tenant notebook\"quoted\" here"}""")] // stops before an escaped quote: still valid JSON
    [InlineData("PAUSE [x / y]: open HTTPS://Contoso.SharePoint.com/a b. Then continue.", "PAUSE [x / y]: open tenant notebook")] // free text: the rest of the line goes too
    [InlineData("""{"path":"https://d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Cloud/"}""", """{"path":"tenant notebook"}""")] // OneDrive personal too
    [InlineData(@"C:\Users\me\AppData\Local\OneNoteMcp-Test\probes\publish\x.one", @"C:\Users\me\AppData\Local\OneNoteMcp-Test\probes\publish\x.one")] // local paths stay
    [InlineData("{8E1AB522-1370-4579-98BC-7032B2A95A5E}{1}{B0}", "{8E1AB522-1370-4579-98BC-7032B2A95A5E}{1}{B0}")] // IDs stay
    public void RedactLine_WithUrlsOn_ReplacesEveryUrl(string input, string expected) =>
        Assert.Equal(expected, ProbeRecorder.RedactLine(input, urls: true, []));

    [Fact]
    public void RedactLine_WithUrlsOff_OnlyRedactsTheAccountId() =>
        Assert.Equal(
            """{"a":"https://contoso.sharepoint.com/MCP Test Tenant/","b":"https://d.docs.live.net/<cid>/Documents/"}""",
            ProbeRecorder.RedactLine("""{"a":"https://contoso.sharepoint.com/MCP Test Tenant/","b":"https://d.docs.live.net/0123456789ABCDEF/Documents/"}""", urls: false, []));

    [Fact]
    public void RedactUrls_KnownTenantPath_IsReplacedInAnyForm()
    {
        const string Unc = @"\\contoso-my.sharepoint.com@SSL\DavWWWRoot\personal\someone\MCP Test Tenant\";
        Assert.Equal("at tenant notebook\\probe label.one", ProbeRecorder.RedactUrls(@"at " + Unc + "probe label.one", [Unc]));
        Assert.Equal("""{"p":"tenant notebook"}""", ProbeRecorder.RedactUrls("""{"p":"\\\\contoso-my.sharepoint.com@SSL\\DavWWWRoot\\personal\\someone\\MCP Test Tenant"}""", [Unc])); // JSON-escaped, no trailing separator
        Assert.Equal("""{"p":"tenant notebook"}""", ProbeRecorder.RedactUrls($$"""{"p":"{{TenantPath}}"}""", [TenantPath]));
    }

    // --- Spike 3.1: landing poll, fresh folders, packages, merges ----------------------------------------------------

    private static FileEntry[] Files(params (string Path, long Bytes)[] entries) => entries.Select(e => new FileEntry(e.Path, e.Bytes)).ToArray();

    [Fact]
    public void LandingTracker_FirstSeen_Growth_ThenStableAfterASecondUnchanged()
    {
        var tracker = new LandingTracker("x.docx");
        Assert.False(tracker.Observe([], 0));
        Assert.False(tracker.Observe(Files(("x.docx", 0)), 250)); // appears empty
        Assert.False(tracker.Observe(Files(("x.docx", 4_000)), 500)); // grows
        Assert.False(tracker.Observe(Files(("x.docx", 9_000)), 750));
        Assert.False(tracker.Observe(Files(("x.docx", 9_000)), 1_500)); // unchanged for 750 ms only
        Assert.True(tracker.Observe(Files(("x.docx", 9_000)), 1_750)); // a second unchanged

        var result = tracker.Result(1_750);
        Assert.Equal(250, result.FirstSeenMs);
        Assert.Equal(750, result.StableMs);
        Assert.True(result.Stable && result.TargetExists);
        Assert.Equal(9_000, result.TargetBytes);
        Assert.Equal([null, 0L, 4_000L, 9_000L], result.Changes.Select(ch => ch.TargetBytes));
    }

    [Fact]
    public void LandingTracker_AnEmptyFileOrNothing_NeverSettles()
    {
        var tracker = new LandingTracker("x.onepkg");
        Assert.False(tracker.Observe([], 0));
        Assert.False(tracker.Observe([], 5_000)); // nothing landed
        Assert.False(tracker.Observe(Files(("x.onepkg", 0)), 6_000));
        Assert.False(tracker.Observe(Files(("x.onepkg", 0)), 9_000)); // still empty

        var result = tracker.Result(9_000);
        Assert.False(result.Stable);
        Assert.Equal(6_000, result.FirstSeenMs);
        Assert.Equal(0, result.TargetBytes);
    }

    [Fact]
    public void LandingTracker_OtherFiles_CountButTheTargetIsNamed()
    {
        var tracker = new LandingTracker("x.htm");
        tracker.Observe(Files(("x_files", -1), ("x_files\\image001.png", 120)), 0); // a folder and an image, no target yet
        Assert.True(tracker.Observe(Files(("x_files", -1), ("x_files\\image001.png", 120)), 1_000));

        var result = tracker.Result(1_000);
        Assert.False(result.TargetExists);
        Assert.Null(result.TargetBytes);
        Assert.Equal(1, result.Changes[0].Files); // folders aren't counted as files
    }

    [Fact]
    public void TimeoutProbe_TargetGrew_OnlyWhenTheTargetItselfGetsBigger()
    {
        Assert.True(Round2TimeoutProbe.TargetGrew([new(0, 0, 0, null), new(250, 1, 100, 100), new(500, 1, 900, 900)]));
        Assert.False(Round2TimeoutProbe.TargetGrew([new(0, 1, 900, 900), new(250, 2, 1_500, 900)])); // another file grew, the target didn't
        Assert.False(Round2TimeoutProbe.TargetGrew([new(0, 1, 900, 900)])); // already whole at the first poll
        Assert.False(Round2TimeoutProbe.TargetGrew([new(0, 1, 900, 900), new(250, 1, 100, 100)])); // shrank
        Assert.False(Round2TimeoutProbe.TargetGrew([]));
    }

    [Fact]
    public void FreshFolder_NewEachTime_AndOnlyASingleName()
    {
        var root = Path.Combine(Path.GetTempPath(), "onenote-mcp-probes-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(Path.Combine(root, "x1"), ProbeSession.FreshFolder(root, "x1"));
            Assert.Equal(Path.Combine(root, "x1-2"), ProbeSession.FreshFolder(root, "x1"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "x1-2")));
            Assert.Throws<InvalidOperationException>(() => ProbeSession.FreshFolder(root, @"..\x"));
            Assert.Throws<InvalidOperationException>(() => ProbeSession.FreshFolder(root, @"a\b"));
            Assert.Throws<InvalidOperationException>(() => ProbeSession.FreshFolder(root, " "));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Snapshot_ListsFilesAndFoldersRelative_Sorted()
    {
        var root = Path.Combine(Path.GetTempPath(), "onenote-mcp-probes-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "x_files"));
        File.WriteAllText(Path.Combine(root, "x_files", "a.png"), "12");
        File.WriteAllText(Path.Combine(root, "x.htm"), "1234");
        try
        {
            Assert.Equal(Files(("x.htm", 4), ("x_files", -1), (Path.Combine("x_files", "a.png"), 2)), ProbeSession.Snapshot(root));
            Assert.Empty(ProbeSession.Snapshot(Path.Combine(root, "absent")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractPackage_RefusesPathsOutsideTheRoot_AndAFolderInUse()
    {
        var root = Path.Combine(Path.GetTempPath(), "onenote-mcp-probes-test", Guid.NewGuid().ToString("N"));
        var used = Path.Combine(root, "used");
        Directory.CreateDirectory(used);
        File.WriteAllText(Path.Combine(used, "keep.txt"), "x");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProbeSession.ExtractPackageAsync(@"D:\Elsewhere\x.onepkg", Path.Combine(root, "out"), root));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProbeSession.ExtractPackageAsync(Path.Combine(root, "x.onepkg"), @"D:\Elsewhere\out", root));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProbeSession.ExtractPackageAsync(Path.Combine(root, "x.onepkg"), used, root));
            Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(used).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NewPageIds_AreTheDestinationsPagesNotThereBefore()
    {
        var section = XElement.Parse($$"""<one:Section xmlns:one="{{Ns}}" ID="{D}"><one:Page ID="{D1}" /><one:Page ID="{M2}" /><one:Page ID="{M1}" /></one:Section>""");

        Assert.Equal(["{D1}", "{M1}", "{M2}"], ProbeSession.PageIds(section).Order(StringComparer.Ordinal));
        Assert.Equal(["{M1}", "{M2}"], ProbeSession.NewPageIds(new HashSet<string> { "{D1}" }, section));
    }

    [Fact]
    public void RecycleBin_IsTheBinGroupAndItsDeletedPages()
    {
        var notebook = XElement.Parse($$"""
            <one:Notebook xmlns:one="{{Ns}}" ID="{T}">
              <one:SectionGroup name="g" ID="{G}" />
              <one:SectionGroup name="OneNote_RecycleBin" ID="{BIN}" isRecycleBin="true">
                <one:Section name="Deleted Pages" ID="{DP}" isDeletedPages="true" />
                <one:Section name="probe x" ID="{RS}" isInRecycleBin="true" />
              </one:SectionGroup>
            </one:Notebook>
            """);

        var (bin, deleted) = ProbeSession.RecycleBin(notebook);
        Assert.Equal("{BIN}", bin);
        Assert.Equal(["{DP}"], deleted);
        Assert.Null(ProbeSession.RecycleBin(XElement.Parse($$"""<one:Notebook xmlns:one="{{Ns}}" ID="{T}" />""")).Bin);
    }

    // --- Spike 3.1: the scenarios' pure parts --------------------------------------------------------------------

    [Fact]
    public void X1Formats_AreEveryFormatButPfOneNote_AndAllFormatsAddIt()
    {
        Assert.Equal(8, Round2ExportProbes.X1Formats.Length);
        Assert.DoesNotContain(PublishFormat.pfOneNote, Round2ExportProbes.X1Formats);
        Assert.Equal(Enum.GetValues<PublishFormat>().Order(), Round2ExportProbes.AllFormats.Order());
        Assert.Equal(9, Round2ExportProbes.AllFormats.Select(Round2ExportProbes.Extension).Count()); // every format has an extension
        Assert.Equal(".onepkg", Round2ExportProbes.Extension(PublishFormat.pfOneNotePackage));
        Assert.Equal(".docx", Round2ExportProbes.Extension(PublishFormat.pfWord));
    }

    [Fact]
    public void PageLevelXml_SetsOnlyTheLevel()
    {
        var page = XElement.Parse(Round2.PageLevelXml("{P}", 2));

        Assert.Equal(XName.Get("Page", Ns), page.Name);
        Assert.Equal("{P}", (string?)page.Attribute("ID"));
        Assert.Equal("2", (string?)page.Attribute("pageLevel"));
        Assert.Empty(page.Elements());
    }

    [Fact]
    public void PageSpot_ReadsSectionPositionLevelAndRecycleBin()
    {
        var notebook = XElement.Parse($$"""
            <one:Notebook xmlns:one="{{Ns}}" ID="{T}">
              <one:Section name="s" ID="{S}"><one:Page ID="{A}" name="a" /><one:Page ID="{B}" name="b" pageLevel="2" lastModifiedTime="2026-11-15T10:00:00.000Z" dateTime="2026-11-15T09:00:00.000Z" /></one:Section>
              <one:SectionGroup name="OneNote_RecycleBin" ID="{BIN}" isRecycleBin="true"><one:Section name="Deleted Pages" ID="{DP}"><one:Page ID="{R}" name="a" /></one:Section></one:SectionGroup>
            </one:Notebook>
            """);
        var pages = notebook.Descendants(XName.Get("Page", Ns)).ToList();

        var b = PageSpot.From("local", pages[1]);
        Assert.Equal(("{S}", "s", 2, "2", false), (b.SectionId, b.SectionName, b.Position, b.PageLevel, b.InRecycleBin));
        Assert.Equal("2026-11-15T09:00:00.000Z", b.DateTime);
        Assert.True(PageSpot.From("local", pages[2]).InRecycleBin);
    }

    [Fact]
    public void LabelProbe_FirstSection_SkipsTheRecycleBin() =>
        Assert.Equal("{S}", Round2LabelProbe.FirstSection(XElement.Parse($$"""
            <one:Notebook xmlns:one="{{Ns}}" ID="{T}">
              <one:SectionGroup name="OneNote_RecycleBin" ID="{BIN}" isRecycleBin="true"><one:Section name="Deleted Pages" ID="{DP}" /></one:SectionGroup>
              <one:Section name="probe label" ID="{S}" />
            </one:Notebook>
            """)));

    // --- Spike 4.1 ------------------------------------------------------------------------------------------------

    // A made-up %LOCALAPPDATA%; the real user name never lands in the repo.
    private const string LocalAppData = @"C:\Users\someone\AppData\Local";

    [Theory]
    [InlineData(@"C:\Users\someone\AppData\Local\OneNoteMcp-Test\probes\app-state\1116-120000\o1", @"%LOCALAPPDATA%\OneNoteMcp-Test\probes\app-state\1116-120000\o1")]
    [InlineData(@"c:\users\SOMEONE\appdata\local\x", @"%LOCALAPPDATA%\x")] // ignoring case
    [InlineData("""{"path":"C:\\Users\\someone\\AppData\\Local\\OneNoteMcp-Test\\x"}""", """{"path":"%LOCALAPPDATA%\\OneNoteMcp-Test\\x"}""")] // JSON-escaped
    [InlineData(@"\\localhost\C$\Users\someone\AppData\Local\OneNoteMcp-Test\x", @"\\localhost\<drive>$\%LOCALAPPDATA%\OneNoteMcp-Test\x")]
    [InlineData("""{"path":"\\\\localhost\\C$\\Users\\someone\\AppData\\Local\\x"}""", """{"path":"\\\\localhost\\<drive>$\\%LOCALAPPDATA%\\x"}""")]
    [InlineData(@"\\localhost\D$\Other\x", @"\\localhost\<drive>$\Other\x")] // any admin share
    [InlineData("""{"p":"\\\\LOCALHOST\\d$\\Other"}""", """{"p":"\\\\localhost\\<drive>$\\Other"}""")]
    [InlineData(@"C:\Users\other\AppData\Local\x", @"C:\Users\other\AppData\Local\x")] // another user's folder isn't this machine's
    [InlineData(@"\\server\C$\x", @"\\server\C$\x")]
    public void Redact_LocalAppDataAndAdminShares(string input, string expected) =>
        Assert.Equal(expected, ProbeRecorder.Redact(input, LocalAppData));

    [Fact]
    public void Redact_KeepsAJsonRowValid()
    {
        var row = System.Text.Json.JsonSerializer.Serialize(new { path = @"\\localhost\C$\Users\someone\AppData\Local\OneNoteMcp-Test\o1", other = @"C:\Users\someone\AppData\Local\x", cloud = "https://d.docs.live.net/0123456789ABCDEF/Documents/x/" });
        var redacted = ProbeRecorder.Redact(row, LocalAppData);

        using var doc = System.Text.Json.JsonDocument.Parse(redacted);
        Assert.Equal(@"\\localhost\<drive>$\%LOCALAPPDATA%\OneNoteMcp-Test\o1", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(@"%LOCALAPPDATA%\x", doc.RootElement.GetProperty("other").GetString());
        Assert.Equal("https://d.docs.live.net/<cid>/Documents/x/", doc.RootElement.GetProperty("cloud").GetString());
        Assert.DoesNotContain("someone", redacted, StringComparison.OrdinalIgnoreCase);
    }

    private static FillPoint Point(long ms, int? sections, int? pages, string? error = null) => new(ms, sections, pages, error);

    [Fact]
    public void FillSchedule_FixedReadsFirst()
    {
        var points = new List<FillPoint>();
        var times = new List<long>();
        while (FillSchedule.NextAtMs(points, 30_000) is { } at && points.Count < 7)
        {
            times.Add(at);
            points.Add(Point(at, 1, 1));
        }

        Assert.Equal([0L, 500, 1000, 2000, 4000, 8000, 16000], times);
    }

    [Fact]
    public void FillSchedule_StopsAfterTheFixedReads_WhenTheLastThreeMatch()
    {
        var points = FillSchedule.FixedMs.Select((ms, i) => Point(ms, i < 3 ? 0 : 2, i < 3 ? 0 : 5)).ToList();

        Assert.True(FillSchedule.Settled(points));
        Assert.Null(FillSchedule.NextAtMs(points, 180_000));
        Assert.True(FillSchedule.Settled(points, (2, 5))); // back to the counts before the close
        Assert.False(FillSchedule.Settled(points, (2, 9))); // a stall below them isn't settled
        Assert.Equal(26_000, FillSchedule.NextAtMs(points, 180_000, (2, 9)));
    }

    [Fact]
    public void FillSchedule_EmptyReads_NeverSettle_TheSeriesRunsToTheLimit()
    {
        var points = FillSchedule.FixedMs.Select(ms => Point(ms, 0, 0)).ToList(); // a cloud notebook not yet filled

        Assert.False(FillSchedule.Settled(points));
        while (FillSchedule.NextAtMs(points, 180_000) is { } at)
        {
            points.Add(Point(at, 0, 0));
        }

        Assert.Equal(176_000, points[^1].Ms); // every 10 s after 16 s, up to the 180 s limit
        Assert.False(FillSchedule.Settled(points));
    }

    [Fact]
    public void FillSchedule_EveryTenSeconds_UntilThreeMatch_NeverPastTheLimit()
    {
        var points = FillSchedule.FixedMs.Select((ms, i) => Point(ms, i, i)).ToList(); // still filling

        Assert.False(FillSchedule.Settled(points));
        Assert.Equal(26_000, FillSchedule.NextAtMs(points, 30_000));
        points.Add(Point(26_000, 9, 9));
        Assert.Null(FillSchedule.NextAtMs(points, 30_000)); // 36 s is past the local limit
        Assert.Equal(36_000, FillSchedule.NextAtMs(points, 180_000)); // the cloud's 180 s allows it
        points.Add(Point(36_000, 9, 9));
        Assert.Equal(46_000, FillSchedule.NextAtMs(points, 180_000)); // two match; a third is needed
        points.Add(Point(46_000, 9, 9));
        Assert.Null(FillSchedule.NextAtMs(points, 180_000));
    }

    [Fact]
    public void FillSchedule_ErrorsNeverSettle()
    {
        Assert.False(FillSchedule.Settled([Point(0, null, null, "ObjectNotFound"), Point(500, null, null, "ObjectNotFound"), Point(1000, null, null, "ObjectNotFound")]));
        Assert.False(FillSchedule.Settled([Point(0, 1, 1), Point(500, 1, 1)])); // two reads aren't three
    }

    [Fact]
    public void FillSchedule_Count_LeavesOutTheRecycleBin()
    {
        var notebook = XElement.Parse($$"""
            <one:Notebook xmlns:one="{{Ns}}" ID="{N}">
              <one:Section ID="{S1}"><one:Page ID="{P1}" /><one:Page ID="{P2}" /><one:Page ID="{P3}" isInRecycleBin="true" /></one:Section>
              <one:SectionGroup ID="{G}"><one:Section ID="{S2}"><one:Page ID="{P4}" /></one:Section></one:SectionGroup>
              <one:SectionGroup ID="{BIN}" isRecycleBin="true"><one:Section ID="{DP}" isDeletedPages="true"><one:Page ID="{P5}" /></one:Section></one:SectionGroup>
            </one:Notebook>
            """);

        Assert.Equal((2, 3), FillSchedule.Count(notebook));
    }

    private static readonly XElement ViewNotebooks = XElement.Parse($$"""
        <one:Notebooks xmlns:one="{{Ns}}">
          <one:Notebook name="Personal Diary" ID="{P}" path="D:\Notebooks\Personal Diary" isCurrentlyViewed="true" />
          <one:Notebook name="MCP Test" ID="{T}" path="C:\x\MCP Test" />
        </one:Notebooks>
        """);

    [Fact]
    public void ViewSnapshot_AnotherNotebook_IsNeverNamed()
    {
        var view = ProbeView.Snapshot(42, "ONENOTE", ViewNotebooks, null, null);

        Assert.Equal(new ViewSnapshot(42, "ONENOTE", ProbeView.OtherNotebook, null, null), view);
    }

    [Fact]
    public void ViewSnapshot_ATestNotebook_NamesTheChain()
    {
        var notebooks = XElement.Parse(ViewNotebooks.ToString().Replace("isCurrentlyViewed=\"true\" ", string.Empty, StringComparison.Ordinal).Replace("ID=\"{T}\"", "ID=\"{T}\" isCurrentlyViewed=\"true\"", StringComparison.Ordinal));
        var tree = XElement.Parse($$"""
            <one:Notebook xmlns:one="{{Ns}}" name="MCP Test" ID="{T}" isCurrentlyViewed="true">
              <one:Section name="Seed" ID="{S}" isCurrentlyViewed="true"><one:Page name="Seed page" ID="{PG}" isCurrentlyViewed="true" /><one:Page name="other" ID="{PG2}" /></one:Section>
              <one:Section name="Other" ID="{S2}" />
            </one:Notebook>
            """);

        Assert.Equal(new ViewSnapshot(1, "test host", "MCP Test", "Seed", "Seed page"), ProbeView.Snapshot(1, "test host", notebooks, tree, null));
        Assert.Equal(new ViewSnapshot(1, "other", "MCP Test", null, null), ProbeView.Snapshot(1, "other", notebooks, null, null)); // not read further
    }

    [Fact]
    public void ViewSnapshot_NothingViewed() =>
        Assert.Equal(new ViewSnapshot(0, "none", null, null, null), ProbeView.Snapshot(0, "none", XElement.Parse($$"""<one:Notebooks xmlns:one="{{Ns}}" />"""), null, null));

    [Theory]
    [InlineData("ONENOTE", "ONENOTE")]
    [InlineData("onenote", "ONENOTE")]
    [InlineData("testhost", "test host")]
    [InlineData("explorer", "other")]
    [InlineData("Code", "other")]
    [InlineData(null, "none")]
    public void ProcessLabel_OnlyOneNoteAndTheTestHostAreNamed(string? process, string expected) =>
        Assert.Equal(expected, ProbeView.ProcessLabel(process, "testhost"));

    [Theory]
    [InlineData("MCP Test", null, true)]
    [InlineData("MCP Test Cloud", null, true)]
    [InlineData("MCP Test Throwaway 1112-034716 b", null, true)]
    [InlineData("MCP Test Throwaway Cloud", null, true)]
    [InlineData("probe sg 1116-120000", @"C:\L\OneNoteMcp-Test\probes\app-state\1116-120000\o3\probe sg 1116-120000", true)] // a run notebook, whatever its name
    [InlineData("Personal Diary", @"D:\Notebooks\Personal Diary", false)]
    [InlineData("MCP Testing", null, false)]
    public void IsLoggable_TestThrowawayAndRunNotebooksOnly(string name, string? path, bool expected) =>
        Assert.Equal(expected, ProbeView.IsLoggable(name, path, @"C:\L\OneNoteMcp-Test\probes\app-state\1116-120000"));

    [Fact]
    public void CompareIds_KeptLostGainedAndClosed()
    {
        var before = new Dictionary<string, HashSet<string>>
        {
            ["MCP Test"] = ["{T}", "{S}"],
            ["MCP Test Throwaway 1112-034716 c"] = ["{C}", "{CS}"],
            ["MCP Test Cloud"] = ["{X}"],
        };
        var after = new Dictionary<string, HashSet<string>>
        {
            ["MCP Test"] = ["{T}", "{S}"],
            ["MCP Test Throwaway 1112-034716 c"] = ["{C}", "{NEW}"],
        };

        Assert.Equal(
            [
                new IdsKept("MCP Test", true, true, 2, 2, 0, 0),
                new IdsKept("MCP Test Cloud", false, false, 1, 0, 1, 0),
                new IdsKept("MCP Test Throwaway 1112-034716 c", true, false, 2, 2, 1, 1),
            ],
            ProbeView.CompareIds(before, after));
    }

    [Fact]
    public void SyntheticId_IsANewNotebookShapedId()
    {
        var a = ProbeSession.SyntheticId();

        Assert.Matches(@"^\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}\{1\}\{B0\}$", a);
        Assert.NotEqual(a, ProbeSession.SyntheticId());
    }

    [Fact]
    public void GrantSettingsJson_IsReadByCoresSettingsStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "onenote-mcp-probe-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, ProbeView.GrantSettingsJson([(@"C:\L\MCP Test Throwaway 1112-034716 b", "MCP Test Throwaway 1112-034716 b"), ("https://d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Throwaway Cloud/", "MCP Test Throwaway Cloud")], new DateTime(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc)));
            var snapshot = new OneNoteMcp.Core.Settings.SettingsStore(file).Read();

            Assert.Equal(OneNoteMcp.Core.Settings.SettingsStatus.Ok, snapshot.Status);
            Assert.Equal(2, snapshot.NotebookEntries.Count(g => g.Write && !g.PermanentDelete));
            Assert.Contains(snapshot.NotebookEntries, g => g.Key == @"C:\L\MCP Test Throwaway 1112-034716 b");
            Assert.Contains(snapshot.NotebookEntries, g => g.Key == "https://d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Throwaway Cloud"); // NotebookKey: no trailing '/'
            Assert.DoesNotContain(snapshot.NotebookEntries, g => g.Key.EndsWith('/') || g.Key.EndsWith('\\'));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Misplaced_NamesOnlyProbeChildren_InLoggableNotebooks()
    {
        var all = XElement.Parse($$"""
            <one:Notebooks xmlns:one="{{Ns}}">
              <one:Notebook name="MCP Test Throwaway 1112-034716 c" ID="{C}" path="C:\L\c">
                <one:SectionGroup name="Misplaced Sections" ID="{M}"><one:Section name="probe pend local 1116-120000" ID="{S1}" /><one:Section name="Seed" ID="{S2}" /></one:SectionGroup>
              </one:Notebook>
              <one:Notebook name="Personal Diary" ID="{P}" path="D:\N\Personal Diary">
                <one:SectionGroup name="Misplaced Sections" ID="{M2}"><one:Section name="probe x" ID="{S3}" /></one:SectionGroup>
              </one:Notebook>
            </one:Notebooks>
            """);

        var json = System.Text.Json.JsonSerializer.Serialize(ProbeView.Misplaced(all, null));

        Assert.Contains("probe pend local 1116-120000", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Personal Diary", json, StringComparison.Ordinal);
        Assert.DoesNotContain("probe x", json, StringComparison.Ordinal); // in another notebook: counted, not named
        Assert.DoesNotContain("Seed", json, StringComparison.Ordinal);
        Assert.Contains(ProbeView.OtherNotebook, json, StringComparison.Ordinal);
    }

    [Fact]
    public void Junctions_MadeAndRemovedWithoutFollowingThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "onenote-mcp-probe-tests", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "probe target");
        var link = Path.Combine(root, "probe junction");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        try
        {
            Junctions.Make(link, target, root);
            Assert.True(Junctions.IsJunction(link));
            Assert.True(File.Exists(Path.Combine(link, "keep.txt"))); // it points at the target

            Assert.Throws<InvalidOperationException>(() => Junctions.Remove(target, root)); // a real folder is refused
            Assert.True(Junctions.Remove(link, root));
            Assert.False(Directory.Exists(link));
            Assert.True(File.Exists(Path.Combine(target, "keep.txt"))); // the target and its files stay
            Assert.False(Junctions.Remove(link, root)); // nothing there any more
        }
        finally
        {
            if (Junctions.IsJunction(link))
            {
                Directory.Delete(link, recursive: false);
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Junctions_BothEndsMustBeInTheRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "onenote-mcp-probe-tests", Guid.NewGuid().ToString("N"));

        Assert.Throws<InvalidOperationException>(() => Junctions.Make(Path.Combine(root, "link"), Path.GetTempPath(), root));
        Assert.Throws<InvalidOperationException>(() => Junctions.Make(Path.Combine(root, "..", "link"), Path.Combine(root, "t"), root));
        Assert.Throws<InvalidOperationException>(() => Junctions.Remove(Path.GetTempPath(), root));
        Assert.False(Directory.Exists(root)); // refused before touching the disk
    }

    [Fact]
    public void ProbeUrl_TheCloudScenariosUrlPassesTheGuard()
    {
        Assert.True(TestNotebookGuard.IsProbeUrl(AppStateCloudProbes.Url("0123456789abcdef", "probe missing 1116-120000"), "0123456789ABCDEF", "1116-120000"));
        Assert.False(TestNotebookGuard.IsProbeUrl(AppStateCloudProbes.Url("0123456789abcdef", "probe missing 1116-120000"), "0123456789ABCDEF", "1116-120001"));
    }

    // --- Spike 4.12: links, landings, windows ----------------------------------------------------------------------

    // Made-up links in the shapes OneNote's links take; no real path, account ID or name.
    private const string SectionGuid = "{11111111-2222-3333-4444-555555555555}";
    private const string PageGuid = "{66666666-7777-8888-9999-AAAAAAAAAAAA}";
    private const string LocalPageLink = "onenote:///C:\\Users\\me\\AppData\\Local\\OneNoteMcp-Test\\MCP%20Test\\Seed.one#Seed%20page&section-id={11111111-2222-3333-4444-555555555555}&page-id={66666666-7777-8888-9999-aaaaaaaaaaaa}&end";
    private const string CloudPageLink = "onenote:https://d.docs.live.net/0123456789abcdef/Documents/MCP%20Test%20Cloud/Probes.one#Text%20Page&section-id=%7B11111111-2222-3333-4444-555555555555%7D&page-id=%7B66666666-7777-8888-9999-AAAAAAAAAAAA%7D&end&base-path=https://d.docs.live.net/0123456789abcdef/Documents/MCP%20Test%20Cloud";
    private const string ParagraphLink = "onenote:///C:\\x\\MCP%20Test\\Probes.one#Probe&section-id={11111111-2222-3333-4444-555555555555}&page-id={66666666-7777-8888-9999-AAAAAAAAAAAA}&object-id={BBBBBBBB-CCCC-DDDD-EEEE-FFFFFFFFFFFF}&10&end";
    private const string WebLink = "https://onedrive.live.com/redir.aspx?cid=0123456789abcdef&page=edit&resid=0123456789ABCDEF!123&wd=target%28Probes.one%7C11111111-2222-3333-4444-555555555555%2FText%20Page%7C66666666-7777-8888-9999-AAAAAAAAAAAA%2F%29";

    [Fact]
    public void LinkShape_LocalPageLink()
    {
        var shape = LinkShape.Parse(LocalPageLink);

        Assert.Equal(("onenote", "local path", ".one"), (shape.Scheme, shape.PathForm, shape.Target));
        Assert.True(shape.Title && shape.SectionId && shape.PageId && shape.End);
        Assert.False(shape.ObjectId || shape.BasePath);
        Assert.Equal(PageGuid, shape.PageGuid); // upper-cased
        Assert.Equal(SectionGuid, shape.SectionGuid);
        Assert.Equal(2, shape.Guids);
    }

    [Fact]
    public void LinkShape_CloudPageLink_EncodedGuidsAndBasePath()
    {
        var shape = LinkShape.Parse(CloudPageLink);

        Assert.Equal(("onenote", "https d.docs.live.net", ".one"), (shape.Scheme, shape.PathForm, shape.Target));
        Assert.True(shape.BasePath && shape.PageId && shape.SectionId);
        Assert.Equal(PageGuid, shape.PageGuid);
    }

    [Fact]
    public void LinkShape_NotebookFolder_ParagraphAndWebLinks()
    {
        var notebook = LinkShape.Parse("onenote:https://d.docs.live.net/0123456789abcdef/Documents/MCP%20Test%20Cloud/");
        Assert.Equal(("folder", false, false, null), (notebook.Target, notebook.PageId, notebook.Title, notebook.PageGuid));

        var paragraph = LinkShape.Parse(ParagraphLink);
        Assert.True(paragraph.ObjectId);
        Assert.Equal(3, paragraph.Guids);

        var web = LinkShape.Parse(WebLink);
        Assert.Equal(("https", "https onedrive.live.com", "other extension"), (web.Scheme, web.PathForm, web.Target));
        Assert.Equal(["cid", "page", "resid", "wd"], web.QueryKeys);
        Assert.Equal(2, web.Guids);
        Assert.Null(web.PageGuid);

        var loose = LinkShape.Parse(NavLinks.LooseFile(@"C:\Users\me\AppData\Local\OneNoteMcp-Test\probes\publish\probe nav 1116-120000.one"));
        Assert.Equal(("onenote", "local path", ".one", false), (loose.Scheme, loose.PathForm, loose.Target, loose.PageId));

        Assert.Equal(("file", "other"), (LinkShape.Parse("file:///C:/x.one").Scheme, LinkShape.Parse("file:///C:/x.one").PathForm));
        Assert.Equal("none", LinkShape.Parse(null).Scheme);
    }

    [Fact]
    public void LinkShape_SerializedRow_HoldsNoPathOrAccountId()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(LinkShape.Parse(CloudPageLink));

        Assert.DoesNotContain("0123456789", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Probes", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Text", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WithPageId_ReplacesOnlyThePageGuid_InTheSameForm()
    {
        var guid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");

        var braces = NavLinks.WithPageId(LocalPageLink, guid);
        Assert.Equal(LocalPageLink.Replace("{66666666-7777-8888-9999-aaaaaaaaaaaa}", "{01234567-89AB-CDEF-0123-456789ABCDEF}", StringComparison.Ordinal), braces);

        var encoded = NavLinks.WithPageId(CloudPageLink, guid);
        Assert.Equal(CloudPageLink.Replace("%7B66666666-7777-8888-9999-AAAAAAAAAAAA%7D", "%7B01234567-89AB-CDEF-0123-456789ABCDEF%7D", StringComparison.Ordinal), encoded);
        Assert.Equal("{01234567-89AB-CDEF-0123-456789ABCDEF}", LinkShape.Parse(encoded).PageGuid);
        Assert.Equal(SectionGuid, LinkShape.Parse(encoded).SectionGuid);
    }

    [Theory]
    [InlineData("onenote:///C:\\x\\MCP%20Test\\Seed.one#Seed&section-id={11111111-2222-3333-4444-555555555555}&end")] // no page-id
    [InlineData("onenote:///C:\\x\\MCP%20Test\\Seed.one#Seed&page-id={1}&page-id={2}&end")] // two
    public void WithPageId_NeedsExactlyOnePageId(string link) =>
        Assert.Throws<InvalidOperationException>(() => NavLinks.WithPageId(link, Guid.NewGuid()));

    private static LandingPoint At(long ms, string page, string? error = null) => new(ms, page, "{S}", "{N}", error);

    [Fact]
    public void Landing_UnchangedFromTheStart_SettlesAt1500()
    {
        var points = new List<LandingPoint>();
        while (LandingSchedule.NextAtMs(points, cloud: false) is { } at)
        {
            points.Add(At(at, "{B}"));
        }

        Assert.Equal([0, 100, 250, 500, 750, 1000, 1500], points.Select(p => p.Ms));
        Assert.True(LandingSchedule.Settled(points));
        Assert.Equal(0, LandingSchedule.SettledAtMs(points));
    }

    [Fact]
    public void Landing_AChangeAt1000_SettlesOneSecondLater()
    {
        var points = new List<LandingPoint>();
        while (LandingSchedule.NextAtMs(points, cloud: false) is { } at)
        {
            points.Add(At(at, at < 1000 ? "{A}" : "{B}"));
        }

        Assert.Equal(2000, points[^1].Ms);
        Assert.Equal(1000, LandingSchedule.SettledAtMs(points));
    }

    [Fact]
    public void Landing_NeverStill_RunsToTheLimit_LongerOnTheCloud()
    {
        foreach (var cloud in new[] { false, true })
        {
            var points = new List<LandingPoint>();
            var i = 0;
            while (LandingSchedule.NextAtMs(points, cloud) is { } at)
            {
                points.Add(At(at, (i++ % 2).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            Assert.Equal(LandingSchedule.LimitMs(cloud), points[^1].Ms);
            Assert.False(LandingSchedule.Settled(points));
            Assert.Null(LandingSchedule.SettledAtMs(points));
        }

        Assert.Equal((5000L, 10000L), (LandingSchedule.LimitMs(false), LandingSchedule.LimitMs(true)));
    }

    [Fact]
    public void Landing_NotBefore1500_EvenIfStillForASecond()
    {
        Assert.False(LandingSchedule.Settled([At(0, "{B}"), At(1000, "{B}"), At(1400, "{B}")]));
        Assert.True(LandingSchedule.Settled([At(0, "{E}", "0x80042014"), At(1500, "{E}", "0x80042014")])); // the same error counts as still
        Assert.False(LandingSchedule.Settled([At(0, "{B}", "0x80042014"), At(1500, "{B}")]));
    }

    [Fact]
    public void Redact_ReplacesOneNoteLinks_InJsonAndText()
    {
        var row = System.Text.Json.JsonSerializer.Serialize(new { link = CloudPageLink, other = "kept" });

        var redacted = ProbeRecorder.Redact(row);
        Assert.Equal("""{"link":"<onenote link>","other":"kept"}""", redacted.Replace("\\u003Conenote link\\u003E", "<onenote link>", StringComparison.Ordinal));
        Assert.Equal("followed <onenote link>", ProbeRecorder.Redact("followed " + LocalPageLink));
    }

    [Fact]
    public void LooseFile_EncodesLikeOneNotesOwnLinks()
    {
        // As in Fixtures/mcp-test/link/page.txt: "onenote:///C:\Users\mcp-test\AppData\Local\OneNoteMcp-Test\MCP%20Test\Fixtures.one#…"
        Assert.Equal(
            @"onenote:///C:\Users\mcp-test\AppData\Local\OneNoteMcp-Test\probes\publish\probe%20nav%201116-120000.one",
            NavLinks.LooseFile(@"C:\Users\mcp-test\AppData\Local\OneNoteMcp-Test\probes\publish\probe nav 1116-120000.one"));
        Assert.DoesNotContain(' ', NavLinks.LooseFile(@"C:\a b\c d.one"));
        Assert.EndsWith(@"\x%23y%26z.one", NavLinks.LooseFile(@"C:\x#y&z.one"), StringComparison.Ordinal); // a fragment or key separator can't leak in
    }

    [Fact]
    public void Redact_KeepsTheGuardsRefusalText()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.CheckNavigateUrl("file:///C:/x.one", new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal(ex.Message, ProbeRecorder.Redact(ex.Message));
        Assert.Contains("neither onenote: nor https:", ProbeRecorder.Redact(ex.Message), StringComparison.Ordinal);
        Assert.Equal("a <onenote link>", ProbeRecorder.Redact("a onenote:https://d.docs.live.net/0123456789abcdef/x.one#p"));
    }

    [Fact]
    public void ProbeWindowsInterop_SlotOrderMatchesTheTypeLibrary()
    {
        static string[] Declared(Type t) =>
            [.. t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .OrderBy(m => m.MetadataToken).Select(m => m.Name)];

        // Windows slots 7-10: Item, Count, _NewEnum, CurrentWindow.
        Assert.Equal(["GetItem", "get_Count", "NewEnumPlaceholder", "get_CurrentWindow"], Declared(typeof(IProbeWindows)));

        // Window slots 7-22: WindowHandle, CurrentPageId, CurrentSectionId, CurrentSectionGroupId, CurrentNotebookId,
        // NavigateTo, FullPageView get/put, Active get/put, DockedLocation get/put, Application, SideNote, NavigateToUrl,
        // SetDockedLocation.
        Assert.Equal(
            [
                "get_WindowHandle", "get_CurrentPageId", "get_CurrentSectionId", "get_CurrentSectionGroupId", "get_CurrentNotebookId",
                "NavigateTo", "GetFullPageView", "SetFullPageViewPlaceholder", "GetActive", "SetActivePlaceholder",
                "GetDockedLocation", "SetDockedLocationPlaceholder", "ApplicationPlaceholder", "GetSideNote",
                "NavigateToUrlPlaceholder", "SetDockedLocationMethodPlaceholder",
            ],
            Declared(typeof(IProbeWindow)));
    }

    [Fact]
    public void ProbeWindowsInterop_IsDualComImport_WithTheTypeLibraryIids_AndUncallablePlaceholders()
    {
        foreach (var (type, iid) in new[] { (typeof(IProbeWindows), "6D4B9C3E-CC05-493F-85E2-43D1006DF96A"), (typeof(IProbeWindow), "8E8304B8-CBD1-44F8-B0E8-89C625B2002E") })
        {
            Assert.True(type.IsImport);
            Assert.False(type.IsPublic); // internal to the test assembly
            Assert.Equal(new Guid(iid), type.GUID);
            Assert.Equal(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsDual, ((System.Runtime.InteropServices.InterfaceTypeAttribute)Attribute.GetCustomAttribute(type, typeof(System.Runtime.InteropServices.InterfaceTypeAttribute))!).Value);
        }

        var placeholders = typeof(IProbeWindow).GetMethods().Concat(typeof(IProbeWindows).GetMethods()).Where(m => m.Name.Contains("Placeholder", StringComparison.Ordinal)).ToList();
        Assert.Equal(7, placeholders.Count);
        Assert.All(placeholders, m => Assert.True(((ObsoleteAttribute?)Attribute.GetCustomAttribute(m, typeof(ObsoleteAttribute)))?.IsError, m.Name));

        // Every callable window member is a getter or NavigateTo: no setter is declared callable.
        var callable = typeof(IProbeWindow).GetMethods().Where(m => Attribute.GetCustomAttribute(m, typeof(ObsoleteAttribute)) is null).Select(m => m.Name).ToList();
        Assert.All(callable, name => Assert.True(name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("Get", StringComparison.Ordinal) || name == "NavigateTo", name));
    }

    [Fact]
    public void ProbeWindowsRelease_SkipsWhatIsNotComAndEmptiesTheList()
    {
        var wrappers = new List<object> { new(), "not com" };

        Assert.Equal(0, ProbeWindowsRelease.All(wrappers));
        Assert.Empty(wrappers);
    }

    [Fact]
    public void Read_KeepsTheHResult_WhenTheRuntimeMapsItToArgumentException()
    {
        // Sitting 2026-11-21: Windows.Item(past the end) fails with E_INVALIDARG, which the runtime raises as
        // ArgumentException, not COMException; it escaped and failed every window read.
        var value = ProbeValue.Read<object>(() => throw new ArgumentException("past the end") { HResult = unchecked((int)0x80070057) });

        Assert.Null(value.Value);
        Assert.StartsWith("0x80070057", value.HResult, StringComparison.Ordinal);
    }

    [Fact]
    public void IsComFailure_CoversTheRuntimesMappedExceptions_NotOurOwnBugs()
    {
        Assert.True(ProbeValue.IsComFailure(new System.Runtime.InteropServices.COMException("x", unchecked((int)0x80042014))));
        Assert.True(ProbeValue.IsComFailure(new ArgumentException("x") { HResult = unchecked((int)0x80070057) }));
        Assert.True(ProbeValue.IsComFailure(new NotImplementedException()));
        Assert.False(ProbeValue.IsComFailure(new InvalidOperationException("ours")));
    }

    private static WindowRead Window(int index, ulong handle, string page) =>
        new(index, null, ProbeValue.Ok(handle), true, true, false, ProbeValue.Ok(page), null, null, null, null);

    [Fact]
    public void Summarize_IndexBaseCurrentWindowAndPastTheEnd()
    {
        var snapshot = new WindowsSnapshot(
            ProbeValue.Ok("Windows"),
            ProbeValue.Ok(2u),
            Window(0, 22, "{B}") with { Index = null },
            [WindowRead.Failed(0, "0x80070057"), Window(1, 11, "{A}"), Window(2, 22, "{B}")],
            5);

        var summary = NavProbe.Summarize(snapshot);

        Assert.Equal(1, summary.IndexBase);
        Assert.Equal(2, summary.CurrentIndex);
        Assert.Equal([new FailedItem(0, "0x80070057")], summary.Failed);
        Assert.Equal(2, summary.Windows);
    }

    [Fact]
    public void Moves_AndSecondWindow_ByHandle()
    {
        var before = new WindowsSnapshot(ProbeValue.Ok("Windows"), ProbeValue.Ok(2u), null, [Window(0, 11, "{A}"), Window(1, 22, "{A}")], 1);
        var after = new WindowsSnapshot(ProbeValue.Ok("Windows"), ProbeValue.Ok(2u), null, [Window(0, 11, "{B}"), Window(1, 22, "{A}")], 1);

        var moves = NavProbe.Moves(before, after);

        Assert.Equal([true, false], moves.Select(m => m.Moved));
        Assert.Equal(22ul, NavProbe.SecondWindow(after, "{B}")!.HandleValue);
        Assert.Null(NavProbe.SecondWindow(after with { Items = [Window(0, 11, "{B}")] }, "{B}"));
    }

    [Fact]
    public void Pick_SeedThenProbes_LeavingOutTheVaultAndTheRecycleBin()
    {
        var tree = XElement.Parse($$"""
            <one:Notebook xmlns:one="{{Ns}}" name="MCP Test" ID="{T}">
              <one:Section name="Ballast Vault" ID="{V}" encrypted="true"><one:Page ID="{VP}" name="v" /></one:Section>
              <one:Section name="Seed" ID="{S}"><one:Page ID="{SP}" name="" /><one:Page ID="{SP2}" name="second" /></one:Section>
              <one:Section name="Ballast" ID="{BA}"><one:Page ID="{BP}" name="b" /></one:Section>
              <one:Section name="Probes" ID="{P}"><one:Page ID="{PP}" name="p" isInRecycleBin="true" /><one:Page ID="{PP2}" name="p2" /></one:Section>
              <one:SectionGroup name="OneNote_RecycleBin" ID="{RB}" isRecycleBin="true"><one:Section name="Seed" ID="{OLD}"><one:Page ID="{OP}" name="o" /></one:Section></one:SectionGroup>
            </one:Notebook>
            """);

        var t = NavProbe.Pick(tree, "Seed", "Probes")!;

        Assert.Equal(("{T}", "{SP}", "{PP2}", "{SP2}"), (t.NotebookId, t.A.PageId, t.B.PageId, t.C.PageId));
        Assert.Equal("MCP Test > Seed > (untitled page)", t.Path(t.A));
        Assert.Equal("{BP}", NavProbe.Pick(tree, "Seed", null)!.B.PageId); // the first other section
        Assert.Null(NavProbe.Pick(tree, "Missing", null));
    }
}
