using System.Xml.Linq;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 5.4's Xml half: <see cref="HierarchyTarget.Read"/> against captured 2013-schema fixtures and synthetic XML, and
/// <see cref="HierarchyUpdate"/>'s minimal rename and reorder XML, which must pass the vendored XSD (the AD-16 hard gate for
/// <c>UpdateHierarchy</c>), while a section group listed before a section must not.
/// </summary>
public sealed class HierarchyUpdateXmlTests
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";
    private const string NotebookId = "{A0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string SectionId = "{A1000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string OtherSectionId = "{A2000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string GroupId = "{A3000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string InnerGroupId = "{A5000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string PageId = "{A1000000-0000-4000-8000-000000000001}{1}{E11}";
    private const string SubpageId = "{A1000000-0000-4000-8000-000000000001}{1}{E12}";

    private static readonly XNamespace One = OneNoteXml.One;

    [Fact]
    public void Read_ACapturedSectionsPages_ListsEveryPage_WithItsLevel()
    {
        var target = HierarchyTarget.Read(FakeGateway.ReadFixture("mcp-test/hierarchy/section-Pages.xml"));

        Assert.Equal(HierarchyKind.Section, target.Kind);
        Assert.Equal(5, target.Children.Count);
        Assert.All(target.Children, c => Assert.Equal(HierarchyKind.Page, c.Kind));
        Assert.Equal([1, 1, 1, 1, 2], target.Children.Select(c => c.PageLevel));
        Assert.False(target.IsRecycleBin);
        Assert.False(target.InRecycleBin);
    }

    [Fact]
    public void Read_ACapturedNotebooksChildren_LeavesOutTheRecycleBin()
    {
        var target = HierarchyTarget.Read(FakeGateway.ReadFixture("mcp-test/hierarchy/notebook-Children.xml"));

        Assert.Equal(HierarchyKind.Notebook, target.Kind);
        Assert.Equal("MCP Test", target.DisplayName);
        Assert.Equal(2, target.Children.Count(c => c.Kind == HierarchyKind.Section));
        Assert.DoesNotContain(target.Children, c => c.Name == "OneNote_RecycleBin");
        Assert.NotNull(target.LastModifiedTime);
    }

    [Fact]
    public void Read_KeepsTheAttributesAWriteChecks()
    {
        var target = HierarchyTarget.Read(
            $"<one:Notebook {Ns} name=\"A\" nickname=\"Shown\" ID=\"{NotebookId}\" path=\"C:\\n\\A\\\" lastModifiedTime=\"2026-11-13T10:00:00.000Z\">"
            + $"<one:Section name=\"S\" ID=\"{SectionId}\" />"
            + $"<one:Section name=\"Gone\" ID=\"{OtherSectionId}\" isInRecycleBin=\"true\" />"
            + $"<one:SectionGroup name=\"G\" ID=\"{GroupId}\" />"
            + "<one:SectionGroup name=\"OneNote_RecycleBin\" ID=\"{A4000000-0000-4000-8000-000000000001}{1}{B0}\" isRecycleBin=\"true\" />"
            + "</one:Notebook>");

        Assert.Equal("A", target.Name);
        Assert.Equal("Shown", target.Nickname);
        Assert.Equal("Shown", target.DisplayName);
        Assert.Equal("2026-11-13T10:00:00.000Z", target.LastModifiedTime);
        Assert.Equal([SectionId, GroupId], target.Children.Select(c => c.Id));
        Assert.All(target.Children, c => Assert.Equal(0, c.PageLevel));

        var locked = HierarchyTarget.Read($"<one:Section {Ns} name=\"V\" ID=\"{SectionId}\" encrypted=\"true\" locked=\"true\" />");
        Assert.True(locked.EncryptedMarker);
        Assert.True(locked.Locked);
        Assert.Null(locked.Nickname);
        Assert.Null(locked.LastModifiedTime);
        Assert.Empty(locked.Children);

        var bin = HierarchyTarget.Read($"<one:SectionGroup {Ns} name=\"OneNote_RecycleBin\" ID=\"{GroupId}\" />");
        Assert.True(bin.IsRecycleBin);
        var deleted = HierarchyTarget.Read($"<one:Section {Ns} name=\"Deleted Pages\" ID=\"{SectionId}\" isDeletedPages=\"true\" />");
        Assert.True(deleted.InRecycleBin);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("2", 2)]
    [InlineData("x", 1)]
    [InlineData("0", 1)]
    public void Read_APagesLevel_DefaultsToOne(string? level, int expected)
    {
        var attribute = level is null ? string.Empty : $" pageLevel=\"{level}\"";
        var target = HierarchyTarget.Read($"<one:Section {Ns} name=\"S\" ID=\"{SectionId}\"><one:Page ID=\"{PageId}\" name=\"p\"{attribute} /></one:Section>");

        Assert.Equal(expected, Assert.Single(target.Children).PageLevel);
    }

    [Fact]
    public void Read_NotWellFormed_Throws() =>
        Assert.Throws<OneNoteXmlException>(() => HierarchyTarget.Read("<one:Section"));

    [Theory]
    [InlineData(HierarchyKind.Section, "Section", "name")]
    [InlineData(HierarchyKind.SectionGroup, "SectionGroup", "name")]
    [InlineData(HierarchyKind.Notebook, "Notebook", "nickname")]
    public void Rename_IsTheNodeWithItsIdAndOneNameAttribute_AndPassesTheXsd(HierarchyKind kind, string element, string attribute)
    {
        var xml = HierarchyUpdate.Rename(kind, SectionId, "Plans & <more>");

        var root = XElement.Parse(xml);
        Assert.Equal(One + element, root.Name);
        Assert.Equal(["ID", attribute], root.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name.LocalName));
        Assert.Equal("Plans & <more>", (string?)root.Attribute(attribute));
        Assert.False(root.HasElements);
        Assert.StartsWith("<?xml version=\"1.0\"?><one:" + element + " ", xml, StringComparison.Ordinal);
        Assert.Empty(SchemaValidation.Problems(xml));
    }

    [Fact]
    public void Rename_AnythingButANotebookGroupSectionOrPage_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HierarchyUpdate.Rename(HierarchyKind.Other, SectionId, "x"));

    [Fact]
    public void Reorder_IsTheParentWithIdOnlyChildren_InOrder_AndPassesTheXsd()
    {
        var xml = HierarchyUpdate.Reorder(HierarchyKind.SectionGroup, GroupId, [
            new HierarchyMember(HierarchyKind.Section, OtherSectionId, "b", 0),
            new HierarchyMember(HierarchyKind.Section, SectionId, "a", 0),
            new HierarchyMember(HierarchyKind.SectionGroup, InnerGroupId, "g", 0),
        ]);

        var root = XElement.Parse(xml);
        Assert.Equal(One + "SectionGroup", root.Name);
        Assert.Equal([OtherSectionId, SectionId, InnerGroupId], root.Elements().Select(e => (string?)e.Attribute("ID")));
        Assert.All(root.Elements(), e => Assert.Single(e.Attributes()));
        Assert.DoesNotContain("name=", xml, StringComparison.Ordinal);
        Assert.Empty(SchemaValidation.Problems(xml));
    }

    [Fact]
    public void ReorderPages_ResendsEachPagesOwnLevel_AndPassesTheXsd()
    {
        // Sitting 2026-11-15 (L6): a reorder without pageLevel made OneNote flatten every subpage to level 1.
        var xml = HierarchyUpdate.Reorder(HierarchyKind.Section, SectionId, [
            new HierarchyMember(HierarchyKind.Page, PageId, "p", 1),
            new HierarchyMember(HierarchyKind.Page, SubpageId, "s", 2),
        ]);

        var pages = XElement.Parse(xml).Elements(One + "Page").ToList();
        Assert.Equal(["1", "2"], pages.Select(p => (string?)p.Attribute("pageLevel")));
        Assert.Empty(SchemaValidation.Problems(xml));
    }

    [Theory]
    [InlineData(0, new[] { "moved", "p", "s", "q" })]
    [InlineData(2, new[] { "p", "s", "moved", "q" })]
    [InlineData(3, new[] { "p", "s", "q", "moved" })]
    public void MovePage_IsTheTargetSection_EveryPageWithItsLevel_TheMovedPageAtLevel1(int index, string[] order)
    {
        // Story 3.4 (spike 3.1 M1 shape a): one call with the target's XML; levels resent so subpages stay subpages (5.4 L6).
        const string Q = "{A1000000-0000-4000-8000-000000000001}{1}{E31}";
        const string Moved = "{A2000000-0000-4000-8000-000000000001}{1}{E11}";
        var ids = new Dictionary<string, string> { ["p"] = PageId, ["s"] = SubpageId, ["q"] = Q, ["moved"] = Moved };
        var xml = HierarchyUpdate.MovePage(SectionId, [
            new HierarchyMember(HierarchyKind.Page, PageId, "p", 1),
            new HierarchyMember(HierarchyKind.Page, SubpageId, "s", 2),
            new HierarchyMember(HierarchyKind.Page, Q, "q", 1),
        ], Moved, index);

        var root = XElement.Parse(xml);
        Assert.Equal(One + "Section", root.Name);
        Assert.Equal(SectionId, (string?)root.Attribute("ID"));
        var pages = root.Elements(One + "Page").ToList();
        Assert.Equal(order.Select(o => ids[o]), pages.Select(p => (string?)p.Attribute("ID")));
        var levels = order.Select(o => o switch { "s" => "2", _ => "1" });
        Assert.Equal(levels, pages.Select(p => (string?)p.Attribute("pageLevel")));
        Assert.All(pages, p => Assert.Equal(["ID", "pageLevel"], p.Attributes().Select(a => a.Name.LocalName)));
        Assert.DoesNotContain("name=", xml, StringComparison.Ordinal);
        Assert.Empty(SchemaValidation.Problems(xml));
    }

    [Fact]
    public void MovePage_IntoAnEmptySection_IsTheOnePage()
    {
        const string Moved = "{A2000000-0000-4000-8000-000000000001}{1}{E11}";

        var xml = HierarchyUpdate.MovePage(SectionId, [], Moved, 0);

        var page = Assert.Single(XElement.Parse(xml).Elements(One + "Page"));
        Assert.Equal(Moved, (string?)page.Attribute("ID"));
        Assert.Equal("1", (string?)page.Attribute("pageLevel"));
        Assert.Empty(SchemaValidation.Problems(xml));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void MovePage_AnIndexOutsideTheList_Throws(int index) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HierarchyUpdate.MovePage(SectionId, [new HierarchyMember(HierarchyKind.Page, PageId, "p", 1)], SubpageId, index));

    [Theory]
    [InlineData(0, new[] { "moved", "a", "b" })]
    [InlineData(1, new[] { "a", "moved", "b" })]
    [InlineData(2, new[] { "a", "b", "moved" })]
    public void MoveContainer_ASection_IsTheNewParentWithItsSectionsAndTheMovedOneAtTheIndex(int index, string[] order)
    {
        // Story 3.12 (spike 3.1 m3, with the same-kind siblings listed for the position, 3.12 Q1): ID-only elements.
        const string Moved = "{A9000000-0000-4000-8000-000000000001}{1}{B0}";
        var ids = new Dictionary<string, string> { ["a"] = SectionId, ["b"] = OtherSectionId, ["moved"] = Moved };
        var xml = HierarchyUpdate.MoveContainer(HierarchyKind.SectionGroup, GroupId, [
            new HierarchyMember(HierarchyKind.Section, SectionId, "a", 0),
            new HierarchyMember(HierarchyKind.Section, OtherSectionId, "b", 0),
        ], HierarchyKind.Section, Moved, index);

        var root = XElement.Parse(xml);
        Assert.Equal(One + "SectionGroup", root.Name);
        Assert.Equal(GroupId, (string?)root.Attribute("ID"));
        var sections = root.Elements().ToList();
        Assert.All(sections, s => Assert.Equal(One + "Section", s.Name));
        Assert.Equal(order.Select(o => ids[o]), sections.Select(s => (string?)s.Attribute("ID")));
        Assert.All(sections, s => Assert.Equal(["ID"], s.Attributes().Select(a => a.Name.LocalName)));
        Assert.Empty(SchemaValidation.Problems(xml));
    }

    [Fact]
    public void MoveContainer_AGroupIntoANotebook_ListsOnlyGroups_AndPassesTheXsd()
    {
        var xml = HierarchyUpdate.MoveContainer(HierarchyKind.Notebook, NotebookId, [
            new HierarchyMember(HierarchyKind.SectionGroup, InnerGroupId, "g", 0),
        ], HierarchyKind.SectionGroup, GroupId, 1);

        var root = XElement.Parse(xml);
        Assert.Equal(One + "Notebook", root.Name);
        Assert.Equal([InnerGroupId, GroupId], root.Elements(One + "SectionGroup").Select(g => (string?)g.Attribute("ID")));
        Assert.Empty(root.Elements(One + "Section"));
        Assert.Empty(SchemaValidation.Problems(xml));
    }

    [Fact]
    public void MoveContainer_IntoAnEmptyParent_IsTheOneNode()
    {
        var xml = HierarchyUpdate.MoveContainer(HierarchyKind.SectionGroup, GroupId, [], HierarchyKind.Section, SectionId, 0);

        var only = Assert.Single(XElement.Parse(xml).Elements());
        Assert.Equal(SectionId, (string?)only.Attribute("ID"));
        Assert.Empty(SchemaValidation.Problems(xml));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void MoveContainer_AnIndexOutsideTheList_Throws(int index) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => HierarchyUpdate.MoveContainer(
            HierarchyKind.Notebook, NotebookId, [new HierarchyMember(HierarchyKind.Section, SectionId, "s", 0)], HierarchyKind.Section, OtherSectionId, index));

    [Fact]
    public void MoveContainer_ASiblingOfAnotherKind_Throws() =>
        Assert.Throws<ArgumentException>(() => HierarchyUpdate.MoveContainer(
            HierarchyKind.Notebook, NotebookId, [new HierarchyMember(HierarchyKind.SectionGroup, GroupId, "g", 0)], HierarchyKind.Section, SectionId, 0));

    [Fact]
    public void AGroupBeforeASection_FailsTheXsd_SoTheGateWouldCatchIt()
    {
        // The 2013 schema's order (spike 5.1 constraint 4: OneNote answered 0x80042001 and applied nothing).
        var xml = HierarchyUpdate.Reorder(HierarchyKind.Notebook, NotebookId, [
            new HierarchyMember(HierarchyKind.SectionGroup, GroupId, "g", 0),
            new HierarchyMember(HierarchyKind.Section, SectionId, "s", 0),
        ]);

        Assert.NotEmpty(SchemaValidation.Problems(xml));
    }
}
