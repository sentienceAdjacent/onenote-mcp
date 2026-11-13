using System.Xml.Linq;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 2.9: <see cref="PageWriteCheck"/> (AD-7 step 4, AD-17's step-5 targets, the read-back comparison) and the
/// content-free schema gate (<see cref="SchemaValidation.Problems"/>, Q3), over the synthetic page
/// <c>synthetic/update-page-Basic.xml</c>: a title OE (10), Outline A (20) with an OE (21) and its nested OE (22), a
/// table OE (23) whose cells hold OEs 27 and 29, and an image OE (30); and Outline B (40) with OEs 41 and 42.
/// </summary>
public sealed class PageWriteCheckTests
{
    internal const string PageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000001}";
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    internal static string Read => FakeGateway.ReadFixture("synthetic/update-page-Basic.xml");

    internal static string G(int n) => $"{{5E900000-0000-4000-8000-000000000001}}{{{n}}}{{B0}}";

    /// <summary>A partial page holding <paramref name="body"/>, with <paramref name="pageId"/> (this page by default).</summary>
    internal static string Sent(string body, string? pageId = null) =>
        $"<?xml version=\"1.0\"?><one:Page xmlns:one=\"{OneNoteXml.Namespace2013}\" ID=\"{pageId ?? PageId}\">{body}</one:Page>";

    /// <summary>An element of the read page, by objectID, as a detached copy that keeps its prefix.</summary>
    internal static XElement Element(int n) => new(XDocument.Parse(Read).Descendants().Single(e => (string?)e.Attribute("objectID") == G(n)));

    internal static string Text(XElement element) =>
        element.ToString(SaveOptions.DisableFormatting).Replace(" xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"", string.Empty, StringComparison.Ordinal);

    /// <summary>Outline <paramref name="n"/> as read, changed by <paramref name="edit"/>.</summary>
    internal static string OutlineAsRead(int n, Action<XElement>? edit = null)
    {
        var outline = Element(n);
        edit?.Invoke(outline);
        return Text(outline);
    }

    internal static void Drop(XElement root, int n) => root.Descendants().Single(e => (string?)e.Attribute("objectID") == G(n)).Remove();

    private static PageWriteValidation Check(string body, params int[] remove) =>
        PageWriteCheck.Validate(Sent(body), PageId, Read, [.. remove.Select(G)]);

    // --- Step 4 ------------------------------------------------------------------------------------------------------

    [Fact]
    public void WholeOutlineAsRead_IsValid_AndItsHashIsTheTarget()
    {
        var result = Check(OutlineAsRead(20));

        Assert.Null(result.Problem);
        Assert.Equal([G(20)], result.HashTargets);
    }

    [Fact]
    public void TitleOe_IsATarget_AfterTheOutlines()
    {
        var result = Check(Text(new XElement(One + "Title", Element(10))) + OutlineAsRead(40));

        Assert.Null(result.Problem);
        Assert.Equal([G(40), G(10)], result.HashTargets);
    }

    [Fact]
    public void NewOutlineWithoutIds_IsValid_WithNoTargets()
    {
        var result = Check("<one:Outline><one:OEChildren><one:OE><one:T>new</one:T></one:OE></one:OEChildren></one:Outline>");

        Assert.Null(result.Problem);
        Assert.Empty(result.HashTargets);
    }

    [Theory]
    [InlineData(22)] // nested
    [InlineData(27)] // in a table cell
    [InlineData(30)] // the image OE
    public void AnOeLeftOut_IsOeOmitted(int omitted)
    {
        // Replaced by a new OE, so the list stays schema-valid (an OEChildren can't be empty).
        var result = Check(OutlineAsRead(20, o => o.Descendants().Single(e => (string?)e.Attribute("objectID") == G(omitted))
            .ReplaceWith(new XElement(One + "OE", new XElement(One + "T", "new")))));

        Assert.Equal(PageWriteProblemKind.OeOmitted, result.Problem!.Kind);
        Assert.Equal([G(omitted)], result.Problem.ObjectIds);
    }

    [Fact]
    public void AnOeLeftOut_AndNamedForRemoval_IsValid()
    {
        Assert.Null(Check(OutlineAsRead(40, o => Drop(o, 42)), 42).Problem);
    }

    [Fact]
    public void RemovingAnOe_RemovesItsNestedOesToo()
    {
        Assert.Null(Check(OutlineAsRead(20, o => Drop(o, 21)), 21).Problem);
    }

    [Fact]
    public void RemovingTheNestedOeOnly_StillNeedsItsParent()
    {
        var result = Check(OutlineAsRead(20, o => Drop(o, 21)), 22);

        Assert.Equal(PageWriteProblemKind.OeOmitted, result.Problem!.Kind);
        Assert.Equal([G(21)], result.Problem.ObjectIds);
    }

    [Theory]
    [InlineData(41)] // an OE of an Outline this call doesn't send
    [InlineData(20)] // an Outline, not an OE
    [InlineData(99)] // not on the page
    public void ARemovalThatIsntAnOeUnderASentOutline_IsRemovalNotAnOe(int removal)
    {
        var result = Check(OutlineAsRead(20), removal);

        Assert.Equal(PageWriteProblemKind.RemovalNotAnOe, result.Problem!.Kind);
        Assert.Equal([G(removal)], result.Problem.ObjectIds);
    }

    [Fact]
    public void ARemovalThatIsAlsoSent_IsRemovalAlsoSent()
    {
        var result = Check(OutlineAsRead(40), 41);

        Assert.Equal(PageWriteProblemKind.RemovalAlsoSent, result.Problem!.Kind);
        Assert.Equal([G(41)], result.Problem.ObjectIds);
    }

    [Fact]
    public void AnUnknownObjectId_IsUnknownObjectId()
    {
        var result = Check(OutlineAsRead(40, o => o.Descendants(One + "OE").First().SetAttributeValue("objectID", G(77))));

        Assert.Equal(PageWriteProblemKind.UnknownObjectId, result.Problem!.Kind);
        Assert.Equal([G(77)], result.Problem.ObjectIds);
    }

    [Fact]
    public void AnObjectIdOnAnotherKindOfElement_IsWrongElement()
    {
        var result = Check($"<one:Outline objectID=\"{G(41)}\"><one:OEChildren><one:OE><one:T>x</one:T></one:OE></one:OEChildren></one:Outline>");

        Assert.Equal(PageWriteProblemKind.WrongElement, result.Problem!.Kind);
    }

    [Theory]
    [InlineData("{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000002}")]
    public void ARootIdThatIsntThePage_IsPageIdMismatch(string otherPage)
    {
        var result = PageWriteCheck.Validate(Sent(OutlineAsRead(40), otherPage), PageId, Read, []);

        Assert.Equal(PageWriteProblemKind.PageIdMismatch, result.Problem!.Kind);
    }

    [Fact]
    public void ARootThatIsntAPage_IsPageIdMismatch()
    {
        var section = $"<one:Section xmlns:one=\"{OneNoteXml.Namespace2013}\" name=\"S\" ID=\"{PageId}\" />";

        Assert.Equal(PageWriteProblemKind.PageIdMismatch, PageWriteCheck.Validate(section, PageId, Read, []).Problem!.Kind);
    }

    [Fact]
    public void AnOeSentUnderAnotherOutline_IsOeMoved()
    {
        // Outline A whole, plus B's first OE moved into it (Q4).
        var result = Check(OutlineAsRead(20, o => o.Element(One + "OEChildren")!.Add(Element(41))));

        Assert.Equal(PageWriteProblemKind.OeMoved, result.Problem!.Kind);
        Assert.Equal([G(41)], result.Problem.ObjectIds);
    }

    [Fact]
    public void AnExistingOeSentInANewOutline_IsOeMoved()
    {
        var result = Check(Text(new XElement(One + "Outline", new XElement(One + "OEChildren", Element(42)))));

        Assert.Equal(PageWriteProblemKind.OeMoved, result.Problem!.Kind);
        Assert.Equal([G(42)], result.Problem.ObjectIds);
    }

    [Fact]
    public void AnExistingTableSentInANewOutline_IsOeMoved()
    {
        // Not only OEs: a table (and its rows, cells and cell OEs) moved out of Outline A is a move too.
        var result = Check(Text(new XElement(One + "Outline", new XElement(One + "OEChildren", new XElement(One + "OE", Element(24))))));

        Assert.Equal(PageWriteProblemKind.OeMoved, result.Problem!.Kind);
        Assert.Contains(G(24), result.Problem.ObjectIds);
    }

    [Fact]
    public void TheTitleOeSentInAnOutline_IsOeMoved()
    {
        var result = Check(OutlineAsRead(40, o => o.Element(One + "OEChildren")!.Add(Element(10))));

        Assert.Equal(PageWriteProblemKind.OeMoved, result.Problem!.Kind);
    }

    // --- The schema gate (Q3) -------------------------------------------------------------------------------------------

    [Fact]
    public void SchemaGate_NamesLinePositionAndElement_NeverTheValue()
    {
        var result = Check("<one:Outline><one:OEChildren><one:OE objectID=\"secret-value\"><one:T>x</one:T></one:OE></one:OEChildren></one:Outline>");

        Assert.Equal(PageWriteProblemKind.InvalidXml, result.Problem!.Kind);
        var problem = Assert.Single(result.Problem.Schema!);
        Assert.Equal("OE", problem.Element);
        Assert.Equal("one:OE", problem.DisplayName);
        Assert.True(problem.Line > 0 && problem.Position > 0);
        Assert.DoesNotContain("secret", problem.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaGate_AnElementTheSchemaForbids_IsNamed()
    {
        var problems = SchemaValidation.Problems(Sent("<one:Outline><one:Bogus /></one:Outline>"));

        Assert.Contains(problems, p => p.Element is "Bogus" or "Outline");
    }

    [Fact]
    public void SchemaGate_NotWellFormed_IsOneProblemWithNoElement()
    {
        var problem = Assert.Single(SchemaValidation.Problems($"<one:Page xmlns:one=\"{OneNoteXml.Namespace2013}\"><one:Outline>"));

        Assert.Null(problem.Element);
        Assert.True(problem.Line > 0);
    }

    [Fact]
    public void SchemaGate_WrongNamespace_IsOneProblemNamingTheRoot()
    {
        var problem = Assert.Single(SchemaValidation.Problems("<one:Page xmlns:one=\"http://schemas.microsoft.com/office/onenote/2010/onenote\" />"));

        Assert.Equal("Page", problem.Element);
        Assert.False(problem.In2013);
        Assert.Equal("Page (not in the OneNote 2013 namespace)", problem.DisplayName);
    }

    [Fact]
    public void SchemaGate_TheReadPage_AndPartialUpdates_Pass()
    {
        Assert.Empty(SchemaValidation.Problems(Read));
        Assert.Empty(SchemaValidation.Problems(Sent(OutlineAsRead(20))));
        Assert.Empty(SchemaValidation.Problems(Sent("<one:Title><one:OE><one:T>t</one:T></one:OE></one:Title>")));
    }

    // --- Read-back --------------------------------------------------------------------------------------------------

    /// <summary>The read page changed by <paramref name="edit"/>: what OneNote "kept".</summary>
    internal static string After(Action<XElement> edit)
    {
        var page = XDocument.Parse(Read);
        edit(page.Root!);
        return page.ToString();
    }

    [Fact]
    public void ReadBack_ThePageAsSent_HasNoDiffs()
    {
        Assert.Empty(PageWriteCheck.CompareReadBack(Sent(OutlineAsRead(20) + Text(new XElement(One + "Title", Element(10)))), Read));
    }

    [Fact]
    public void ReadBack_ExcludedAttributes_AreSkipped()
    {
        // lastModifiedTime is bumped on every OE of a resent Outline (spike 2.2 constraint 5); an unlocked width is OneNote's own.
        var after = After(p =>
        {
            foreach (var e in p.Descendants().Where(e => e.Attribute("lastModifiedTime") is not null))
            {
                e.SetAttributeValue("lastModifiedTime", "2026-11-12T01:00:00.000Z");
            }

            p.Descendants(One + "Column").First().SetAttributeValue("width", "37.11");
        });

        Assert.Empty(PageWriteCheck.CompareReadBack(Sent(OutlineAsRead(20)), after));
    }

    [Fact]
    public void ReadBack_AnIgnoredQuickStyleIndex_IsAttributeDiffers()
    {
        var sent = Sent(OutlineAsRead(40, o => o.Descendants(One + "OE").First().SetAttributeValue("quickStyleIndex", "0")));

        var diff = Assert.Single(PageWriteCheck.CompareReadBack(sent, Read));

        Assert.Equal(new ReadBackDiff(ReadBackDiffKind.AttributeDiffers, G(41), "quickStyleIndex"), diff);
    }

    [Fact]
    public void ReadBack_AnOeRecreatedUnderANewId_IsObjectIdChanged()
    {
        var after = After(p => p.Descendants().Single(e => (string?)e.Attribute("objectID") == G(41)).SetAttributeValue("objectID", G(91)));

        var diff = Assert.Single(PageWriteCheck.CompareReadBack(Sent(OutlineAsRead(40)), after));

        Assert.Equal(new ReadBackDiff(ReadBackDiffKind.ObjectIdChanged, G(41), NewObjectId: G(91)), diff);
    }

    [Fact]
    public void ReadBack_ANewOe_ComparesOnlyItsSentAttributes_Positionally()
    {
        var sent = Sent(OutlineAsRead(40, o => o.Element(One + "OEChildren")!.Add(
            new XElement(One + "OE", new XAttribute("alignment", "right"), new XElement(One + "T", "new")))));
        var after = After(p => p.Descendants().Single(e => (string?)e.Attribute("objectID") == G(40)).Element(One + "OEChildren")!.Add(
            new XElement(One + "OE", new XAttribute("objectID", G(93)), new XAttribute("alignment", "left"), new XAttribute("quickStyleIndex", "1"), new XElement(One + "T", "new"))));

        var diff = Assert.Single(PageWriteCheck.CompareReadBack(sent, after));

        Assert.Equal(new ReadBackDiff(ReadBackDiffKind.AttributeDiffers, G(93), "alignment"), diff);
    }

    [Fact]
    public void ReadBack_AnOeCountThatDiffers_IsOneOeCountDiffersOnTheOutline()
    {
        var after = After(p => Drop(p, 42));

        var diff = Assert.Single(PageWriteCheck.CompareReadBack(Sent(OutlineAsRead(40)), after));

        Assert.Equal(new ReadBackDiff(ReadBackDiffKind.OeCountDiffers, G(40)), diff);
    }

    [Fact]
    public void ReadBack_AnImageGoneFromASentOutline_IsImageLost()
    {
        var after = After(p => p.Descendants(One + "Image").Single().ReplaceWith(new XElement(One + "T", "no image")));

        var diff = Assert.Single(PageWriteCheck.CompareReadBack(Sent(OutlineAsRead(20)), after));

        Assert.Equal(new ReadBackDiff(ReadBackDiffKind.ImageLost, G(20), SentImages: 1, ImagesAfter: 0), diff);
    }

    [Fact]
    public void ReadBack_ASentOutlineThatIsGone_IsMissing()
    {
        var after = After(p => Drop(p, 40));

        Assert.Equal([new ReadBackDiff(ReadBackDiffKind.Missing, G(40))], PageWriteCheck.CompareReadBack(Sent(OutlineAsRead(40)), after));
    }

    [Fact]
    public void ReadBack_AStyleEqualToItsQuickStyleDef_DroppedByOneNote_IsExempt()
    {
        var sent = Sent(OutlineAsRead(40, o => o.Descendants(One + "OE").First().SetAttributeValue("style", "font-family:Calibri;font-size:11.0pt;color:automatic")));

        Assert.Empty(PageWriteCheck.CompareReadBack(sent, Read));
    }

    [Theory]
    [InlineData("font-family:Calibri;font-size:12.0pt")]
    [InlineData("font-family:Arial")]
    [InlineData("font-family:Calibri;font-weight:bold")]
    public void ReadBack_AStyleThatIsntItsQuickStyleDef_DroppedByOneNote_IsAttributeDiffers(string style)
    {
        var sent = Sent(OutlineAsRead(40, o => o.Descendants(One + "OE").First().SetAttributeValue("style", style)));

        Assert.Equal([new ReadBackDiff(ReadBackDiffKind.AttributeDiffers, G(41), "style")], PageWriteCheck.CompareReadBack(sent, Read));
    }

    [Fact]
    public void ReadBack_NumbersAndBooleans_CompareByValue()
    {
        var sent = Sent(OutlineAsRead(20, o => o.Descendants(One + "Table").Single().SetAttributeValue("bordersVisible", "1")));

        Assert.Empty(PageWriteCheck.CompareReadBack(sent, Read));
    }

    /// <summary>The read page plus one new Outline <c>60</c> holding OE <c>61</c> with <paramref name="oeContent"/>: what a new Outline reads back as.</summary>
    private static string AfterWithNewOutline(params object[] oeContent) => After(p => p.Add(
        new XElement(One + "Outline", new XAttribute("objectID", G(60)), new XElement(One + "OEChildren",
            new XElement(One + "OE", new XAttribute("objectID", G(61)), oeContent)))));

    [Fact]
    public void ReadBack_ANewOutlineWhoseImageIsGone_IsImageLost_UnderItsNewId()
    {
        var sent = Sent("<one:Outline><one:OEChildren><one:OE><one:Image><one:Size width=\"10.0\" height=\"10.0\" /><one:Data>AAAA</one:Data></one:Image></one:OE></one:OEChildren></one:Outline>");
        var after = AfterWithNewOutline(new XElement(One + "T", "no image"));

        Assert.Equal([new ReadBackDiff(ReadBackDiffKind.ImageLost, G(60), SentImages: 1, ImagesAfter: 0)], PageWriteCheck.CompareReadBack(sent, after, Read));
    }

    [Fact]
    public void ReadBack_ANewOeInANewOutline_WithAnIgnoredAttribute_IsAttributeDiffers()
    {
        var sent = Sent("<one:Outline><one:OEChildren><one:OE alignment=\"right\"><one:T>new</one:T></one:OE></one:OEChildren></one:Outline>");
        var after = AfterWithNewOutline(new XAttribute("alignment", "left"), new XElement(One + "T", "new"));

        Assert.Equal([new ReadBackDiff(ReadBackDiffKind.AttributeDiffers, G(61), "alignment")], PageWriteCheck.CompareReadBack(sent, after, Read));
    }

    [Fact]
    public void ReadBack_NewOutlines_AreComparedOnlyWithStep3sRead_AndOnlyTheFirstPairs()
    {
        var sent = Sent(
            "<one:Outline><one:OEChildren><one:OE alignment=\"right\"><one:T>new</one:T></one:OE></one:OEChildren></one:Outline>"
            + "<one:Outline><one:OEChildren><one:OE alignment=\"right\"><one:T>second</one:T></one:OE></one:OEChildren></one:Outline>");
        var after = AfterWithNewOutline(new XAttribute("alignment", "left"), new XElement(One + "T", "new"));

        Assert.Empty(PageWriteCheck.CompareReadBack(sent, after));
        Assert.Single(PageWriteCheck.CompareReadBack(sent, after, Read));
    }

    [Fact]
    public void ReadBack_TheTitleOe_IsComparedById()
    {
        var title = new XElement(One + "Title", Element(10));
        title.Element(One + "OE")!.SetAttributeValue("alignment", "center");

        Assert.Equal(
            [new ReadBackDiff(ReadBackDiffKind.AttributeDiffers, G(10), "alignment")],
            PageWriteCheck.CompareReadBack(Sent(Text(title)), Read));
    }
}
