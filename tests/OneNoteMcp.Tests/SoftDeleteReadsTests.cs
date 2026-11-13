using System.Xml.Linq;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 5.5's hierarchy reads, against hand-written XML in OneNote's 2013 shape: the recycle bin and its Deleted Pages
/// (<see cref="RecycleBin"/>), the pick of a soft-deleted page's new ID (Q3), a delete target's root, a page's subpages
/// (Q2), and a section group's snapshot without protected sections' pages (AD-8).
/// </summary>
public sealed class SoftDeleteReadsTests
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";

    [Fact]
    public void BinId_IsTheRecycleBinGroupDirectlyUnderTheNotebook_OrNullBeforeTheFirstDelete()
    {
        var with = $$"""
            <one:Notebook {{Ns}} name="A" ID="{NB}">
              <one:Section name="S" ID="{S}" />
              <one:SectionGroup name="G" ID="{G}" />
              <one:SectionGroup name="OneNote_RecycleBin" ID="{RB}" isRecycleBin="true" />
            </one:Notebook>
            """;
        var byName = $$"""<one:Notebook {{Ns}} name="A" ID="{NB}"><one:SectionGroup name="OneNote_RecycleBin" ID="{RB2}" /></one:Notebook>""";
        var without = $$"""<one:Notebook {{Ns}} name="A" ID="{NB}"><one:Section name="S" ID="{S}" /></one:Notebook>""";

        Assert.Equal("{RB}", RecycleBin.BinId(with));
        Assert.Equal("{RB2}", RecycleBin.BinId(byName));
        Assert.Null(RecycleBin.BinId(without));
    }

    [Fact]
    public void DeletedPages_ListsOnlyTheDeletedPagesSections_NotARecycledSectionsPages()
    {
        var bin = $$"""
            <one:SectionGroup {{Ns}} name="OneNote_RecycleBin" ID="{RB}" isRecycleBin="true">
              <one:Section name="Deleted Pages" ID="{DP}" isInRecycleBin="true" isDeletedPages="true">
                <one:Page ID="{P1}" name="Trip" isInRecycleBin="true" />
                <one:Page ID="{P2}" name="Notes" isInRecycleBin="true" />
              </one:Section>
              <one:Section name="Old" ID="{OLD}" isInRecycleBin="true">
                <one:Page ID="{P3}" name="Kept" />
              </one:Section>
            </one:SectionGroup>
            """;

        Assert.Equal([new DeletedPage("{P1}", "Trip"), new DeletedPage("{P2}", "Notes")], RecycleBin.DeletedPages(bin));
    }

    [Fact]
    public void PickNewPage_OneNewId_IsIt_WhateverItsName()
    {
        DeletedPage[] before = [new("{A}", "Trip")];
        DeletedPage[] after = [new("{A}", "Trip"), new("{B}", "Renamed")];

        Assert.Equal("{B}", RecycleBin.PickNewPage(before, after, "Trip"));
    }

    [Fact]
    public void PickNewPage_SeveralNew_AreNarrowedByName_ToExactlyOne()
    {
        DeletedPage[] before = [new("{A}", "Trip"), new("{B}", "Trip")];
        DeletedPage[] after = [new("{A}", "Trip"), new("{B}", "Trip"), new("{C}", "Other"), new("{D}", "Trip")];

        Assert.Equal("{D}", RecycleBin.PickNewPage(before, after, "Trip"));
    }

    [Fact]
    public void PickNewPage_NoneNew_OrSeveralWithTheName_OrNoneWithIt_IsNull()
    {
        DeletedPage[] before = [new("{A}", "Trip")];

        Assert.Null(RecycleBin.PickNewPage(before, before, "Trip"));
        Assert.Null(RecycleBin.PickNewPage(before, [.. before, new("{B}", "Trip"), new("{C}", "Trip")], "Trip"));
        Assert.Null(RecycleBin.PickNewPage(before, [.. before, new("{B}", "x"), new("{C}", "y")], "Trip"));
        Assert.Null(RecycleBin.PickNewPage(before, [.. before, new("{B}", "trip"), new("{C}", "TRIP")], "Trip")); // names compare ordinal
    }

    [Fact]
    public void PickNewPage_FirstDelete_IntoANewRecycleBin_IsTheOnlyPage()
    {
        Assert.Equal("{A}", RecycleBin.PickNewPage([], [new("{A}", "Trip")], "Trip"));
    }

    [Fact]
    public void Root_ReadsKindDateAndTheRecycleBinStates()
    {
        var section = DeleteTargets.Root($$"""<one:Section {{Ns}} name="Vault" ID="{S}" path="C:\n\Vault.one" lastModifiedTime="2026-11-13T10:00:00.000Z" encrypted="true" locked="true" />""");
        var bin = DeleteTargets.Root($$"""<one:SectionGroup {{Ns}} name="OneNote_RecycleBin" ID="{RB}" />""");
        var deletedPages = DeleteTargets.Root($$"""<one:Section {{Ns}} name="Deleted Pages" ID="{DP}" isDeletedPages="true" />""");

        Assert.Equal(new DeleteTarget(HierarchyKind.Section, "{S}", "Vault", @"C:\n\Vault.one", "2026-11-13T10:00:00.000Z", false, false, true, true), section);
        Assert.True(bin.IsRecycleBin);
        Assert.True(deletedPages.InRecycleBin);
    }

    [Fact]
    public void Page_CountsTheSubpagesThatFollowAtADeeperLevel()
    {
        var pages = $$"""
            <one:Section {{Ns}} name="S" ID="{S}">
              <one:Page ID="{P1}" name="Top" lastModifiedTime="2026-11-13T10:00:01.000Z" pageLevel="1" />
              <one:Page ID="{P2}" name="Child" pageLevel="2" />
              <one:Page ID="{P3}" name="Grandchild" pageLevel="3" />
              <one:Page ID="{P4}" name="Next" lastModifiedTime="2026-11-13T10:00:04.000Z" pageLevel="1" />
              <one:Page ID="{P5}" name="Last" />
            </one:Section>
            """;

        Assert.Equal(new SectionPage("Top", "2026-11-13T10:00:01.000Z", 1, 2), DeleteTargets.Page(pages, "{P1}"));
        Assert.Equal(1, DeleteTargets.Page(pages, "{P2}")!.Subpages);
        Assert.Equal(new SectionPage("Next", "2026-11-13T10:00:04.000Z", 1, 0), DeleteTargets.Page(pages, "{P4}"));
        Assert.Equal(0, DeleteTargets.Page(pages, "{P5}")!.Subpages);
        Assert.Null(DeleteTargets.Page(pages, "{P9}"));
    }

    [Fact]
    public void WithoutProtectedPages_DropsOnlyProtectedSectionsPages_KeepingTheirSections()
    {
        var group = $$"""
            <?xml version="1.0"?>
            <one:SectionGroup {{Ns}} name="G" ID="{G}">
              <one:Section name="Open" ID="{S1}"><one:Page ID="{P1}" name="Visible" /></one:Section>
              <one:Section name="Vault" ID="{S2}" encrypted="true"><one:Page ID="{P2}" name="Secret" /></one:Section>
              <one:SectionGroup name="Inner" ID="{G2}">
                <one:Section name="Vault 2" ID="{S3}" encrypted="true"><one:Page ID="{P3}" name="Secret 2" /></one:Section>
              </one:SectionGroup>
            </one:SectionGroup>
            """;

        var kept = XDocument.Parse(DeleteTargets.WithoutProtectedPages(group)).Root!;

        Assert.Equal(["{S1}", "{S2}", "{S3}"], kept.Descendants(OneNoteXml.One + "Section").Select(s => (string?)s.Attribute("ID")));
        Assert.Equal(["{P1}"], kept.Descendants(OneNoteXml.One + "Page").Select(p => (string?)p.Attribute("ID")));
        Assert.DoesNotContain("Secret", DeleteTargets.WithoutProtectedPages(group), StringComparison.Ordinal);
        Assert.Equal(["{P1}", "{P2}", "{P3}"], DeleteTargets.PageIds(group));
    }

    [Fact]
    public void WithoutProtectedPages_NothingToDrop_ReturnsTheTextUnchanged()
    {
        var group = $$"""<one:SectionGroup {{Ns}} name="G" ID="{G}"><one:Section name="Open" ID="{S1}"><one:Page ID="{P1}" name="Visible" /></one:Section></one:SectionGroup>""";

        Assert.Same(group, DeleteTargets.WithoutProtectedPages(group));
    }
}
