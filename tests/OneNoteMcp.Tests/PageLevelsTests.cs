using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 3.14's pure level rules (<see cref="PageLevels"/>): every row of the plan's I/O matrix that a level change or a
/// subpage placement decides, without the pipeline. Pages are written as "name:level".
/// </summary>
public sealed class PageLevelsTests
{
    [Theory]
    [InlineData("P:1 A:1", "A", 2, "P:1 A:2")] // Indent
    [InlineData("P:1 A:1 B:2", "A", 2, "P:1 A:2 B:3")] // Indent with block: the subpage moves by the same amount (Q2)
    [InlineData("P:1 A:2 B:2", "A", 1, "P:1 A:1 B:2")] // Outdent re-parents: B now under A
    [InlineData("P:1 A:2 B:3 C:1", "A", 1, "P:1 A:1 B:2 C:1")] // Outdent with block
    [InlineData("P:1 A:2 B:3", "B", 2, "P:1 A:2 B:2")] // A subpage outdented under its grandparent
    public void Change_ReLevelsThePageAndItsBlock_EveryOtherPageAsRead(string pages, string page, int level, string expected)
    {
        var result = PageLevels.Change(Pages(pages), Id(page), level);

        Assert.Equal(PageLevelProblem.None, result.Problem);
        Assert.False(result.Unchanged);
        Assert.Equal(expected, Text(result.Pages));
    }

    [Fact]
    public void Change_MovedListsThePageThenItsBlock()
    {
        var result = PageLevels.Change(Pages("P:1 A:1 B:2 C:2 D:1"), Id("A"), 2);

        Assert.Equal([Id("A"), Id("B"), Id("C")], result.Moved);
        Assert.Equal(1, result.Index);
    }

    [Fact]
    public void Change_ToTheSameLevel_IsUnchanged()
    {
        var result = PageLevels.Change(Pages("P:1 A:2"), Id("A"), 2);

        Assert.Equal(PageLevelProblem.None, result.Problem);
        Assert.True(result.Unchanged);
        Assert.Empty(result.Moved);
        Assert.Equal("P:1 A:2", Text(result.Pages));
    }

    [Theory]
    [InlineData("P:1 A:2 B:3", "A", 3, PageLevelProblem.GapBefore, "A")] // Block overflows, caught by the gap first
    [InlineData("P:1 Q:2 A:2 B:3", "A", 3, PageLevelProblem.SubpageOutOfRange, "B")] // Block overflows
    [InlineData("P:1 A:1", "A", 3, PageLevelProblem.GapBefore, "A")] // Gap
    [InlineData("P:1 A:1", "P", 2, PageLevelProblem.FirstPage, "P")] // First page
    [InlineData("P:1 Q:2 A:3 F:3", "A", 1, PageLevelProblem.GapAfter, "F")] // Gap after block
    [InlineData("P:1 A:1", "Z", 1, PageLevelProblem.PageNotListed, "Z")] // Not a live page of the section
    public void Change_Refusals(string pages, string page, int level, PageLevelProblem problem, string offender)
    {
        var result = PageLevels.Change(Pages(pages), Id(page), level);

        Assert.Equal(problem, result.Problem);
        Assert.Equal(Id(offender), result.Offender);
        Assert.Empty(result.Moved);
    }

    [Fact]
    public void Change_TheFirstPageAtLevel1_IsUnchanged()
    {
        Assert.True(PageLevels.Change(Pages("P:1 A:1"), Id("P"), 1).Unchanged);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Change_ALevelOutside1To3_IsLevelOutOfRange(int level)
    {
        Assert.Equal(PageLevelProblem.LevelOutOfRange, PageLevels.Change(Pages("P:1 A:1"), Id("A"), level).Problem);
    }

    [Theory]
    [InlineData("P:1 S1:2 S2:2 X:1 N:1", "P", "P:1 S1:2 S2:2 N:2 X:1", 3)] // Create under parent: after the last subpage
    [InlineData("P:1 S1:2 S2:3 N:1", "P", "P:1 S1:2 S2:3 N:2", 3)] // Nested subpages belong to the block
    [InlineData("P:1 X:1 N:1", "P", "P:1 N:2 X:1", 1)] // A parent with no subpages
    [InlineData("P:1 S:2 N:1", "S", "P:1 S:2 N:3", 2)] // A subpage as parent
    [InlineData("P:1 N:1", "P", "P:1 N:2", 1)] // Already last: only the level changes
    public void PlaceSubpage_PutsTheNewPageAfterTheParentsBlock_OneLevelDeeper(string pages, string parent, string expected, int index)
    {
        var result = PageLevels.PlaceSubpage(Pages(pages), Id(parent), Id("N"));

        Assert.Equal(PageLevelProblem.None, result.Problem);
        Assert.Equal(expected, Text(result.Pages));
        Assert.Equal(index, result.Index);
        Assert.Equal([Id("N")], result.Moved);
    }

    [Theory]
    [InlineData("P:1 S:2 T:3 N:1", "T", "N", PageLevelProblem.ParentAtDeepest, "T")]
    [InlineData("P:1 N:1", "Z", "N", PageLevelProblem.ParentNotListed, "Z")]
    [InlineData("P:1 N:1", "P", "Z", PageLevelProblem.NewPageNotListed, "Z")]
    [InlineData("P:1 N:1", "N", "N", PageLevelProblem.ParentNotListed, "N")]
    public void PlaceSubpage_Refusals(string pages, string parent, string page, PageLevelProblem problem, string offender)
    {
        var result = PageLevels.PlaceSubpage(Pages(pages), Id(parent), Id(page));

        Assert.Equal(problem, result.Problem);
        Assert.Equal(Id(offender), result.Offender);
    }

    [Fact]
    public void ParentProblem_RefusesAParentAtLevel3OrNotListed_AcceptsTheRest()
    {
        var pages = Pages("P:1 S:2 T:3");

        Assert.Equal(PageLevelProblem.None, PageLevels.ParentProblem(pages, Id("S")));
        Assert.Equal(PageLevelProblem.ParentAtDeepest, PageLevels.ParentProblem(pages, Id("T")));
        Assert.Equal(PageLevelProblem.ParentNotListed, PageLevels.ParentProblem(pages, Id("Z")));
    }

    [Fact]
    public void ThePlacedXml_PassesTheXsd()
    {
        const string section = "{A1000000-0000-4000-8000-000000000001}{1}{B0}";
        var pages = new[]
        {
            new HierarchyMember(HierarchyKind.Page, "{A1000000-0000-4000-8000-000000000001}{1}{E11}", "P", 1),
            new HierarchyMember(HierarchyKind.Page, "{A1000000-0000-4000-8000-000000000001}{1}{E12}", "S", 2),
            new HierarchyMember(HierarchyKind.Page, "{A1000000-0000-4000-8000-000000000001}{1}{E13}", "N", 1),
        };
        var result = PageLevels.PlaceSubpage(pages, pages[0].Id, pages[2].Id);

        Assert.Empty(SchemaValidation.Problems(HierarchyUpdate.Reorder(HierarchyKind.Section, section, result.Pages)));
    }

    [Fact]
    public void ALevel4Page_FailsTheXsd()
    {
        // The AD-16 gate refuses our own level-4 XML (the 2013 XSD's pageLevel is 1 to 3).
        const string section = "{A1000000-0000-4000-8000-000000000001}{1}{B0}";
        var pages = new[] { new HierarchyMember(HierarchyKind.Page, "{A1000000-0000-4000-8000-000000000001}{1}{E11}", "P", 4) };

        Assert.NotEmpty(SchemaValidation.Problems(HierarchyUpdate.Reorder(HierarchyKind.Section, section, pages)));
    }

    private static string Id(string name) => "id-" + name;

    private static List<HierarchyMember> Pages(string text) =>
        [.. text.Split(' ').Select(p => p.Split(':')).Select(p => new HierarchyMember(HierarchyKind.Page, Id(p[0]), p[0], int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)))];

    private static string Text(IReadOnlyList<HierarchyMember> pages) =>
        string.Join(' ', pages.Select(p => $"{p.Name}:{p.PageLevel}"));
}
