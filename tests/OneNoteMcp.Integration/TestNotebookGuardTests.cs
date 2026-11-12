using Xunit;

namespace OneNoteMcp.Integration;

/// <summary>The guarded write path's pure logic. These run without OneNote, whenever the integration project runs.</summary>
public sealed class TestNotebookGuardTests
{
    private const string Ns = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    private const string Notebooks = $$"""
        <one:Notebooks xmlns:one="{{Ns}}">
          <one:Notebook name="Personal" ID="{P}" path="C:\Users\me\Documents\OneNote Notebooks\Personal" />
          <one:Notebook name="MCP Test" ID="{T}" path="C:\Users\me\AppData\Local\OneNoteMcp-Test\MCP Test\" />
          <one:Notebook name="Work" ID="{W}" path="D:\Notebooks\Work" />
        </one:Notebooks>
        """;

    private const string Tree = $$"""
        <one:Notebook xmlns:one="{{Ns}}" name="MCP Test" ID="{T}">
          <one:Section name="Seed" ID="{S}">
            <one:Page ID="{PG}" name="" />
          </one:Section>
          <one:Section name="Seed" ID="{OLD}" isInRecycleBin="true" />
          <one:SectionGroup name="g" ID="{G}">
            <one:Section name="Nested" ID="{NS}" />
          </one:SectionGroup>
        </one:Notebook>
        """;

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\OneNoteMcp-Test\MCP Test")]
    [InlineData(@"c:\users\me\appdata\local\onenotemcp-test\mcp test\")]
    public void FindNotebookId_MatchesByPath_IgnoringCaseAndTrailingSeparator(string folder) =>
        Assert.Equal("{T}", TestNotebookGuard.FindNotebookId(Notebooks, folder));

    [Fact]
    public void FindNotebookId_NoMatch_IsNull() =>
        Assert.Null(TestNotebookGuard.FindNotebookId(Notebooks, @"C:\Users\me\AppData\Local\OneNoteMcp-Test\Other"));

    [Fact]
    public void FindNotebookId_PathOfANotebookNotNamedMcpTest_IsRefused() =>
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.FindNotebookId(Notebooks, @"D:\Notebooks\Work"));

    [Theory]
    [InlineData("{T}")]
    [InlineData("{S}")]
    [InlineData("{PG}")]
    [InlineData("{NS}")]
    public void Contains_TheNotebookAndEverythingInside(string id) =>
        Assert.True(TestNotebookGuard.Contains(Tree, id));

    [Theory]
    [InlineData("{P}")]
    [InlineData("")]
    [InlineData("{t}")] // IDs compare exactly
    public void Contains_RefusesForeignOrEmptyTargets(string id) =>
        Assert.False(TestNotebookGuard.Contains(Tree, id));

    [Fact]
    public void Contains_RefusesATreeThatIsNotANotebook() =>
        Assert.False(TestNotebookGuard.Contains($$"""<one:Section xmlns:one="{{Ns}}" ID="{S}" />""", "{S}"));

    [Fact]
    public void FindSectionId_SkipsTheRecycleBin() =>
        Assert.Equal("{S}", TestNotebookGuard.FindSectionId(Tree, "Seed"));

    [Fact]
    public void FirstPageId_ReadsTheSection() =>
        Assert.Equal("{PG}", TestNotebookGuard.FirstPageId(Tree, "{S}"));

    [Fact]
    public void EnsureFolderIsOurs_RefusesAFolderWithForeignFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "onenote-mcp-guard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            TestNotebookGuard.EnsureFolderIsOurs(Path.Combine(folder, "absent")); // nonexistent: fine
            TestNotebookGuard.EnsureFolderIsOurs(folder); // empty: fine
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "x");
            Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.EnsureFolderIsOurs(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void EnsureFolderIsOurs_RefusesAnExistingNotebookFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "onenote-mcp-guard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "Open Notebook.onetoc2"), "x");
            Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.EnsureFolderIsOurs(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void EnsureFolderIsOurs_RefusesARelativePath() =>
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.EnsureFolderIsOurs(@"relative\MCP Test"));

    // --- MCP Test Cloud (story 2.2) -------------------------------------------------------------------------------

    // A made-up account ID; the real one never lands in the repo.
    private const string CloudPath = "https://d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Cloud/";

    private static string CloudNotebooks(params (string Name, string Id, string Path)[] extra)
    {
        var rows = string.Concat(extra.Select(e => $"""<one:Notebook name="{e.Name}" ID="{e.Id}" path="{e.Path}" />"""));
        return $$"""
            <one:Notebooks xmlns:one="{{Ns}}">
              <one:Notebook name="MCP Test" ID="{T}" path="C:\Users\me\AppData\Local\OneNoteMcp-Test\MCP Test" />
              <one:Notebook name="Personal" ID="{P}" path="https://d.docs.live.net/0123456789ABCDEF/Documents/Personal/" />
              {{rows}}
            </one:Notebooks>
            """;
    }

    [Theory]
    [InlineData(CloudPath)]
    [InlineData("https://D.DOCS.LIVE.NET/0123456789abcdef/documents/mcp test cloud/")]
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Cloud")]
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Documents/MCP%20Test%20Cloud/")]
    public void FindCloudNotebookId_MatchesExactNameAndCloudPath_IgnoringPathCase(string path) =>
        Assert.Equal("{C}", TestNotebookGuard.FindCloudNotebookId(CloudNotebooks(("MCP Test Cloud", "{C}", path))));

    [Fact]
    public void FindCloudNotebookId_NotOpen_IsNull_AndNothingIsCreated() =>
        Assert.Null(TestNotebookGuard.FindCloudNotebookId(CloudNotebooks()));

    [Theory]
    [InlineData("MCP Test Cloud 2", "https://d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Cloud 2/")] // look-alike name and folder
    [InlineData("MCP Test Cloud", "https://d.docs.live.net.example.com/0123456789ABCDEF/Documents/MCP Test Cloud/")] // look-alike host
    [InlineData("MCP Test Cloud", "https://example.com/d.docs.live.net/Documents/MCP Test Cloud/")] // host only in the path
    [InlineData("MCP Test Cloud", "http://d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Cloud/")] // not https
    [InlineData("MCP Test Cloud", "https://d.docs.live.net:8443/0123456789ABCDEF/Documents/MCP Test Cloud/")] // other port
    [InlineData("MCP Test Cloud", "https://user@d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Cloud/")] // user info
    [InlineData("MCP Test Cloud", "https://d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Cloud/Sub/")] // a folder inside it
    [InlineData("MCP Test Cloud", "https://d.docs.live.net/MCP Test Cloud/")] // no account segment
    [InlineData("MCP Test Cloud", @"C:\Notebooks\MCP Test Cloud")] // a local notebook with the name
    [InlineData("MCP Test Cloud", "https://contoso.sharepoint.com/personal/me/Documents/MCP Test Cloud/")] // another cloud host
    public void FindCloudNotebookId_RefusesLookAlikes(string name, string path) =>
        Assert.Null(TestNotebookGuard.FindCloudNotebookId(CloudNotebooks((name, "{X}", path))));

    [Theory]
    [InlineData("mcp test cloud")] // the name is exact
    [InlineData("Work")]
    public void FindCloudNotebookId_CloudPathOfAnotherName_IsRefused(string name) =>
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.FindCloudNotebookId(CloudNotebooks((name, "{X}", CloudPath))));

    [Fact]
    public void FindCloudNotebookId_TwoNotebooksAtTheCloudPath_IsRefused() =>
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.FindCloudNotebookId(
            CloudNotebooks(("MCP Test Cloud", "{C}", CloudPath), ("MCP Test Cloud", "{D}", CloudPath.ToLowerInvariant()))));

    [Fact]
    public void TestNotebookIds_BothNotebooks_LocalFirst() =>
        Assert.Equal(
            ["{T}", "{C}"],
            TestNotebookGuard.TestNotebookIds(CloudNotebooks(("MCP Test Cloud", "{C}", CloudPath)), @"C:\Users\me\AppData\Local\OneNoteMcp-Test\MCP Test"));

    [Fact]
    public void TestNotebookIds_NeitherOpen_IsEmpty() =>
        Assert.Empty(TestNotebookGuard.TestNotebookIds(CloudNotebooks(), @"C:\Elsewhere\MCP Test"));

    [Fact]
    public void Contains_CloudTree_RefusesForeignTargets()
    {
        var cloudTree = $$"""
            <one:Notebook xmlns:one="{{Ns}}" name="MCP Test Cloud" ID="{C}" path="{{CloudPath}}">
              <one:Section name="Probes" ID="{CS}"><one:Page ID="{CP}" name="Text Page" /></one:Section>
            </one:Notebook>
            """;

        Assert.True(TestNotebookGuard.Contains(cloudTree, "{CP}"));
        Assert.False(TestNotebookGuard.Contains(cloudTree, "{P}"));
    }

    // --- Story 5.1: deletes, hierarchy XML, throwaway notebooks, publish paths ------------------------------------

    private const string Root = @"C:\Users\me\AppData\Local\OneNoteMcp-Test";
    private static readonly string[] TestIds = ["{T}", "{C}"];
    private static readonly HashSet<string> Made = ["{S1}", "{S2}", "{G}"];

    [Fact]
    public void Contains_AcceptsTheNotebookRoot_SoDeletesNeedCheckDelete() =>
        Assert.True(TestNotebookGuard.Contains(Tree, "{T}"));

    [Theory]
    [InlineData("{T}")] // MCP Test itself, even if a bug put it in the created set
    [InlineData("{C}")] // MCP Test Cloud itself
    [InlineData("{S}")] // a fixture: inside MCP Test, not made by this run
    [InlineData("")]
    public void CheckDelete_RefusesTestNotebooksFixturesAndEmpty(string id) =>
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.CheckDelete("DeleteHierarchy", id, TestIds, new HashSet<string>(Made) { "{T}", "{C}" }));

    [Fact]
    public void CheckDelete_AllowsAnObjectThisRunMade() =>
        TestNotebookGuard.CheckDelete("DeleteHierarchy", "{S1}", TestIds, Made);

    [Fact]
    public void CheckHierarchyXml_AcceptsOnlyCreatedIds_RootFirst() =>
        Assert.Equal(
            ["{G}", "{S2}", "{S1}"],
            TestNotebookGuard.CheckHierarchyXml("UpdateHierarchy", $$"""<one:SectionGroup xmlns:one="{{Ns}}" ID="{G}"><one:Section ID="{S2}" /><one:Section ID="{S1}" /><one:Section name="new" /></one:SectionGroup>""", TestIds, Made));

    [Theory]
    [InlineData("""<one:Notebook xmlns:one="NS" ID="{T}"><one:Section name="x" /></one:Notebook>""")] // MCP Test's root
    [InlineData("""<one:Notebook xmlns:one="NS" ID="{C}" name="renamed" />""")] // MCP Test Cloud renamed
    [InlineData("""<one:SectionGroup xmlns:one="NS" ID="{G}"><one:Section ID="{S}" /></one:SectionGroup>""")] // a fixture inside
    [InlineData("""<one:Section xmlns:one="NS" name="no id" />""")] // no ID at all
    [InlineData("""<one:SectionGroup xmlns:one="NS" name="x"><one:Section ID="{S1}" /></one:SectionGroup>""")] // root without an ID
    [InlineData("""<one:Section xmlns:one="NS" id='{S}' />""")] // other case and quotes still count
    [InlineData("""<one:Section xmlns:one="NS" ID="{S1}"><one:Section ID = "{S}" """)] // malformed, naming a fixture
    public void CheckHierarchyXml_Refuses(string xml) =>
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.CheckHierarchyXml("UpdateHierarchy", xml.Replace("NS", Ns, StringComparison.Ordinal), TestIds, Made));

    [Fact]
    public void CheckHierarchyXml_MalformedXml_IsCheckedByPattern() =>
        Assert.Equal(["{S1}"], TestNotebookGuard.CheckHierarchyXml("UpdateHierarchy", $$"""<one:Section xmlns:one="{{Ns}}" ID="{S1}" name="x">""", TestIds, Made));

    [Theory]
    [InlineData("MCP Test Throwaway 1110-1200 a", Root + @"\MCP Test Throwaway 1110-1200 a")]
    [InlineData("MCP Test Throwaway 1110-1200 a renamed", Root + @"\mcp test throwaway 1110-1200 a\")]
    public void IsLocalThrowaway_PrefixedNameInTheThrowawayRoot(string name, string path) =>
        Assert.True(TestNotebookGuard.IsLocalThrowaway(name, path, Root));

    [Theory]
    [InlineData("MCP Test", Root + @"\MCP Test")] // the fixture notebook
    [InlineData("MCP Test Throwaway x", @"D:\Notebooks\MCP Test Throwaway x")] // another folder
    [InlineData("MCP Test Throwaway x", Root + @"\Sub\MCP Test Throwaway x")] // nested deeper
    [InlineData("MCP Test Throwaway x", Root + @"\Personal")] // folder without the prefix
    [InlineData("Personal", Root + @"\MCP Test Throwaway x")] // name without the prefix
    [InlineData("MCP Test Throwaway x", Root + @"\MCP Test\..\MCP Test Throwaway x")] // relative parts
    [InlineData("MCP Test Throwaway x", "https://d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Throwaway x/")] // cloud
    [InlineData("MCP Test Throwaway Cloud", Root + @"\MCP Test Throwaway Cloud")] // the cloud name, locally
    public void IsLocalThrowaway_RefusesLookAlikes(string name, string path) =>
        Assert.False(TestNotebookGuard.IsLocalThrowaway(name, path, Root));

    [Fact]
    public void IsCloudThrowaway_ExactNameAndOneDrivePath()
    {
        Assert.True(TestNotebookGuard.IsCloudThrowaway("MCP Test Throwaway Cloud", "https://d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Throwaway Cloud/"));
        Assert.False(TestNotebookGuard.IsCloudThrowaway("MCP Test Cloud", CloudPath));
        Assert.False(TestNotebookGuard.IsCloudThrowaway("MCP Test Throwaway Cloud", CloudPath));
        Assert.False(TestNotebookGuard.IsCloudThrowaway("MCP Test Throwaway Cloud", "https://example.com/0123456789ABCDEF/Documents/MCP Test Throwaway Cloud/"));
    }

    private static Dictionary<string, IReadOnlySet<string>> Names(params (string Id, string[] Names)[] entries) =>
        entries.ToDictionary(e => e.Id, e => (IReadOnlySet<string>)new HashSet<string>(e.Names));

    [Fact]
    public void ThrowawayNotebookIds_OnlyOpenCandidates_AndRefusesACandidateThatIsNoThrowaway()
    {
        var notebooks = CloudNotebooks(
            ("MCP Test Throwaway 1110 a", "{A}", Root + @"\MCP Test Throwaway 1110 a"),
            ("MCP Test Throwaway Cloud", "{X}", "https://d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Throwaway Cloud/"),
            ("MCP Test Throwaway 1110 b", "{B}", Root + @"\MCP Test Throwaway 1110 b"));

        Assert.Equal(
            ["{A}", "{X}"],
            TestNotebookGuard.ThrowawayNotebookIds(notebooks, Root, Names(("{A}", ["MCP Test Throwaway 1110 a"]), ("{X}", ["MCP Test Throwaway Cloud"]), ("{GONE}", ["MCP Test Throwaway 1110 g"]))));
        Assert.Empty(TestNotebookGuard.ThrowawayNotebookIds(notebooks, Root, Names()));
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.ThrowawayNotebookIds(notebooks, Root, Names(("{T}", ["MCP Test"]))));
    }

    [Fact]
    public void ThrowawayNotebookIds_AcceptsOnlyTheExactNamesThisRunGave()
    {
        var notebooks = CloudNotebooks(
            ("MCP Test Throwaway 1110 a renamed", "{A}", Root + @"\MCP Test Throwaway 1110 a"),
            ("MCP Test Throwaway 1110 b", "{B}", Root + @"\MCP Test Throwaway 1110 b"));

        // A rename the run sent (and allowed) is accepted; the folder still carries the created name.
        Assert.Equal(["{A}"], TestNotebookGuard.ThrowawayNotebookIds(notebooks, Root, Names(("{A}", ["MCP Test Throwaway 1110 a", "MCP Test Throwaway 1110 a renamed"]))));

        // A prefix match alone isn't enough: a name, or a folder, the run never gave it is refused.
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.ThrowawayNotebookIds(notebooks, Root, Names(("{A}", ["MCP Test Throwaway 1110 a"]))));
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.ThrowawayNotebookIds(notebooks, Root, Names(("{B}", ["MCP Test Throwaway 1110 a"]))));
    }

    [Fact]
    public void ThrowawayRoot_IsFixedUnderLocalAppData_IgnoringTheFolderOverride() =>
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneNoteMcp-Test"),
            TestNotebook.ThrowawayRoot);

    [Theory]
    [InlineData("MCP Test Throwaway 1110 a")]
    public void EnsureThrowawayLeaf_AcceptsAPrefixedName(string leaf) => TestNotebookGuard.EnsureThrowawayLeaf(leaf);

    [Theory]
    [InlineData("MCP Test")]
    [InlineData("MCP Test Throwaway Cloud")]
    [InlineData(@"MCP Test Throwaway x\..\MCP Test")]
    [InlineData("MCP Test Throwaway x/y")]
    [InlineData("MCP Test Throwaway ..")]
    [InlineData("")]
    public void EnsureThrowawayLeaf_Refuses(string leaf) =>
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.EnsureThrowawayLeaf(leaf));

    [Fact]
    public void EnsureUnder_AllowsOnlyPathsInsideTheRoot()
    {
        var root = Root + @"\probes\publish";
        TestNotebookGuard.EnsureUnder("Publish", root + @"\x.one", root);
        TestNotebookGuard.EnsureUnder("Publish", root + @"\sub\x.one", root);
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.EnsureUnder("Publish", root, root)); // the root itself
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.EnsureUnder("Publish", root + @"2\x.one", root)); // a sibling sharing the prefix
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.EnsureUnder("Publish", root + @"\..\..\MCP Test\Seed.one", root)); // climbs out
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.EnsureUnder("Publish", @"publish\x.one", root)); // relative
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.EnsureUnder("Publish", @"D:\Elsewhere\x.one", root));
    }

    [Fact]
    public void EnsureOnlyProbeMarker_AcceptsTheMarkerAlone()
    {
        var folder = Path.Combine(Path.GetTempPath(), "onenote-mcp-guard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.EnsureOnlyProbeMarker(folder)); // empty
            File.WriteAllText(Path.Combine(folder, TestNotebook.ProbeMarkerFile), "x");
            TestNotebookGuard.EnsureOnlyProbeMarker(folder);
            File.WriteAllText(Path.Combine(folder, "Open Notebook.onetoc2"), "x");
            Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.EnsureOnlyProbeMarker(folder));
            Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.EnsureOnlyProbeMarker(Path.Combine(folder, "absent")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AnyContains_FindsAnIdAnywhere() =>
        Assert.True(TestNotebookGuard.AnyContains(Tree, "{NS}") && !TestNotebookGuard.AnyContains(Tree, "{GONE}") && !TestNotebookGuard.AnyContains(Tree, string.Empty));

    [Fact]
    public void ResolveFolder_DefaultsUnderLocalAppData_OutsideOneNoteMcp()
    {
        var folder = TestNotebook.ResolveFolder(_ => null);

        Assert.EndsWith(@"\OneNoteMcp-Test\MCP Test", folder, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\OneNoteMcp\", folder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveFolder_HonoursTheOverride() =>
        Assert.Equal(@"D:\Scratch\MCP Test", TestNotebook.ResolveFolder(v => v == TestNotebook.FolderOverrideVariable ? @"D:\Scratch\MCP Test\" : null));
}
