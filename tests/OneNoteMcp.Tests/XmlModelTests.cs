using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>Story 6: the vendored XSD, <see cref="SchemaValidation"/>, <see cref="ElementHash"/>/<see cref="PageBasis"/> and <see cref="ImageCount"/>, over the captured fixtures.</summary>
public sealed partial class XmlModelTests
{
    private const string WaybackSha256 = "fcddc26ac8fa925a4dac86e0b3cfc451445e2728757d4804ed1e0e1cbfb2a29d";
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    // --- XSD -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Xsd_IsTheVendoredWaybackBytes()
    {
        var embedded = SchemaValidation.XsdBytes();

        Assert.Equal(47_961, embedded.Length);
        Assert.Equal(WaybackSha256, Convert.ToHexStringLower(SHA256.HashData(embedded)));
    }

    [Fact]
    public void Xsd_OnDisk_IsUnchangedByGit()
    {
        // core.autocrlf would rewrite the CRLF endings unless .gitattributes marks *.xsd -text.
        var onDisk = File.ReadAllBytes(RepoPaths.Combine("src", "OneNoteMcp.Xml", "Schema", "OneNoteApplication_2013.xsd"));

        Assert.Equal(WaybackSha256, Convert.ToHexStringLower(SHA256.HashData(onDisk)));
    }

    public static TheoryData<string> XmlFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in FakeGateway.Manifest.Entries.Select(e => e.File).OfType<string>().Where(f => f.EndsWith(".xml", StringComparison.Ordinal)))
        {
            data.Add(file);
        }

        if (FakeGateway.Manifest.Probe is { } probe)
        {
            data.Add(probe.BeforeFile);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(XmlFixtures))]
    public void EveryXmlFixture_ValidatesAgainstTheXsd(string file)
    {
        Assert.Empty(SchemaValidation.Validate(FakeGateway.ReadFixture(file)));
    }

    [Fact]
    public void Validation_RejectsWhatTheSchemaForbids()
    {
        var page = Fixture("GetPageContent", NestedPageId(), "Basic");
        var root = XDocument.Parse(page);
        root.Root!.SetAttributeValue("notInTheSchema", "x");

        Assert.NotEmpty(SchemaValidation.Validate(root.ToString()));
    }

    [Fact]
    public void Validation_NotWellFormed_IsOneError()
    {
        var errors = SchemaValidation.Validate("<one:Page xmlns:one=\"" + OneNoteXml.Namespace2013 + "\">");

        Assert.Contains("not well-formed", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_WrongNamespace_IsOneError()
    {
        var errors = SchemaValidation.Validate("<one:Page xmlns:one=\"http://schemas.microsoft.com/office/onenote/2010/onenote\" />");

        Assert.Contains("2013 namespace", Assert.Single(errors), StringComparison.Ordinal);
    }

    // --- Partial-update XML against the XSD (AD-16; epic 2's gate depends on this) ---------------------------------

    [Fact]
    public void PartialUpdate_NewOutlineWithNoIds_Validates()
    {
        var xml = $"""
            <?xml version="1.0"?>
            <one:Page xmlns:one="{OneNoteXml.Namespace2013}" ID="{NestedPageId()}">
              <one:Outline><one:OEChildren><one:OE><one:T><![CDATA[A new outline]]></one:T></one:OE></one:OEChildren></one:Outline>
            </one:Page>
            """;

        Assert.Empty(SchemaValidation.Validate(xml));
    }

    [Fact]
    public void PartialUpdate_OneExistingOeWithChangedText_Validates()
    {
        var page = XDocument.Parse(Fixture("GetPageContent", NestedPageId(), "Basic")).Root!;
        var outline = page.Elements(One + "Outline").First();
        var oe = outline.Descendants(One + "OE").First();
        var xml = $"""
            <?xml version="1.0"?>
            <one:Page xmlns:one="{OneNoteXml.Namespace2013}" ID="{(string)page.Attribute("ID")!}">
              <one:Outline objectID="{(string)outline.Attribute("objectID")!}"><one:OEChildren>
                <one:OE objectID="{(string)oe.Attribute("objectID")!}"><one:T><![CDATA[Changed text]]></one:T></one:OE>
              </one:OEChildren></one:Outline>
            </one:Page>
            """;

        Assert.Empty(SchemaValidation.Validate(xml));
    }

    [Fact]
    public void PartialUpdate_PageWithTitleOnly_Validates()
    {
        var xml = $"""
            <?xml version="1.0"?>
            <one:Page xmlns:one="{OneNoteXml.Namespace2013}" ID="{NestedPageId()}">
              <one:Title><one:OE><one:T><![CDATA[A new title]]></one:T></one:OE></one:Title>
            </one:Page>
            """;

        Assert.Empty(SchemaValidation.Validate(xml));
    }

    [Fact]
    public void PartialUpdate_PlaceholderObjectId_FailsThePattern()
    {
        // The XSD's ObjectID pattern is what makes hand-built IDs fail (AD-4: IDs are opaque, never built).
        var xml = $"""<one:Page xmlns:one="{OneNoteXml.Namespace2013}" ID="{NestedPageId()}"><one:Outline objectID="new-outline"><one:OEChildren><one:OE><one:T>x</one:T></one:OE></one:OEChildren></one:Outline></one:Page>""";

        Assert.NotEmpty(SchemaValidation.Validate(xml));
    }

    // --- ElementHash / PageBasis: the I/O matrix --------------------------------------------------------------------

    [Fact]
    public void Hash_IsLowercaseHexSha256()
    {
        Assert.Matches("^[0-9a-f]{64}$", PageBasis.From(Fixture("GetPageContent", NestedPageId(), "Basic")).PageHash);
    }

    [Fact]
    public void ExcludedAttributes_AreAd17sPlusPhillsDecision()
    {
        Assert.Equal(
            ["creationTime", "isCurrentlyViewed", "isUnread", "lastModifiedBy", "lastModifiedByInitials", "lastModifiedByResolutionID", "lastModifiedTime", "selected"],
            ElementHash.ExcludedAttributes.Order(StringComparer.Ordinal));
    }

    // --- AD-17's two attributes OneNote sets itself (Phill, 2026-11-10, 2.3 plan Q2; built by story 2.9) ---------------

    private const string ExclusionPage =
        "<one:Page xmlns:one=\"" + OneNoteXml.Namespace2013 + "\" ID=\"{5E900000-0000-4000-8000-000000000001}{1}{E1}\">"
        + "<one:Outline objectID=\"{5E900000-0000-4000-8000-000000000001}{20}{B0}\"><one:OEChildren>"
        + "<one:OE objectID=\"{5E900000-0000-4000-8000-000000000001}{21}{B0}\"><one:Table><one:Columns>"
        + "<one:Column index=\"0\" width=\"80.0\" /><one:Column index=\"1\" width=\"105.51\" isLocked=\"true\" />"
        + "</one:Columns><one:Row><one:Cell><one:OEChildren><one:OE><one:T>x</one:T></one:OE></one:OEChildren></one:Cell>"
        + "<one:Cell><one:OEChildren><one:OE><one:T>y</one:T></one:OE></one:OEChildren></one:Cell></one:Row></one:Table></one:OE>"
        + "<one:OE objectID=\"{5E900000-0000-4000-8000-000000000001}{22}{B0}\"><one:InsertedFile pathCache=\"C:\\cache\\a.bin\" pathSource=\"C:\\a.txt\" preferredName=\"a.txt\" /></one:OE>"
        + "</one:OEChildren></one:Outline></one:Page>";

    [Fact]
    public void InsertedFilePathCache_IsExcluded()
    {
        var changed = ExclusionPage.Replace(@"C:\cache\a.bin", @"C:\cache\b.bin", StringComparison.Ordinal);

        Assert.Equal(PageBasis.From(ExclusionPage), PageBasis.From(changed), BasisComparer.Instance);
    }

    [Fact]
    public void UnlockedColumnWidth_IsExcluded()
    {
        var changed = ExclusionPage.Replace("width=\"80.0\"", "width=\"37.11\"", StringComparison.Ordinal);

        Assert.Equal(PageBasis.From(ExclusionPage), PageBasis.From(changed), BasisComparer.Instance);
    }

    [Fact]
    public void LockedColumnWidth_StaysInTheHash()
    {
        var changed = ExclusionPage.Replace("width=\"105.51\"", "width=\"90.0\"", StringComparison.Ordinal);

        Assert.NotEqual(PageBasis.From(ExclusionPage).PageHash, PageBasis.From(changed).PageHash);
        Assert.NotEqual(
            PageBasis.From(ExclusionPage).ElementHashes["{5E900000-0000-4000-8000-000000000001}{20}{B0}"],
            PageBasis.From(changed).ElementHashes["{5E900000-0000-4000-8000-000000000001}{20}{B0}"]);
    }

    [Fact]
    public void PathCacheAndWidth_ElsewhereStayInTheHash()
    {
        // Only one:InsertedFile@pathCache and an unlocked one:Column@width are excluded, not the names on other elements.
        var root = XElement.Parse(ExclusionPage);
        var size = new XElement(One + "Size", new XAttribute("width", "10.0"), new XAttribute("height", "10.0"));
        root.Element(One + "Outline")!.AddFirst(size);
        var before = ElementHash.Compute(root);
        size.SetAttributeValue("width", "20.0");

        Assert.NotEqual(before, ElementHash.Compute(root));
        Assert.False(ElementHash.IsExcluded(new XAttribute("pathCache", "x")));
    }

    [Fact]
    public void DateOnlyChange_LeavesEveryHashEqual()
    {
        var original = Fixture("GetPageContent", NestedPageId(), "Basic");
        var changed = XDocument.Parse(original);
        foreach (var attribute in changed.Descendants().Attributes().Where(a => a.Name.LocalName is "lastModifiedTime" or "creationTime"))
        {
            attribute.Value = "2001-01-01T00:00:00.000Z";
        }

        changed.Root!.SetAttributeValue("isCurrentlyViewed", "true");
        changed.Root.SetAttributeValue("isUnread", "true");
        foreach (var element in changed.Descendants().Where(e => e.Name.LocalName is "Outline" or "OE"))
        {
            element.SetAttributeValue("lastModifiedBy", "Someone Else");
            element.SetAttributeValue("lastModifiedByInitials", "SE");
            element.SetAttributeValue("lastModifiedByResolutionID", "elsewhere");
            element.SetAttributeValue("selected", "all");
        }

        var a = PageBasis.From(original);
        var b = PageBasis.From(changed.ToString());

        Assert.NotEqual(a.LastModifiedTime, b.LastModifiedTime);
        Assert.Equal(a.PageHash, b.PageHash);
        Assert.Equal(a.ElementHashes, b.ElementHashes);
    }

    [Fact]
    public void TextChange_ChangesTheOeItsAncestorsItsOutlineAndThePage_NotItsSiblings()
    {
        var original = Fixture("GetPageContent", NestedPageId(), "Basic");
        var changed = XDocument.Parse(original);
        var level3 = changed.Descendants(One + "T").Single(t => t.Value == "Level 3").Parent!;
        level3.Element(One + "T")!.ReplaceNodes(new XCData("Level 3, changed"));

        var a = PageBasis.From(original);
        var b = PageBasis.From(changed.ToString());

        var expectedChanged = level3.AncestorsAndSelf()
            .Where(e => e.Name == One + "OE" || e.Name == One + "Outline")
            .Select(e => (string)e.Attribute("objectID")!)
            .ToHashSet();
        Assert.Equal(4, expectedChanged.Count); // the level-3 OE, its level-2 and level-1 OEs, and the Outline
        Assert.NotEqual(a.PageHash, b.PageHash);
        Assert.Equal(a.ElementHashes.Keys.Order(), b.ElementHashes.Keys.Order());
        foreach (var (id, hash) in a.ElementHashes)
        {
            if (expectedChanged.Contains(id))
            {
                Assert.NotEqual(hash, b.ElementHashes[id]);
            }
            else
            {
                Assert.Equal(hash, b.ElementHashes[id]);
            }
        }
    }

    /// <summary>
    /// Layout stays in the hash (Phill, 2026-11-05). Resizing or moving an Outline, with no text change, changes that
    /// Outline's hash and the page hash, and no OE's. So an element edit whose basis predates a resize or a move gets
    /// <c>StaleWrite</c> if it names the Outline, and goes through if it names only OEs. The test prints the before and
    /// after hashes so the consequence can be read in the output.
    /// </summary>
    [Theory]
    [InlineData("resize", "Size", "width", "300")]
    [InlineData("move", "Position", "x", "200.0")]
    public void LayoutOnlyChange_ChangesTheOutlineAndThePage_NotItsOes(string what, string element, string attribute, string value)
    {
        var original = Fixture("GetPageContent", NestedPageId(), "Basic");
        var changed = XDocument.Parse(original);
        var outline = changed.Descendants(One + "Outline").Single();
        var layout = outline.Element(One + element)!;
        var oldValue = (string)layout.Attribute(attribute)!;
        layout.SetAttributeValue(attribute, value);

        var before = PageBasis.From(original);
        var after = PageBasis.From(changed.ToString());
        var outlineId = (string)outline.Attribute("objectID")!;

        var log = TestContext.Current.TestOutputHelper;
        log?.WriteLine($"{what}: one:{element}@{attribute} {oldValue} -> {value}; no text changed");
        log?.WriteLine($"  page    {before.PageHash[..12]} -> {after.PageHash[..12]}  {Verdict(before.PageHash, after.PageHash)}");
        foreach (var (id, hash) in before.ElementHashes)
        {
            var kind = id == outlineId ? "Outline" : "OE     ";
            log?.WriteLine($"  {kind} {hash[..12]} -> {after.ElementHashes[id][..12]}  {Verdict(hash, after.ElementHashes[id])}  {id}");
        }

        Assert.Equal(before.LastModifiedTime, after.LastModifiedTime);
        Assert.NotEqual(before.PageHash, after.PageHash);
        Assert.NotEqual(before.ElementHashes[outlineId], after.ElementHashes[outlineId]);
        Assert.All(before.ElementHashes.Where(e => e.Key != outlineId), e => Assert.Equal(e.Value, after.ElementHashes[e.Key]));

        static string Verdict(string a, string b) => a == b ? "same    (an edit on the old basis goes through)" : "CHANGED (an edit on the old basis gets StaleWrite)";
    }

    [Fact]
    public void BinaryVsBasic_OnALocalPage_HashEqual()
    {
        var pageId = PageId("Fixture Image");
        var basic = Fixture("GetPageContent", pageId, "Basic");
        var binary = Fixture("GetPageContent", pageId, "BinaryData");
        Assert.Contains("<one:CallbackID", basic, StringComparison.Ordinal);
        Assert.Contains("<one:Data>", binary, StringComparison.Ordinal);

        var a = PageBasis.From(basic);
        var b = PageBasis.From(binary);

        Assert.Equal(a.PageHash, b.PageHash);
        Assert.Equal(a.ElementHashes, b.ElementHashes);
    }

    [Fact]
    public void CloudImage_MissingIsTheImagesPiBinaryDataDropped()
    {
        var cloud = FakeGateway.Manifest.Cloud;
        Assert.NotNull(cloud);
        var basic = Fixture("GetPageContent", cloud.PageId, "Basic");
        var binary = Fixture("GetPageContent", cloud.PageId, "BinaryData");

        Assert.Equal(cloud.BasicImageCount, ImageCount.Count(basic));
        Assert.Equal(cloud.BinaryImageCount, ImageCount.Count(binary));
        Assert.True(ImageCount.Missing(basic, binary) > 0);
        Assert.Equal(cloud.BasicImageCount - cloud.BinaryImageCount, ImageCount.Missing(basic, binary));
    }

    [Fact]
    public void ImageCount_LocalPage_BothReadsCountTheImage()
    {
        var pageId = PageId("Fixture Image");

        Assert.Equal(0, ImageCount.Missing(Fixture("GetPageContent", pageId, "Basic"), Fixture("GetPageContent", pageId, "BinaryData")));
        Assert.Equal(1, ImageCount.Count(Fixture("GetPageContent", pageId, "Basic")));
    }

    [Fact]
    public void NoObjectId_IsSkipped()
    {
        var xml = $"""<one:Page xmlns:one="{OneNoteXml.Namespace2013}" lastModifiedTime="2026-11-04T00:00:00.000Z"><one:Outline><one:OEChildren><one:OE><one:T><![CDATA[x]]></one:T></one:OE></one:OEChildren></one:Outline></one:Page>""";

        var basis = PageBasis.From(xml);

        Assert.Empty(basis.ElementHashes);
        Assert.Equal("2026-11-04T00:00:00.000Z", basis.LastModifiedTime);
        Assert.Matches("^[0-9a-f]{64}$", basis.PageHash);
    }

    [Fact]
    public void ElementHashes_CoverEveryOutlineAndOe_WithAnObjectId()
    {
        var page = Fixture("GetPageContent", NestedPageId(), "Basic");
        var expected = XDocument.Parse(page).Root!.Descendants()
            .Where(e => e.Name == One + "Outline" || e.Name == One + "OE")
            .Select(e => (string?)e.Attribute("objectID"))
            .OfType<string>()
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, PageBasis.From(page).ElementHashes.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void LastModifiedTime_IsTheRootAttribute_Unparsed()
    {
        var page = Fixture("GetPageContent", NestedPageId(), "Basic");

        Assert.Equal((string?)XDocument.Parse(page).Root!.Attribute("lastModifiedTime"), PageBasis.From(page).LastModifiedTime);
    }

    [Fact]
    public void Formatting_DoesNotChangeTheHash()
    {
        var page = Fixture("GetPageContent", NestedPageId(), "Basic");
        var reformatted = XDocument.Parse(page).ToString(SaveOptions.None); // re-indented

        Assert.Equal(PageBasis.From(page), PageBasis.From(reformatted), BasisComparer.Instance);
    }

    [Theory]
    [InlineData("<one:Page xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\">")]
    [InlineData("<one:Page xmlns:one=\"http://schemas.microsoft.com/office/onenote/2010/onenote\" />")]
    [InlineData("<one:Section xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\" />")]
    public void BadXml_PageBasisThrowsOneNoteXmlException(string xml)
    {
        Assert.Throws<OneNoteXmlException>(() => PageBasis.From(xml));
    }

    [Fact]
    public void BadXml_HashThrowsOneNoteXmlException_WithoutContent()
    {
        var ex = Assert.Throws<OneNoteXmlException>(() => ElementHash.Compute("<a><secret-content></a>"));

        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrongNamespace_HashThrowsOneNoteXmlException()
    {
        Assert.Throws<OneNoteXmlException>(() => ElementHash.Compute("<one:Page xmlns:one=\"http://schemas.microsoft.com/office/onenote/2010/onenote\" />"));
    }

    // --- Acceptance: an edit elsewhere leaves untouched Outlines' hashes equal ---------------------------------------

    [Fact]
    public void Probe_EditElsewhere_LeavesTheUntouchedOutlineHashEqual()
    {
        var probe = FakeGateway.Manifest.Probe;
        Assert.NotNull(probe);
        var before = PageBasis.From(FakeGateway.ReadFixture(probe.BeforeFile));
        var after = PageBasis.From(Fixture("GetPageContent", probe.PageId, "Basic"));

        Assert.Equal(before.ElementHashes[probe.UntouchedOutlineId], after.ElementHashes[probe.UntouchedOutlineId]);
        Assert.NotEqual(before.ElementHashes[probe.EditedObjectId], after.ElementHashes[probe.EditedObjectId]);
        Assert.NotEqual(before.PageHash, after.PageHash);
    }

    // --- Fixture privacy ----------------------------------------------------------------------------------------------

    public static TheoryData<string> AllFixtureFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(FakeGateway.DefaultDirectory, "*", SearchOption.AllDirectories))
        {
            data.Add(Path.GetRelativePath(FakeGateway.DefaultDirectory, file).Replace('\\', '/'));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllFixtureFiles))]
    public void Fixture_HoldsNoUserPathOrAuthor(string file)
    {
        var text = FakeGateway.ReadFixture(file);

        foreach (Match match in UserPath().Matches(text))
        {
            Assert.Equal("mcp-test", match.Groups["user"].Value);
        }

        foreach (Match match in AuthorAttribute().Matches(text))
        {
            Assert.Equal(match.Groups["name"].Value.EndsWith("Initials", StringComparison.Ordinal) ? "MT" : "MCP Test", match.Groups["value"].Value);
        }
    }

    // --- Helpers ------------------------------------------------------------------------------------------------------

    private static string Fixture(string method, params string[] args) =>
        FakeGateway.ReadFixture(FakeGateway.Manifest.Entries.Single(e => e.Key == FixtureManifest.Key(method, args)).File!);

    private static string NestedPageId() => PageId("Fixture Nested");

    private static string PageId(string title) =>
        FakeGateway.Manifest.Entries.First(e => e.Method == "GetPageContent" && e.Source == "mcp-test" && e.Note == title).Args[0];

    [GeneratedRegex(@"[A-Za-z]:(?:\\|/|%5C)Users(?:\\|/|%5C)(?<user>[^\\/%""<>]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserPath();

    [GeneratedRegex(@"(?<name>\b(?:author|authorInitials|authorResolutionID|lastModifiedBy|lastModifiedByInitials|lastModifiedByResolutionID))=""(?<value>[^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorAttribute();

    private sealed class BasisComparer : IEqualityComparer<PageBasis>
    {
        public static readonly BasisComparer Instance = new();

        public bool Equals(PageBasis? x, PageBasis? y) =>
            x is not null && y is not null && x.LastModifiedTime == y.LastModifiedTime && x.PageHash == y.PageHash
            && x.ElementHashes.Count == y.ElementHashes.Count && x.ElementHashes.All(kv => y.ElementHashes.TryGetValue(kv.Key, out var v) && v == kv.Value);

        public int GetHashCode(PageBasis obj) => obj.PageHash.GetHashCode(StringComparison.Ordinal);
    }
}
