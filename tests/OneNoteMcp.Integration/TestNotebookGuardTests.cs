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

    // --- Spike 3.1: merges, MergeFiles, adoption by exact name, recycle bins, the tenant notebook -------------------

    private static readonly HashSet<string> Opened = ["{OPEN}"];

    [Fact]
    public void CheckMerge_CreatedSourceAndDestination_IsNotAnOpenedSource() =>
        Assert.False(TestNotebookGuard.CheckMerge("MergeSections", "{S1}", "{S2}", TestIds, Made, Opened));

    [Fact]
    public void CheckMerge_OpenedBackupAsSource_NeedsItsPathChecked() =>
        Assert.True(TestNotebookGuard.CheckMerge("MergeSections", "{OPEN}", "{S2}", TestIds, Made, Opened));

    [Theory]
    [InlineData("{S1}", "{S}")] // destination is a fixture
    [InlineData("{S}", "{S2}")] // source is a fixture, neither made nor opened
    [InlineData("{S1}", "{OPEN}")] // an opened backup is never a destination
    [InlineData("{T}", "{S2}")] // MCP Test itself as the source
    [InlineData("{S1}", "{C}")] // MCP Test Cloud itself as the destination
    [InlineData("{S1}", "{S1}")] // the same section twice
    [InlineData("", "{S2}")]
    [InlineData("{S1}", "")]
    public void CheckMerge_Refuses(string source, string destination) =>
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.CheckMerge("MergeSections", source, destination, TestIds, new HashSet<string>(Made) { "{T}", "{C}" }, Opened));

    [Fact]
    public void EnsureOpenedFrom_AcceptsOnlyAPathUnderThePublishFolder()
    {
        var root = Root + @"\probes\publish";
        TestNotebookGuard.EnsureOpenedFrom("MergeSections", $$"""<one:Section xmlns:one="{{Ns}}" ID="{OPEN}" path="{{root}}\s3\probe s3.one" />""", root);
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.EnsureOpenedFrom("MergeSections", $$"""<one:Section xmlns:one="{{Ns}}" ID="{OPEN}" path="{{Root}}\MCP Test\Seed.one" />""", root));
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.EnsureOpenedFrom("MergeSections", $$"""<one:Section xmlns:one="{{Ns}}" ID="{OPEN}" path="{{CloudPath}}Probes.one" />""", root));
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.EnsureOpenedFrom("MergeSections", $$"""<one:Section xmlns:one="{{Ns}}" ID="{OPEN}" />""", root));
    }

    [Fact]
    public void CheckMergeFiles_AllFourUnderThePublishFolder()
    {
        var root = Root + @"\probes\publish";
        string[] ok = [root + @"\a\x.one", root + @"\b\x.one", root + @"\c\x.one", root + @"\t\x.one"];
        TestNotebookGuard.CheckMergeFiles("MergeFiles", ok, root);
        for (var i = 0; i < 4; i++)
        {
            var bad = ok.ToArray();
            bad[i] = Root + @"\MCP Test\Seed.one"; // each of base, client, server and target is checked
            Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.CheckMergeFiles("MergeFiles", bad, root));
        }

        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.CheckMergeFiles("MergeFiles", [.. ok, root + @"\x.one"], root)); // not four
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.CheckMergeFiles("MergeFiles", [ok[0], ok[1], ok[2], @"publish\t\x.one"], root)); // relative
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.CheckMergeFiles("MergeFiles", [ok[0], ok[1], ok[2], root + @"\..\..\MCP Test\x.one"], root)); // climbs out
    }

    private const string Adoptee = "MCP Test Throwaway 1112-034716 a";

    [Theory]
    [InlineData(Root + @"\MCP Test Throwaway 1112-034716 a")]
    [InlineData(Root + @"\mcp test throwaway 1112-034716 a\")] // Windows folder names ignore case
    public void FindThrowawayByExactName_NameAndFolderMatch(string path) =>
        Assert.Equal("{A}", TestNotebookGuard.FindThrowawayByExactName(CloudNotebooks((Adoptee, "{A}", path)), Root, Adoptee));

    [Fact]
    public void FindThrowawayByExactName_NotOpen_IsNull()
    {
        Assert.Null(TestNotebookGuard.FindThrowawayByExactName(CloudNotebooks(), Root, Adoptee));
        Assert.Null(TestNotebookGuard.FindThrowawayByExactName(CloudNotebooks(("mcp test throwaway 1112-034716 a", "{A}", Root + @"\" + Adoptee)), Root, Adoptee)); // the name is exact
    }

    [Theory]
    [InlineData(Root + @"\MCP Test Throwaway 1112-034716 b")] // another throwaway's folder
    [InlineData(@"D:\Notebooks\MCP Test Throwaway 1112-034716 a")] // outside the throwaway root
    [InlineData(Root + @"\Sub\MCP Test Throwaway 1112-034716 a")] // nested deeper
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Throwaway 1112-034716 a/")] // a cloud notebook with the name
    public void FindThrowawayByExactName_RefusesTheNameElsewhere(string path) =>
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.FindThrowawayByExactName(CloudNotebooks((Adoptee, "{A}", path)), Root, Adoptee));

    [Fact]
    public void FindThrowawayByExactName_RefusesTwoNotebooksWithTheName() =>
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.FindThrowawayByExactName(
            CloudNotebooks((Adoptee, "{A}", Root + @"\" + Adoptee), (Adoptee, "{B}", Root + @"\" + Adoptee)), Root, Adoptee));

    [Theory]
    [InlineData("MCP Test")]
    [InlineData("MCP Test Cloud")]
    [InlineData("MCP Test Throwaway Cloud")]
    [InlineData("Personal")]
    public void FindThrowawayByExactName_RefusesANameThatIsNoThrowaway(string name) =>
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.FindThrowawayByExactName(CloudNotebooks((name, "{X}", Root + @"\" + name)), Root, name));

    private const string BinTree = $$"""
        <one:Notebook xmlns:one="{{Ns}}" name="MCP Test" ID="{T}">
          <one:Section name="Seed" ID="{S}" />
          <one:SectionGroup name="probe run x" ID="{G}">
            <one:SectionGroup name="OneNote_RecycleBin" ID="{FAKEBIN}" isRecycleBin="true" />
          </one:SectionGroup>
          <one:SectionGroup name="OneNote_RecycleBin" ID="{BIN}" isRecycleBin="true">
            <one:Section name="Deleted Pages" ID="{DP}" isInRecycleBin="true" isDeletedPages="true" />
            <one:Section name="probe del b" ID="{RS}" isInRecycleBin="true" />
          </one:SectionGroup>
        </one:Notebook>
        """;

    [Fact]
    public void RecycleBinIds_TheNotebooksOwnBinAndDeletedPagesOnly() =>
        Assert.Equal(["{BIN}", "{DP}"], TestNotebookGuard.RecycleBinIds(BinTree).Order(StringComparer.Ordinal));

    [Fact]
    public void RecycleBinIds_NotANotebook_IsEmpty() =>
        Assert.Empty(TestNotebookGuard.RecycleBinIds($$"""<one:SectionGroup xmlns:one="{{Ns}}" ID="{BIN}" isRecycleBin="true" />"""));

    [Fact]
    public void CheckHierarchyXml_RecycleBinIdsPassOnlyWhenAllowed()
    {
        var bins = TestNotebookGuard.RecycleBinIds(BinTree);
        var xml = $$"""<one:SectionGroup xmlns:one="{{Ns}}" ID="{BIN}"><one:Section ID="{S1}" /></one:SectionGroup>""";

        Assert.Equal(["{BIN}", "{S1}"], TestNotebookGuard.CheckHierarchyXml("UpdateHierarchy", xml, TestIds, Made, bins));
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.CheckHierarchyXml("UpdateHierarchy", xml, TestIds, Made)); // not allowed by default
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.CheckHierarchyXml("UpdateHierarchy", xml.Replace("{S1}", "{S}", StringComparison.Ordinal), TestIds, Made, bins)); // a fixture still refused
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.CheckHierarchyXml("UpdateHierarchy", $$"""<one:Section xmlns:one="{{Ns}}" ID="{RS}" name="x" />""", TestIds, Made, bins)); // a recycled section isn't the bin
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.CheckHierarchyXml("UpdateHierarchy", $$"""<one:Notebook xmlns:one="{{Ns}}" ID="{T}" />""", TestIds, Made, new HashSet<string>(bins) { "{T}" })); // never a test notebook
    }

    private const string TenantPath = "https://contoso-my.sharepoint.com/sites/probes/Documents/Notebooks/MCP Test Tenant/";

    [Theory]
    [InlineData(TenantPath)]
    [InlineData("https://contoso.sharepoint.com/sites/Team/Shared Documents/MCP Test Tenant")]
    public void IsTenantTest_ExactNameAndAnHttpsPathOffOneDrivePersonal(string path) =>
        Assert.True(TestNotebookGuard.IsTenantTest("MCP Test Tenant", path));

    [Theory]
    [InlineData("MCP Test Tenant 2", TenantPath)] // look-alike name
    [InlineData("mcp test tenant", TenantPath)] // the name is exact
    [InlineData("Work Notes", TenantPath)] // another tenant notebook
    [InlineData("MCP Test Tenant", "https://d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Tenant/")] // OneDrive personal
    [InlineData("MCP Test Tenant", "https://D.DOCS.LIVE.NET/0123456789ABCDEF/Documents/MCP Test Tenant/")] // the same, other case
    [InlineData("MCP Test Tenant", "http://contoso.sharepoint.com/MCP Test Tenant/")] // not https
    [InlineData("MCP Test Tenant", "https://user@contoso.sharepoint.com/MCP Test Tenant/")] // user info
    [InlineData("MCP Test Tenant", @"C:\Notebooks\MCP Test Tenant")] // a local notebook with the name
    [InlineData("MCP Test Tenant", "MCP Test Tenant")] // relative
    [InlineData("MCP Test Tenant", null)]
    [InlineData(null, TenantPath)]
    public void IsTenantTest_RefusesLookAlikes(string? name, string? path) =>
        Assert.False(TestNotebookGuard.IsTenantTest(name, path));

    [Fact]
    public void ThrowawayNotebookIds_AcceptsTheAdoptedTenantNotebookOnly()
    {
        var notebooks = CloudNotebooks(("MCP Test Tenant", "{TT}", TenantPath), ("Work Notes", "{WN}", "https://contoso-my.sharepoint.com/sites/probes/Documents/Notebooks/Work Notes/"));

        Assert.Equal(["{TT}"], TestNotebookGuard.ThrowawayNotebookIds(notebooks, Root, Names(("{TT}", ["MCP Test Tenant"]))));
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.ThrowawayNotebookIds(notebooks, Root, Names(("{WN}", ["MCP Test Tenant"])))); // another tenant notebook
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.ThrowawayNotebookIds(notebooks, Root, Names(("{TT}", ["MCP Test Throwaway x"])))); // a name the run never gave it
    }

    // --- Spike 4.1: CloseNotebook, OpenHierarchy cftNone, SyncHierarchy ---------------------------------------------

    private const string RunRoot = Root + @"\probes\app-state\1116-120000";
    private const string TestFolder = Root + @"\MCP Test";
    private const string NameA = "MCP Test Throwaway 1112-034716 a";
    private const string NameB = "MCP Test Throwaway 1112-034716 b";
    private const string NameC = "MCP Test Throwaway 1112-034716 c";
    private const string CloudThrowawayPath = "https://d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Throwaway Cloud/";

    /// <summary>MCP Test, Personal (OneDrive), MCP Test Cloud, throwaways a, b and c, the cloud throwaway, and two run notebooks.</summary>
    private static string AppStateNotebooks(params (string Name, string Id, string Path)[] extra) => CloudNotebooks(
        [
            ("MCP Test Cloud", "{C}", CloudPath),
            (NameA, "{A}", Root + @"\" + NameA),
            (NameB, "{B}", Root + @"\" + NameB),
            (NameC, "{CC}", Root + @"\" + NameC),
            ("MCP Test Throwaway Cloud", "{X}", CloudThrowawayPath),
            ("MCP Test Throwaway 1116-120000 o1", "{O1}", RunRoot + @"\MCP Test Throwaway 1116-120000 o1"),
            ("probe sg 1116-120000", "{SG}", RunRoot + @"\MCP Test Throwaway 1116-120000 o3\probe sg 1116-120000"),
            .. extra,
        ]);

    private static void Close(string notebooksXml, string id, string? runRoot = RunRoot) =>
        TestNotebookGuard.CheckClose("CloseNotebook", notebooksXml, id, TestFolder, Root, runRoot);

    [Theory]
    [InlineData("{B}")] // throwaway b, by exact name in its own folder
    [InlineData("{CC}")] // throwaway c
    [InlineData("{X}")] // the cloud throwaway
    [InlineData("{O1}")] // a run notebook
    [InlineData("{SG}")] // a section-group folder opened as a notebook: OneNote picked its name; its path is in the run folder
    public void CheckClose_AllowsTheAllowedSet(string id) => Close(AppStateNotebooks(), id);

    [Theory]
    [InlineData(RunRoot + @"\probe junction 1116-120000")] // through the junction
    [InlineData(@"\\localhost\C$\Users\me\AppData\Local\OneNoteMcp-Test\probes\app-state\1116-120000\MCP Test Throwaway 1116-120000 o1")] // the UNC form
    [InlineData(@"\\LOCALHOST\c$\users\me\appdata\local\onenotemcp-test\probes\app-state\1116-120000\x\")] // ignoring case
    public void CheckClose_RunFolderPathsInAnyForm(string path) =>
        Close(AppStateNotebooks(("whatever OneNote named it", "{J}", path)), "{J}");

    [Theory]
    [InlineData("{T}")] // MCP Test
    [InlineData("{C}")] // MCP Test Cloud
    [InlineData("{A}")] // throwaway a: never closed
    [InlineData("{P}")] // any other notebook
    [InlineData("{NOPE}")] // not open
    [InlineData("")]
    public void CheckClose_RefusesOutsideTheAllowedSet(string id) =>
        Assert.Throws<InvalidOperationException>(() => Close(AppStateNotebooks(), id));

    [Theory]
    [InlineData(NameB, @"D:\Notebooks\MCP Test Throwaway 1112-034716 b")] // b's name, the wrong folder
    [InlineData(NameB, Root + @"\MCP Test Throwaway 1112-034716 c")] // b's name in c's folder
    [InlineData(NameB, Root + @"\Sub\MCP Test Throwaway 1112-034716 b")] // nested deeper
    [InlineData("MCP Test Throwaway 1112-034716 b2", Root + @"\MCP Test Throwaway 1112-034716 b2")] // a name prefix
    [InlineData("MCP Test Throwaway 1116-120000 o1", RunRoot + @"\..\..\..\MCP Test")] // a .. path out of the run folder
    [InlineData("MCP Test Throwaway 1116-120000 o1", RunRoot + @"\x\..\o1")] // any .. segment
    [InlineData("MCP Test Throwaway 1116-120000 o1", Root + @"\probes\app-state\1116-130000\MCP Test Throwaway 1116-120000 o1")] // another run's folder
    [InlineData("MCP Test Throwaway 1116-120000 o1", RunRoot)] // the run folder itself
    [InlineData("MCP Test Throwaway 1116-120000 o1", @"\\server\C$\Users\me\AppData\Local\OneNoteMcp-Test\probes\app-state\1116-120000\o1")] // another server
    [InlineData("MCP Test Throwaway Cloud", "https://d.docs.live.net/0123456789ABCDEF/Documents/MCP Test Throwaway Cloud 2/")] // cloud look-alike
    public void CheckClose_RefusesLookAlikes(string name, string path) =>
        Assert.Throws<InvalidOperationException>(() => Close(AppStateNotebooks((name, "{L}", path)), "{L}"));

    [Fact]
    public void CheckClose_RefusesASectionId()
    {
        var notebooks = AppStateNotebooks();
        Assert.Throws<InvalidOperationException>(() => Close(notebooks, "{S}")); // a section's ID is never an open notebook
        Assert.Throws<InvalidOperationException>(() => Close(notebooks, "{b}")); // IDs compare exactly
    }

    [Theory]
    [InlineData(null)] // no run folder: run notebooks can't be closed
    [InlineData(Root + @"\probes\app-state\not-a-stamp")]
    [InlineData(Root + @"\probes\1116-120000")]
    [InlineData(@"D:\Elsewhere\1116-120000")]
    public void CheckClose_RunNotebook_NeedsARealRunFolder(string? runRoot) =>
        Assert.Throws<InvalidOperationException>(() => Close(AppStateNotebooks(), "{O1}", runRoot));

    [Theory] // Phill 2026-11-16 (O3): a notebook opened from this run's stamped probe URL may be closed
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Documents/probe missing 1116-120000/")]
    [InlineData("https://d.docs.live.net/0123456789abcdef/Documents/probe empty 1116-120000")] // the cid ignoring case, no trailing slash
    public void CheckClose_AllowsThisRunsStampedProbeUrl(string path) =>
        Close(AppStateNotebooks(("whatever OneNote named it", "{U}", path)), "{U}");

    [Theory]
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Documents/probe empty 1116-130000/")] // another run's stamp
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Documents/probe empty 1116-1200001/")] // a stamp look-alike
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Documents/Notes 1116-120000/")] // not a probe URL
    [InlineData("https://d.docs.live.net/FEDCBA9876543210/Documents/probe empty 1116-120000/")] // another account
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Documents/Sub/probe empty 1116-120000/")] // deeper
    [InlineData("https://contoso.sharepoint.com/Documents/probe empty 1116-120000/")] // another host
    public void CheckClose_RefusesOtherProbeUrls(string path) =>
        Assert.Throws<InvalidOperationException>(() => Close(AppStateNotebooks(("whatever OneNote named it", "{U}", path)), "{U}"));

    [Fact]
    public void CheckClose_ProbeUrl_NeedsARunFolder_AndNeverReachesTheTestNotebooks()
    {
        var notebooks = AppStateNotebooks(("whatever OneNote named it", "{U}", "https://d.docs.live.net/0123456789ABCDEF/Documents/probe empty 1116-120000/"));
        Assert.Throws<InvalidOperationException>(() => Close(notebooks, "{U}", runRoot: null)); // no run, no stamp
        Assert.Throws<InvalidOperationException>(() => Close(notebooks, "{C}")); // MCP Test Cloud, with the probe URL rule active
        Assert.Throws<InvalidOperationException>(() => Close(notebooks, "{T}")); // MCP Test
    }

    private static readonly string[] Recorded = [Root + @"\" + NameB, Root + @"\" + NameC, CloudThrowawayPath];

    private static void Open(string path, string notebooksXml, string? runRoot = RunRoot, string[]? recorded = null, bool allowOpenTest = false) =>
        TestNotebookGuard.CheckOpen("OpenHierarchy(cftNone)", path, notebooksXml, TestFolder, Root, runRoot, recorded ?? Recorded, allowOpenTest);

    [Theory]
    [InlineData(RunRoot + @"\MCP Test Throwaway 1116-120000 o2")]
    [InlineData(RunRoot + @"\probe missing 1116-120000")] // need not exist
    [InlineData(RunRoot + @"\probe no parent 1116-120000\probe child 1116-120000")]
    [InlineData(RunRoot + @"\probe junction 1116-120000")]
    [InlineData(@"\\localhost\C$\Users\me\AppData\Local\OneNoteMcp-Test\probes\app-state\1116-120000\MCP Test Throwaway 1116-120000 o1")]
    [InlineData(Root + @"\" + NameB)] // recorded b
    [InlineData(Root + @"\mcp test throwaway 1112-034716 c\")] // recorded c, ignoring case and the trailing separator
    [InlineData(CloudThrowawayPath)] // recorded cloud throwaway
    [InlineData("https://d.docs.live.net/0123456789abcdef/Documents/probe missing 1116-120000/")] // O3: MCP Test Cloud's account, this run's stamp
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Documents/probe empty 1116-120000")]
    public void CheckOpen_AllowsTheAllowedSet(string path) => Open(path, AppStateNotebooks());

    [Theory]
    [InlineData(RunRoot + @"\..\..\..\MCP Test")] // a .. path
    [InlineData(RunRoot + @"\x\..\o1")]
    [InlineData(Root + @"\probes\app-state\1116-130000\o1")] // another run's folder
    [InlineData(RunRoot)] // the run folder itself
    [InlineData(Root + @"\" + NameA)] // throwaway a, never recorded
    [InlineData(Root + @"\MCP Test Throwaway 1112-034716 b2")] // a name prefix
    [InlineData(@"D:\Notebooks\MCP Test Throwaway 1112-034716 b")] // b's name in the wrong folder
    [InlineData(TestFolder)] // MCP Test, without O4's flag
    [InlineData(CloudPath)] // MCP Test Cloud, without O4's flag
    [InlineData(@"D:\Notebooks\Work")] // any other notebook
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Documents/Personal/")]
    [InlineData("https://d.docs.live.net/FEDCBA9876543210/Documents/probe missing 1116-120000/")] // a cid other than MCP Test Cloud's
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Documents/probe missing 1116-130000/")] // another run's stamp
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Documents/missing 1116-120000/")] // not a probe name
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Documents/x/probe missing 1116-120000/")] // deeper
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Pictures/probe missing 1116-120000/")] // not Documents
    [InlineData("https://d.docs.live.net/0123456789ABCDEF/Documents/../Documents/probe missing 1116-120000/")]
    [InlineData("http://d.docs.live.net/0123456789ABCDEF/Documents/probe missing 1116-120000/")] // not https
    [InlineData("https://onedrive.live.com/0123456789ABCDEF/Documents/probe missing 1116-120000/")] // another host
    [InlineData(@"\\server\C$\Users\me\AppData\Local\OneNoteMcp-Test\probes\app-state\1116-120000\o1")]
    [InlineData("")]
    public void CheckOpen_RefusesOutsideTheAllowedSet(string path) =>
        Assert.Throws<InvalidOperationException>(() => Open(path, AppStateNotebooks()));

    [Fact]
    public void CheckOpen_ARecordedPathMustStillBeBsCsOrTheCloudThrowaways()
    {
        string[] recorded = [TestFolder, Root + @"\" + NameA, @"D:\Notebooks\Work"];
        foreach (var path in recorded)
        {
            Assert.Throws<InvalidOperationException>(() => Open(path, AppStateNotebooks(), recorded: recorded));
        }

        Assert.Throws<InvalidOperationException>(() => Open(Root + @"\" + NameB, AppStateNotebooks(), recorded: [])); // b's folder, but not recorded
    }

    [Fact]
    public void CheckOpen_O4_TestNotebooksOnlyWhileOpen()
    {
        Open(TestFolder, AppStateNotebooks(), allowOpenTest: true);
        Open(CloudPath, AppStateNotebooks(), allowOpenTest: true);
        Open(CloudPath.ToLowerInvariant().TrimEnd('/'), AppStateNotebooks(), allowOpenTest: true);

        var withoutTest = $$"""<one:Notebooks xmlns:one="{{Ns}}"><one:Notebook name="Personal" ID="{P}" path="D:\Notebooks\Personal" /></one:Notebooks>""";
        Assert.Throws<InvalidOperationException>(() => Open(TestFolder, withoutTest, allowOpenTest: true)); // MCP Test closed
        Assert.Throws<InvalidOperationException>(() => Open(CloudPath, withoutTest, allowOpenTest: true)); // MCP Test Cloud closed
        Assert.Throws<InvalidOperationException>(() => Open(@"D:\Notebooks\Personal", withoutTest, allowOpenTest: true)); // the flag is for test notebooks only
    }

    [Fact]
    public void CheckOpen_O3_NeedsMcpTestCloudOpenAndARunFolder()
    {
        const string Url = "https://d.docs.live.net/0123456789ABCDEF/Documents/probe missing 1116-120000/";
        var withoutCloud = CloudNotebooks();
        Assert.Throws<InvalidOperationException>(() => Open(Url, withoutCloud)); // no cid to compare
        Assert.Throws<InvalidOperationException>(() => Open(Url, AppStateNotebooks(), runRoot: null)); // no stamp
        Assert.Throws<InvalidOperationException>(() => Open(RunRoot + @"\o1", AppStateNotebooks(), runRoot: null));
    }

    [Theory]
    [InlineData(@"\\localhost\C$\Users\me\x", @"C:\Users\me\x")]
    [InlineData(@"\\LocalHost\d$\x\", @"D:\x\")]
    [InlineData(@"\\localhost\C$", @"C:\")]
    public void MapLocalhostAdminShare_MapsTheAdminShare(string unc, string local) =>
        Assert.Equal(local, TestNotebookGuard.MapLocalhostAdminShare(unc));

    [Theory]
    [InlineData(@"\\server\C$\x")]
    [InlineData(@"\\localhost\share\x")]
    [InlineData(@"\\localhost\CD$\x")]
    [InlineData(@"C:\x")]
    [InlineData("https://d.docs.live.net/x/")]
    [InlineData(null)]
    public void MapLocalhostAdminShare_LeavesAnythingElse(string? path) =>
        Assert.Null(TestNotebookGuard.MapLocalhostAdminShare(path));

    [Theory]
    [InlineData(RunRoot, true)]
    [InlineData(RunRoot + @"\", true)]
    [InlineData(Root + @"\probes\app-state\1116-12000", false)] // not a stamp
    [InlineData(Root + @"\probes\app-state\1116-120000\x", false)] // deeper
    [InlineData(Root + @"\probes\app-state\..\app-state\1116-120000", false)]
    [InlineData(@"D:\probes\app-state\1116-120000", false)]
    [InlineData(@"probes\app-state\1116-120000", false)]
    public void IsRunProbeRoot_OnlyAStampFolderInAppState(string path, bool expected) =>
        Assert.Equal(expected, TestNotebookGuard.IsRunProbeRoot(path, Root));

    [Fact]
    public void ThrowawayNotebookIds_AcceptsARunNotebookByItsName()
    {
        var notebooks = AppStateNotebooks();

        Assert.Equal(["{O1}"], TestNotebookGuard.ThrowawayNotebookIds(notebooks, Root, Names(("{O1}", ["MCP Test Throwaway 1116-120000 o1"]))));
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.ThrowawayNotebookIds(notebooks, Root, Names(("{SG}", ["probe sg 1116-120000"])))); // not a throwaway name: the guard's writes key on names
    }

    [Fact]
    public void NotebooksUnder_TheRunFoldersNotebooksWhateverTheirNames() =>
        Assert.Equal(["{O1}", "{SG}"], TestNotebookGuard.NotebooksUnder(AppStateNotebooks(), RunRoot).Select(n => n.Id).Order(StringComparer.Ordinal));

    [Fact]
    public void CloudCid_IsMcpTestCloudsAccount()
    {
        Assert.Equal("0123456789ABCDEF", TestNotebookGuard.CloudCid(AppStateNotebooks()));
        Assert.Null(TestNotebookGuard.CloudCid(CloudNotebooks()));
    }

    [Fact]
    public void Calls_CloseSendsForceFalse_OpenSendsCftNone_SyncSendsTheId()
    {
        var app = RecordingApplication.Create();
        var recorder = (RecordingApplication)(object)app;

        TestNotebook.CloseCall(app, "{B}");
        Assert.Equal("CloseNotebook", recorder.Calls[^1].Method);
        Assert.Equal(new object?[] { "{B}", false }, recorder.Calls[^1].Args);

        Assert.Equal("{OPENED}", TestNotebook.OpenExistingCall(app, @"C:\x"));
        Assert.Equal("OpenHierarchy", recorder.Calls[^1].Method);
        Assert.Equal(@"C:\x", recorder.Calls[^1].Args[0]);
        Assert.Equal(string.Empty, recorder.Calls[^1].Args[1]);
        Assert.Equal(OneNoteMcp.Com.Interop.CreateFileType.cftNone, recorder.Calls[^1].Args[3]);

        TestNotebook.SyncCall(app, "{S}");
        Assert.Equal("SyncHierarchy", recorder.Calls[^1].Method);
        Assert.Equal(new object?[] { "{S}" }, recorder.Calls[^1].Args);
        Assert.Equal(3, recorder.Calls.Count); // nothing else was called
    }

    [Fact]
    public void Wrappers_HaveNoForceOrCreateTypeParameter()
    {
        var close = typeof(TestNotebook).GetMethod(nameof(TestNotebook.CloseNotebookAsync))!;
        var open = typeof(TestNotebook).GetMethod(nameof(TestNotebook.OpenExistingAsync))!;

        Assert.DoesNotContain(close.GetParameters(), p => p.ParameterType == typeof(bool));
        Assert.DoesNotContain(open.GetParameters(), p => p.ParameterType == typeof(OneNoteMcp.Com.Interop.CreateFileType));
        Assert.DoesNotContain(close.GetParameters(), p => p.Name?.Contains("force", StringComparison.OrdinalIgnoreCase) == true);
    }

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

/// <summary>Spike 4.1: a stand-in <c>IApplication</c> that records each call's name and arguments (no COM).</summary>
public class RecordingApplication : System.Reflection.DispatchProxy
{
    public List<(string Method, object?[] Args)> Calls { get; } = [];

    internal static OneNoteMcp.Com.Interop.IApplication Create() => Create<OneNoteMcp.Com.Interop.IApplication, RecordingApplication>();

    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        args ??= [];
        if (targetMethod.Name == "OpenHierarchy")
        {
            args[2] = "{OPENED}"; // the out ID
        }

        Calls.Add((targetMethod.Name, (object?[])args.Clone()));
        return null;
    }
}
