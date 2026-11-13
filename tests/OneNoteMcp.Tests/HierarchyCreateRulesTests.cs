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

    // Security review, 2026-11-13: control (Cc) characters past ASCII, line and paragraph separators, bidi controls and
    // invisible format characters, which could make a name in a dialog or a listing read as something it isn't.
    [Theory]
    [InlineData("a\u007Fb")]
    [InlineData("a\u0085b")]
    [InlineData("a\u009Fb")]
    [InlineData("a\u2028b")]
    [InlineData("a\u2029b")]
    [InlineData("a\u200Eb")]
    [InlineData("a\u200Fb")]
    [InlineData("a\u202Ab")]
    [InlineData("a\u202Bb")]
    [InlineData("a\u202Cb")]
    [InlineData("a\u202Db")]
    [InlineData("a\u202Eb")]
    [InlineData("a\u2066b")]
    [InlineData("a\u2067b")]
    [InlineData("a\u2068b")]
    [InlineData("a\u2069b")]
    [InlineData("a\u200Bb")]
    [InlineData("a\u200Cb")]
    [InlineData("a\u200Db")]
    [InlineData("a\u2060b")]
    [InlineData("\uFEFFab")]
    public void NameProblem_RefusesControlBidiAndInvisibleCharacters_NeverQuotingTheName(string name)
    {
        var problem = SectionFileNames.NameProblem(name);

        Assert.NotNull(problem);
        Assert.DoesNotContain(name, problem, StringComparison.Ordinal);
        Assert.DoesNotContain("ab", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void NameProblem_AllowsUpTo128Utf16Characters()
    {
        Assert.Null(SectionFileNames.NameProblem(new string('a', SectionFileNames.MaxNameLength)));
        Assert.Equal(128, SectionFileNames.MaxNameLength);

        var problem = SectionFileNames.NameProblem(new string('a', SectionFileNames.MaxNameLength + 1));
        Assert.NotNull(problem);
        Assert.DoesNotContain("aaaa", problem, StringComparison.Ordinal);

        // UTF-16 code units, not text elements: 64 surrogate pairs fit, 65 don't.
        Assert.Null(SectionFileNames.NameProblem(string.Concat(Enumerable.Repeat("\U0001F600", 64))));
        Assert.NotNull(SectionFileNames.NameProblem(string.Concat(Enumerable.Repeat("\U0001F600", 65))));
    }

    [Fact]
    public void InsideANotebookFolder_IsTrue_WhenTheFolderOrAnAncestorHoldsAnOnetoc2File()
    {
        using var temp = new TempDirectory();
        var closed = Directory.CreateDirectory(temp.Combine("closed")).FullName;
        File.WriteAllText(Path.Combine(closed, "Open Notebook.onetoc2"), "x");
        var group = Directory.CreateDirectory(Path.Combine(closed, "Group")).FullName;
        var plain = Directory.CreateDirectory(temp.Combine("plain")).FullName;
        var hidden = Directory.CreateDirectory(temp.Combine("hidden")).FullName;
        var toc = Path.Combine(hidden, "x.ONETOC2");
        File.WriteAllText(toc, "x");
        File.SetAttributes(toc, FileAttributes.Hidden);

        Assert.True(NotebookFolders.InsideANotebookFolder(closed));
        Assert.True(NotebookFolders.InsideANotebookFolder(group));
        Assert.True(NotebookFolders.InsideANotebookFolder(Path.Combine(group, "missing", "deeper")));
        Assert.True(NotebookFolders.InsideANotebookFolder(hidden));
        Assert.False(NotebookFolders.InsideANotebookFolder(plain));
        Assert.False(NotebookFolders.InsideANotebookFolder(Path.Combine(plain, "missing")));
        Assert.False(NotebookFolders.InsideANotebookFolder("https://d.docs.live.net/0123/x"));
    }

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
