using OneNoteMcp.Core.Access;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Where a section or section-group move puts sections (story 3.12; AD-7 Moves, AD-21): the locations a move is checked
/// against before it can put a section on a key that holds a read grant, as a rename is (story 5.4).
/// </summary>
public sealed class MovedLocationsTests
{
    [Theory]
    [InlineData(@"C:\notebooks\A\Ideas.one", @"C:\notebooks\A\Archive", @"C:\notebooks\A\Archive\Ideas.one")]
    [InlineData(@"C:\notebooks\A\Archive\Ideas.one", @"C:\notebooks\A\", @"C:\notebooks\A\Ideas.one")]
    [InlineData(@"C:\notebooks\A\Ideas.one", @"C:\notebooks\Other", @"C:\notebooks\Other\Ideas.one")]
    [InlineData(@"C:\notebooks\A\Ideas.one", "https://d.docs.live.net/0123abcd/Cloud/", "https://d.docs.live.net/0123abcd/Cloud/Ideas.one")]
    [InlineData("https://d.docs.live.net/0123abcd/Cloud/Ideas.one", @"C:\notebooks\A\", @"C:\notebooks\A\Ideas.one")]
    public void ASection_LandsInTheNewParentsFolder_UnderItsOwnFileName(string path, string parentPath, string expected) =>
        Assert.Equal(expected, MovedLocations.Section(path, parentPath));

    [Theory]
    [InlineData("Ideas.one", @"C:\notebooks\A\")]
    [InlineData("", @"C:\notebooks\A\")]
    [InlineData(@"C:\notebooks\A\Ideas.one", "")]
    [InlineData(@"C:\notebooks\A\Ideas.one", @"\")]
    public void APathWithNoFolderOrNoParent_IsUnpredictable(string path, string parentPath) =>
        Assert.Null(MovedLocations.Section(path, parentPath));

    [Fact]
    public void ASectionGroup_TakesEverySectionBeneathIt_IntoTheNewParent_NestedOnesIncluded()
    {
        var moved = MovedLocations.SectionsInGroup(
            @"C:\notebooks\A\Archive\",
            "https://d.docs.live.net/0123abcd/Cloud/Group",
            [@"C:\notebooks\A\Archive\Plans.one", @"c:\NOTEBOOKS\a\archive\Sub\Deep.one"]);

        Assert.NotNull(moved);
        Assert.Equal(
            [
                (@"C:\notebooks\A\Archive\Plans.one", "https://d.docs.live.net/0123abcd/Cloud/Group/Archive/Plans.one"),
                (@"c:\NOTEBOOKS\a\archive\Sub\Deep.one", "https://d.docs.live.net/0123abcd/Cloud/Group/Archive/Sub/Deep.one"),
            ],
            moved);
    }

    [Fact]
    public void ASectionGroup_WithASectionOutsideItsFolder_IsUnpredictable() =>
        Assert.Null(MovedLocations.SectionsInGroup(@"C:\notebooks\A\Archive", @"C:\notebooks\Other", [@"C:\notebooks\A\Elsewhere\Plans.one"]));

    [Fact]
    public void AnEmptySectionGroup_MovesNoSection() =>
        Assert.Equal([], MovedLocations.SectionsInGroup(@"C:\notebooks\A\Archive", @"C:\notebooks\Other", [])!);
}
