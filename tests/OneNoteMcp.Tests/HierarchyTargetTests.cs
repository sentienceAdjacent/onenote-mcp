using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// The one reader for the target of a delete or change (backlog story "One reader for the target of a delete or change"):
/// <see cref="HierarchyTarget.Read"/>, its children and <see cref="HierarchyTarget.Page"/> against the odd inputs OneNote
/// never writes, so the single recycle-bin rule, <c>pageLevel</c> parse and subpage count stay pinned.
/// </summary>
public sealed class HierarchyTargetTests
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";

    // --- Bin by attribute, any kind; bin by name -----------------------------------------------------------------------

    [Theory]
    [InlineData("Notebook")]
    [InlineData("SectionGroup")]
    [InlineData("Section")]
    [InlineData("Page")]
    public void Read_IsRecycleBinOnAnyElement_IsTheRecycleBin(string element)
    {
        var target = HierarchyTarget.Read($"<one:{element} {Ns} ID=\"X\" name=\"Plain\" isRecycleBin=\"true\" />");

        Assert.True(target.IsRecycleBin);
    }

    [Theory]
    [InlineData("SectionGroup", true)]
    [InlineData("Section", false)]
    [InlineData("Notebook", false)]
    public void Read_TheRecycleBinName_MarksOnlyASectionGroup(string element, bool expected)
    {
        var target = HierarchyTarget.Read($"<one:{element} {Ns} ID=\"X\" name=\"OneNote_RecycleBin\" />");

        Assert.Equal(expected, target.IsRecycleBin);
    }

    [Fact]
    public void Read_AChildCarryingIsRecycleBin_IsNeverListed()
    {
        var target = HierarchyTarget.Read(
            $"<one:Notebook {Ns} ID=\"N\" name=\"A\"><one:Section ID=\"S\" name=\"S\" /><one:Section ID=\"B\" name=\"Plain\" isRecycleBin=\"true\" /></one:Notebook>");

        Assert.Equal(["S"], target.Children.Select(c => c.Id));
    }

    // --- Recycle states ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", false, false)]
    [InlineData("isInRecycleBin=\"true\"", true, false)]
    [InlineData("isDeletedPages=\"true\"", false, true)]
    [InlineData("isInRecycleBin=\"true\" isDeletedPages=\"true\"", true, true)]
    public void Read_TheRecycleStates_AreEachReadAsWritten_AndInRecycleBinIsEither(string attributes, bool inBin, bool deletedPages)
    {
        var target = HierarchyTarget.Read($"<one:Section {Ns} ID=\"S\" name=\"S\" {attributes} />");

        Assert.Equal((inBin, deletedPages, inBin || deletedPages), (target.IsInRecycleBin, target.IsDeletedPages, target.InRecycleBin));
        Assert.False(target.IsRecycleBin);
    }

    // --- pageLevel -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, 1)] // absent
    [InlineData("", 1)]
    [InlineData("x", 1)]
    [InlineData("2.5", 1)]
    [InlineData("0x2", 1)]
    [InlineData("99999999999", 1)] // overflow
    [InlineData(" 2 ", 2)] // padded
    [InlineData("+2", 2)] // signed
    [InlineData("0", 1)] // below 1
    [InlineData("-1", 1)]
    [InlineData("3", 3)]
    public void APagesLevel_IsOneParse_ForChildrenAndForPage(string? level, int expected)
    {
        var attribute = level is null ? string.Empty : $" pageLevel=\"{level}\"";
        var xml = $"<one:Section {Ns} ID=\"S\" name=\"S\"><one:Page ID=\"P\" name=\"p\"{attribute} /></one:Section>";

        Assert.Equal(expected, Assert.Single(HierarchyTarget.Read(xml).Children).PageLevel);
        Assert.Equal(expected, HierarchyTarget.Page(xml, "P")!.PageLevel);
    }

    // --- Subpages around odd levels ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0", "1", 0)] // a level below 1 reads as 1, so the next page at 1 isn't its subpage
    [InlineData("-1", "2", 1)]
    [InlineData("1", "+2", 1)] // a signed level is read, so the page after is a subpage
    [InlineData("1", " 2 ", 1)]
    [InlineData("1", "x", 0)]
    [InlineData("x", "2", 1)]
    [InlineData("1", "0", 0)]
    public void Page_CountsSubpages_WithTheSameLevelParse(string first, string second, int expected)
    {
        var xml = $"<one:Section {Ns} ID=\"S\" name=\"S\">"
            + $"<one:Page ID=\"A\" name=\"a\" pageLevel=\"{first}\" /><one:Page ID=\"B\" name=\"b\" pageLevel=\"{second}\" /><one:Page ID=\"C\" name=\"c\" pageLevel=\"1\" />"
            + "</one:Section>";

        Assert.Equal(expected, HierarchyTarget.Page(xml, "A")!.Subpages);
    }

    [Fact]
    public void Page_ReadsOnlyTheRootsDirectPages()
    {
        var xml = $"<one:SectionGroup {Ns} ID=\"G\" name=\"G\"><one:Section ID=\"S\" name=\"S\"><one:Page ID=\"P\" name=\"p\" /></one:Section></one:SectionGroup>";

        Assert.Null(HierarchyTarget.Page(xml, "P"));
    }

    [Fact]
    public void Page_ReadsItsNameAndDate_AndNullForAPageNotListed()
    {
        var xml = $"<one:Section {Ns} ID=\"S\" name=\"S\"><one:Page ID=\"P\" name=\"Top\" lastModifiedTime=\"2026-11-15T10:00:00.000Z\" /></one:Section>";

        Assert.Equal(new SectionPage("Top", "2026-11-15T10:00:00.000Z", 1, 0), HierarchyTarget.Page(xml, "P"));
        Assert.Null(HierarchyTarget.Page(xml, "Z"));
    }

    // --- Not well-formed / no root -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("<one:Section")]
    [InlineData("")]
    [InlineData("<?xml version=\"1.0\"?>")]
    public void NotWellFormed_OrNoRoot_Throws(string xml)
    {
        Assert.Throws<OneNoteXmlException>(() => HierarchyTarget.Read(xml));
        Assert.Throws<OneNoteXmlException>(() => HierarchyTarget.Page(xml, "P"));
    }
}
