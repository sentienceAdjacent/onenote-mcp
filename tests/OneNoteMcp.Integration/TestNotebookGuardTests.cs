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
    public void EnsureInside_ThrowsForAForeignTarget() =>
        Assert.Throws<InvalidOperationException>(() => TestNotebookGuard.EnsureInside(Tree, "{P}", "write"));

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
