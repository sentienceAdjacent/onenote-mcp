using OneNoteMcp.Core.Access;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Where a rename moves sections (story 5.4 security review; AD-21): the locations a rename is checked against before it
/// can hand one section's read grant to another.
/// </summary>
public sealed class RenamedLocationsTests
{
    [Theory]
    [InlineData(@"C:\notebooks\A\Old.one", "New", @"C:\notebooks\A\New.one")]
    [InlineData(@"C:\notebooks\A\Group\Old.one", "New", @"C:\notebooks\A\Group\New.one")]
    [InlineData("https://d.docs.live.net/0123abcd/Cloud/Old.one", "New", "https://d.docs.live.net/0123abcd/Cloud/New.one")]
    [InlineData(@"C:\notebooks\A\Old.one", "old", @"C:\notebooks\A\old.one")]
    public void ASection_StaysInItsFolder_UnderItsNewName(string path, string name, string expected) =>
        Assert.Equal(expected, RenamedLocations.Section(path, name));

    [Theory]
    [InlineData("Old.one")]
    [InlineData("")]
    [InlineData(@"\Old.one")]
    public void ASectionPathWithNoFolder_IsUnpredictable(string path) =>
        Assert.Null(RenamedLocations.Section(path, "New"));

    [Fact]
    public void ASectionGroup_MovesEverySectionBeneathIt_NestedOnesIncluded()
    {
        var moved = RenamedLocations.SectionsInGroup(
            @"C:\notebooks\A\Archive\",
            "Moved",
            [@"C:\notebooks\A\Archive\Plans.one", @"c:\NOTEBOOKS\a\archive\Sub\Deep.one"]);

        Assert.NotNull(moved);
        Assert.Equal(
            [(@"C:\notebooks\A\Archive\Plans.one", @"C:\notebooks\A\Moved\Plans.one"), (@"c:\NOTEBOOKS\a\archive\Sub\Deep.one", @"C:\notebooks\A\Moved\Sub\Deep.one")],
            moved);
    }

    [Fact]
    public void ACloudSectionGroup_MovesItsSections_WithForwardSlashes()
    {
        var moved = RenamedLocations.SectionsInGroup("https://d.docs.live.net/0123abcd/Cloud/Shared", "Team", ["https://d.docs.live.net/0123abcd/Cloud/Shared/Web.one"]);

        Assert.Equal([("https://d.docs.live.net/0123abcd/Cloud/Shared/Web.one", "https://d.docs.live.net/0123abcd/Cloud/Team/Web.one")], moved);
    }

    [Fact]
    public void AnEmptySectionGroup_MovesNothing() =>
        Assert.Empty(RenamedLocations.SectionsInGroup(@"C:\notebooks\A\Archive", "Moved", [])!);

    [Theory]
    [InlineData(@"C:\notebooks\A\Elsewhere\Plans.one")] // not inside the group's folder
    [InlineData(@"C:\notebooks\A\ArchiveOld\Plans.one")] // shares the folder's name as a prefix only
    [InlineData(@"C:\notebooks\A\Archive")] // the folder itself
    [InlineData("")] // no path
    public void ASectionOutsideTheGroupsFolder_MakesTheGroupUnpredictable(string sectionPath) =>
        Assert.Null(RenamedLocations.SectionsInGroup(@"C:\notebooks\A\Archive", "Moved", [@"C:\notebooks\A\Archive\Notes.one", sectionPath]));

    [Fact]
    public void AGroupPathWithNoFolder_IsUnpredictable() =>
        Assert.Null(RenamedLocations.SectionsInGroup("Archive", "Moved", []));
}
