using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>Story 1.20's Xml pieces (AD-21): <see cref="EncryptedSections"/>, <see cref="HierarchyNode"/>, and the section-scoped page cut.</summary>
public sealed class ProtectedSectionXmlTests
{
    private const string Ns = "xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\"";

    private const string Tree =
        "<?xml version=\"1.0\"?>\n<one:Notebooks " + Ns + ">"
        + "<one:Notebook name=\"N\" nickname=\"Notebook A\" ID=\"{N}\" path=\"C:\\nb\\\">"
        + "<one:Section name=\"Open\" ID=\"{S0}\" path=\"C:\\nb\\Open.one\"><one:Page ID=\"{P0}\" name=\"p0\" /></one:Section>"
        + "<one:Section name=\"V1\" ID=\"{S1}\" path=\"C:\\nb\\V1.one\" encrypted=\"true\"><one:Page ID=\"{P1}\" name=\"p1\"><one:Meta name=\"m\" content=\"c\" /></one:Page><one:Page ID=\"{P2}\" name=\"p2\" /></one:Section>"
        + "<one:SectionGroup name=\"G\" ID=\"{G}\"><one:Section name=\"V2\" ID=\"{S2}\" path=\"C:\\nb\\G\\V2.one\" encrypted=\"1\" locked=\"true\" /></one:SectionGroup>"
        + "<one:Section name=\"NotQuite\" ID=\"{S3}\" path=\"C:\\nb\\N.one\" encrypted=\"false\" />"
        + "</one:Notebook></one:Notebooks>";

    [Fact]
    public void EncryptedSections_FindsTrueAndOne_WithLocationAndNotebookPath()
    {
        var found = EncryptedSections.Find(Tree);

        Assert.Equal(["{S1}", "{S2}"], found.Select(s => s.Id));
        Assert.Equal(("V1", @"C:\nb\V1.one", false), (found[0].Name, found[0].Path, found[0].Locked));
        Assert.Equal(["Notebook A"], found[0].Location);
        Assert.Equal(["Notebook A", "G"], found[1].Location);
        Assert.True(found[1].Locked);
        Assert.Equal(@"C:\nb\", found[0].NotebookPath);
    }

    [Fact]
    public void EncryptedSections_ARootSection_IsFound()
    {
        var found = Assert.Single(EncryptedSections.Find("<one:Section " + Ns + " name=\"V\" ID=\"{S}\" path=\"p\" encrypted=\"true\" />"));

        Assert.Equal("{S}", found.Id);
        Assert.Empty(found.Location);
        Assert.Equal(string.Empty, found.NotebookPath);
    }

    [Fact]
    public void EncryptedSections_Malformed_Throws() =>
        Assert.Throws<OneNoteXmlException>(() => EncryptedSections.Find("<one:Section"));

    [Theory]
    [InlineData("<one:Notebooks " + Ns + " />", HierarchyKind.Other)]
    [InlineData("<one:Notebook " + Ns + " ID=\"{N}\" name=\"n\" nickname=\"nick\" path=\"p\" />", HierarchyKind.Notebook)]
    [InlineData("<one:SectionGroup " + Ns + " ID=\"{G}\" name=\"g\" />", HierarchyKind.SectionGroup)]
    [InlineData("<one:Section " + Ns + " ID=\"{S}\" name=\"s\" encrypted=\"true\" />", HierarchyKind.Section)]
    [InlineData("<one:Page " + Ns + " ID=\"{P}\" name=\"p\" />", HierarchyKind.Page)]
    [InlineData("<Section ID=\"{S}\" encrypted=\"true\" />", HierarchyKind.Other)]
    public void HierarchyNode_Root_Kind(string xml, HierarchyKind kind) =>
        Assert.Equal(kind, HierarchyNode.Root(xml).Kind);

    [Fact]
    public void HierarchyNode_Root_ReadsTheAttributes_NotebookDisplayNameIsTheNickname()
    {
        var notebook = HierarchyNode.Root("<one:Notebook " + Ns + " ID=\"{N}\" name=\"n\" nickname=\"nick\" path=\"C:\\nb\" />");
        var section = HierarchyNode.Root("<one:Section " + Ns + " ID=\"{S}\" name=\"s\" path=\"C:\\nb\\s.one\" encrypted=\"true\" locked=\"1\" />");

        Assert.Equal(("{N}", "n", "nick", @"C:\nb", false), (notebook.Id, notebook.Name, notebook.DisplayName, notebook.Path, notebook.Encrypted));
        Assert.Equal((true, true), (section.Encrypted, section.Locked));
    }

    [Fact]
    public void SearchPages_CarryTheirNearestSectionId()
    {
        var pages = SearchResultFilter.Pages(Tree);

        Assert.Equal([("{P0}", "{S0}"), ("{P1}", "{S1}"), ("{P2}", "{S1}")], pages.Select(p => (p.Id, p.SectionId)));
        Assert.Equal(string.Empty, Assert.Single(SearchResultFilter.Pages("<one:Page " + Ns + " ID=\"{P}\" name=\"p\" />")).SectionId);
    }

    [Fact]
    public void RemovePagesInSections_CutsOnlyThosePages_KeepsTheSection_AndEveryOtherByte()
    {
        var cut = SearchResultFilter.RemovePagesInSections(Tree, new HashSet<string> { "{S1}" });

        Assert.Equal(["{P1}", "{P2}"], cut.RemovedIds);
        Assert.Equal(
            Tree.Replace("<one:Page ID=\"{P1}\" name=\"p1\"><one:Meta name=\"m\" content=\"c\" /></one:Page><one:Page ID=\"{P2}\" name=\"p2\" />", string.Empty, StringComparison.Ordinal),
            cut.Xml);
        Assert.Same(Tree, SearchResultFilter.RemovePagesInSections(Tree, new HashSet<string>()).Xml);
    }
}
