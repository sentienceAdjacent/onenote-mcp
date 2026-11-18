using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 2.1 (A3): Core's <see cref="ProtectedSections"/>, the grant CLI's way to the Xml model. It gives the same
/// sections and location as <see cref="EncryptedSections"/>, with the key computed and unreadable XML as InvalidXml.
/// </summary>
public sealed class ProtectedSectionsTests
{
    private const string Ns = "xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\"";

    private const string Tree =
        "<one:Notebooks " + Ns + ">"
        + "<one:Notebook name=\"N\" nickname=\"Notebook A\" ID=\"{N}\" path=\"C:\\nb\\\">"
        + "<one:Section name=\"V1\" ID=\"{S1}\" path=\"C:\\nb\\V1.one\\\" encrypted=\"true\" />"
        + "<one:SectionGroup name=\"G\" ID=\"{G}\"><one:SectionGroup name=\"H\" ID=\"{H}\">"
        + "<one:Section name=\"V2\" ID=\"{S2}\" path=\"C:\\nb\\G\\H\\V2.one\" encrypted=\"1\" locked=\"true\" />"
        + "</one:SectionGroup></one:SectionGroup>"
        + "<one:Section name=\"Open\" ID=\"{S3}\" path=\"C:\\nb\\Open.one\" />"
        + "</one:Notebook></one:Notebooks>";

    [Fact]
    public void Find_MatchesEncryptedSections_WithKeyNotebookAndGroups()
    {
        var found = ProtectedSections.Find(Tree, new Protection([]));
        var xml = EncryptedSections.Find(Tree, _ => false);

        Assert.Equal(xml.Select(s => (s.Id, s.Name, s.Locked)), found.Select(s => (s.Id, s.Name, s.Locked)));
        Assert.Equal(xml.Select(s => s.Location), found.Select(s => s.Location));
        Assert.Equal([@"C:\nb\V1.one", @"C:\nb\G\H\V2.one"], found.Select(s => s.Key));
        Assert.All(found, s => Assert.Equal(("Notebook A", @"C:\nb\"), (s.NotebookName, s.NotebookPath)));
        Assert.Empty(found[0].Groups);
        Assert.Equal(["G", "H"], found[1].Groups);
        Assert.Equal(["Notebook A", "G", "H"], found[1].Location);
    }

    [Fact]
    public void Find_WithAnExportRoot_ListsTheUnmarkedSectionsUnderIt_ForTheGrantCli()
    {
        // Entry 15 (AD-21 protected by path): Open carries no marker, but the notebook's folder is a root.
        var found = ProtectedSections.Find(Tree, new Protection([@"C:\nb"]));

        Assert.Equal(["{S1}", "{S2}", "{S3}"], found.Select(s => s.Id));
        Assert.Equal(@"C:\nb\Open.one", found[2].Key);
    }

    [Fact]
    public void Find_ALooseSectionUnderOpenSections_UnderARoot_IsListed()
    {
        // Probe P3 (2026-11-17): a .one opened by hand sits under one:OpenSections, outside any notebook.
        const string tree = "<one:Notebooks " + Ns + "><one:OpenSections ID=\"{OS}\">"
            + "<one:Section name=\"Copy\" ID=\"{L}\" path=\"C:\\exports\\P3\\Copy.one\" />"
            + "<one:Section name=\"Elsewhere\" ID=\"{E}\" path=\"C:\\elsewhere\\E.one\" />"
            + "</one:OpenSections></one:Notebooks>";

        var found = Assert.Single(ProtectedSections.Find(tree, new Protection([@"C:\exports"])));

        Assert.Equal("{L}", found.Id);
        Assert.Null(found.NotebookName);
    }

    [Fact]
    public void Find_ARootSection_HasNoNotebook_AndAnEmptyLocation()
    {
        var found = Assert.Single(ProtectedSections.Find("<one:Section " + Ns + " name=\"V\" ID=\"{S}\" path=\"\" encrypted=\"true\" />", new Protection([])));

        Assert.Null(found.NotebookName);
        Assert.Equal(string.Empty, found.NotebookPath);
        Assert.Equal(string.Empty, found.Key);
        Assert.Empty(found.Location);
    }

    [Fact]
    public void Find_UnreadableXml_IsInvalidXml_WithTheCommonMessage_AndNoInnerException()
    {
        var ex = Assert.Throws<OneNoteException>(() => ProtectedSections.Find("<one:Section", new Protection([])));

        Assert.Equal(ErrorCode.InvalidXml, ex.Code);
        Assert.StartsWith("GetHierarchy returned XML the server can't read: The XML is not well-formed (line ", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }
}
