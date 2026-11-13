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
        Assert.True(locked.Encrypted);
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
    public void ReorderPages_SendsNoPageLevel_AndPassesTheXsd()
    {
        var xml = HierarchyUpdate.Reorder(HierarchyKind.Section, SectionId, [
            new HierarchyMember(HierarchyKind.Page, PageId, "p", 1),
            new HierarchyMember(HierarchyKind.Page, SubpageId, "s", 2),
        ]);

        Assert.DoesNotContain("pageLevel", xml, StringComparison.Ordinal);
        Assert.Equal(2, XElement.Parse(xml).Elements(One + "Page").Count());
        Assert.Empty(SchemaValidation.Problems(xml));
    }

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
