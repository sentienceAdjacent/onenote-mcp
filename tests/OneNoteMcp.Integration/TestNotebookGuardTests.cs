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
