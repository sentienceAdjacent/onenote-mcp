using OneNoteMcp.Core;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 5.3's pure rules: the name every new section, section group and notebook must pass (<see cref="SectionFileNames"/>),
/// Q2's <c>.one</c> rule, the parent-and-children reader (<see cref="HierarchyChildren"/>) and folder containment
/// (<see cref="NotebookFolders"/>).
/// </summary>
public sealed class HierarchyCreateRulesTests
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";

    [Theory]
    [InlineData("Ideas", true)]
    [InlineData("Trips 2026", true)]
    [InlineData("a.b", true)]
    [InlineData("Notes.one", true)]
    [InlineData("Ünïcödé", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("..", false)]
    [InlineData("a..b", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a<b", false)]
    [InlineData("a>b", false)]
    [InlineData("a:b", false)]
    [InlineData("a\"b", false)]
    [InlineData("a|b", false)]
    [InlineData("a?b", false)]
    [InlineData("a*b", false)]
    [InlineData("a\u0001b", false)]
    [InlineData(" a", false)]
    [InlineData("a ", false)]
    [InlineData(".a", false)]
    [InlineData("a.", false)]
    [InlineData("CON", false)]
    [InlineData("con", false)]
    [InlineData("NUL.txt", false)]
    [InlineData("COM1", false)]
    [InlineData("LPT9.one", false)]
    [InlineData("CON .x", false)]
    [InlineData("CONSOLE", true)]
    [InlineData("OneNote_RecycleBin", false)]
    [InlineData("onenote_recyclebin", false)]
    [InlineData("OneNote_DeletedPages", false)]
    [InlineData("OneNote_DeletedPages.one", false)]
    public void NameProblem_OneFileSystemName_NotReservedByWindowsOrOneNote(string name, bool ok) =>
        Assert.Equal(ok, SectionFileNames.NameProblem(name) is null);

    [Theory]
    [InlineData("a/b")]
    [InlineData("CON")]
    [InlineData("OneNote_RecycleBin")]
    [InlineData("x?")]
    public void NameProblem_NeverQuotesTheName(string name) =>
        Assert.DoesNotContain(name, SectionFileNames.NameProblem(name)!, StringComparison.Ordinal);

    [Theory]
    [InlineData("Ideas", "Ideas.one")]
    [InlineData("Ideas.one", "Ideas.one")]
    [InlineData("Ideas.ONE", "Ideas.ONE")]
    [InlineData("Ideas.txt", "Ideas.txt.one")]
    [InlineData("one", "one.one")]
    public void ToSectionFileName_AddsOneUnlessItEndsWithIt(string name, string fileName) =>
        Assert.Equal(fileName, SectionFileNames.ToSectionFileName(name));

    [Theory]
    [InlineData("Ideas.one", "Ideas")]
    [InlineData("Ideas.ONE", "Ideas")]
    [InlineData("Ideas", "Ideas")]
    public void SectionName_DropsOne(string fileName, string name) =>
        Assert.Equal(name, SectionFileNames.SectionName(fileName));

    [Theory]
    [InlineData("Recovered.one", true)]
    [InlineData("Recovered", false)]
    [InlineData("Recovered .one", false)]
    [InlineData("CON .x.one", false)]
    public void Problem_KeepsItsContractForTheRecoveredSection(string fileName, bool ok) =>
        Assert.Equal(ok, SectionFileNames.Problem(fileName) is null);

    [Fact]
    public void HierarchyChildren_ListsLiveSectionsAndGroups_SkippingTheRecycleBinAndWhatIsInIt()
    {
        var parent = HierarchyChildren.Read($"""
            <one:Notebook {Ns} name="Notebook A" ID="{"{N}"}" path="C:\n\A">
              <one:Section name="Ideas" ID="{"{S1}"}" path="C:\n\A\Ideas.one" />
              <one:Section name="Old" ID="{"{S2}"}" path="C:\n\A\Old.one" isInRecycleBin="true" />
              <one:SectionGroup name="Archive" ID="{"{G1}"}" path="C:\n\A\Archive">
                <one:Section name="Nested" ID="{"{S3}"}" path="C:\n\A\Archive\Nested.one" />
              </one:SectionGroup>
              <one:SectionGroup name="OneNote_RecycleBin" ID="{"{RB}"}" path="C:\n\A\OneNote_RecycleBin" isRecycleBin="true" />
            </one:Notebook>
            """);

        Assert.Equal(HierarchyKind.Notebook, parent.Kind);
        Assert.Equal("{N}", parent.Id);
        Assert.False(parent.IsRecycleBin);
        Assert.False(parent.InRecycleBin);
        Assert.Equal(
            [new HierarchyChild(HierarchyKind.Section, "{S1}", "Ideas"), new HierarchyChild(HierarchyKind.SectionGroup, "{G1}", "Archive")],
            parent.Children);
    }

    [Theory]
    [InlineData("<one:SectionGroup " + Ns + " name=\"OneNote_RecycleBin\" ID=\"{RB}\" isRecycleBin=\"true\" />", HierarchyKind.SectionGroup, true, false)]
    [InlineData("<one:SectionGroup " + Ns + " name=\"OneNote_RecycleBin\" ID=\"{RB}\" />", HierarchyKind.SectionGroup, true, false)]
    [InlineData("<one:SectionGroup " + Ns + " name=\"Gone\" ID=\"{G}\" isInRecycleBin=\"true\" />", HierarchyKind.SectionGroup, false, true)]
    [InlineData("<one:Section " + Ns + " name=\"Deleted Pages\" ID=\"{D}\" isInRecycleBin=\"true\" isDeletedPages=\"true\" />", HierarchyKind.Section, false, true)]
    [InlineData("<one:Page " + Ns + " name=\"P\" ID=\"{P}\" />", HierarchyKind.Page, false, false)]
    public void HierarchyChildren_TheParentsKindAndRecycleBinState(string xml, HierarchyKind kind, bool isRecycleBin, bool inRecycleBin)
    {
        var parent = HierarchyChildren.Read(xml);

        Assert.Equal(kind, parent.Kind);
        Assert.Equal(isRecycleBin, parent.IsRecycleBin);
        Assert.Equal(inRecycleBin, parent.InRecycleBin);
    }

    [Fact]
    public void HierarchyChildren_BadXml_Throws() =>
        Assert.Throws<OneNoteXmlException>(() => HierarchyChildren.Read("<not closed"));

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a", true)]
    [InlineData(@"c:\A\B\", @"C:\a\", true)]
    [InlineData(@"C:\a\b\c", @"C:\a", true)]
    [InlineData(@"C:\a", @"C:\a", false)]
    [InlineData(@"C:\ab", @"C:\a", false)]
    [InlineData(@"C:\a-Test\x", @"C:\a", false)]
    [InlineData(@"C:\a", @"C:\a\b", false)]
    [InlineData(@"C:\a\b", @"C:\", true)]
    [InlineData(@"C:\a\b", "https://d.docs.live.net/x/Documents/a/", false)]
    public void NotebookFolders_Inside_IsStrict_AndIgnoresCase(string path, string folder, bool inside) =>
        Assert.Equal(inside, NotebookFolders.Inside(path, folder));

    [Theory]
    [InlineData(@"C:\a\b", @"c:\A\B\", true)]
    [InlineData(@"C:\a\b", @"C:\a\b\c", false)]
    [InlineData(@"C:\a\.\b", @"C:\a\b", true)]
    public void NotebookFolders_Same_IgnoresCaseAndTrailingSeparators(string path, string folder, bool same) =>
        Assert.Equal(same, NotebookFolders.Same(path, folder));

    [Fact]
    public void NotebookFolders_State_AbsentEmptyNotEmptyAndFile()
    {
        using var temp = new TempDirectory();
        var folder = temp.Combine("f");
        Assert.Equal(FolderState.Absent, NotebookFolders.State(folder));

        Directory.CreateDirectory(folder);
        Assert.Equal(FolderState.Empty, NotebookFolders.State(folder));

        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        Assert.Equal(FolderState.NotEmpty, NotebookFolders.State(folder));

        var file = temp.Combine("file");
        File.WriteAllText(file, "x");
        Assert.Equal(FolderState.File, NotebookFolders.State(file));
    }

    [Fact]
    public void CloudNotebookCreate_IsClosed_AndKnowsAUrl()
    {
        Assert.False(CloudNotebookCreate.Enabled);
        Assert.True(CloudNotebookCreate.IsUrl("https://d.docs.live.net/x/Documents"));
        Assert.False(CloudNotebookCreate.IsUrl(@"C:\Users\x\Documents"));
        Assert.False(CloudNotebookCreate.IsUrl(null));
    }
}
