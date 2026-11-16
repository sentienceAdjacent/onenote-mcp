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
}
