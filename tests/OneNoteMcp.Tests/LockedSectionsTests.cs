using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>Story 7: <see cref="LockedSections.Find"/>, the source of <c>get_hierarchy</c>'s <c>sectionLocked</c> warnings.</summary>
public sealed class LockedSectionsTests
{
    [Fact]
    public void Finds_EveryLockedSection_InDocumentOrder_IncludingInsideSectionGroups()
    {
        Assert.Equal(
            [HierarchyReadToolTests.LockedA, HierarchyReadToolTests.LockedB],
            LockedSections.Find(HierarchyReadToolTests.LockedXml));
    }

    [Fact]
    public void AcceptsTheSchemaBooleanOne_AndIgnoresFalse()
    {
        const string xml =
            "<one:Notebook xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\" ID=\"{n}\">"
            + "<one:Section ID=\"{a}\" locked=\"1\" /><one:Section ID=\"{b}\" locked=\"false\" /><one:Section ID=\"{c}\" locked=\"0\" />"
            + "</one:Notebook>";

        Assert.Equal(["{a}"], LockedSections.Find(xml));
    }

    [Fact]
    public void IgnoresLockedOnElementsOutsideThe2013Namespace()
    {
        const string xml = "<Notebook><Section ID=\"{a}\" locked=\"true\" /></Notebook>";

        Assert.Empty(LockedSections.Find(xml));
    }

    public static TheoryData<string> HierarchyFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var entry in FakeGateway.Manifest.Entries.Where(e => e.Method == "GetHierarchy" && e.File is not null))
        {
            data.Add(entry.File!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(HierarchyFixtures))]
    public void NoCapturedFixture_HasALockedSection(string file)
    {
        Assert.Empty(LockedSections.Find(FakeGateway.ReadFixture(file)));
    }

    [Fact]
    public void MalformedXml_IsOneNoteXmlException_WithoutQuotingIt()
    {
        var ex = Assert.Throws<OneNoteXmlException>(() => LockedSections.Find("<one:Notebook secret"));

        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
    }
}
