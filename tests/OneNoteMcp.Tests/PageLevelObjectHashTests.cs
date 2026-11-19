using System.Xml.Linq;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Backlog 5: an image, ink drawing or inserted file placed directly on the page, outside any Outline, gets an AD-17
/// element hash like an Outline or OE, so a stale write that sends one is refused element by element. Over
/// <c>synthetic/page-level-objects-Basic.xml</c>: title OE 10, Outline 20 with OE 21, page-level image 70, ink 71,
/// inserted file 72, media file 73 (not hashed: its <c>pathCache</c> isn't excluded from the hash), and an image with no
/// <c>objectID</c>.
/// </summary>
public sealed class PageLevelObjectHashTests
{
    private const string PageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000007}";

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private static string Read => FakeGateway.ReadFixture("synthetic/page-level-objects-Basic.xml");

    private static string G(int n) => PageWriteCheckTests.G(n);

    [Fact]
    public void TheBasis_HashesEveryPageLevelObjectWithAnId_BesideTheOutlinesAndOes()
    {
        var basis = PageBasis.From(Read);

        Assert.Equal(
            new[] { G(10), G(20), G(21), G(70), G(71), G(72) }.Order(StringComparer.Ordinal),
            basis.ElementHashes.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(70)]
    [InlineData(71)]
    [InlineData(72)]
    public void ChangingAPageLevelObject_ChangesOnlyItsOwnHash_AndThePageHash(int n)
    {
        var before = PageBasis.From(Read);
        var page = XDocument.Parse(Read);
        page.Descendants().Single(e => (string?)e.Attribute("objectID") == G(n)).Element(One + "Size")!.SetAttributeValue("width", "99.0");

        var after = PageBasis.From(page.ToString(SaveOptions.DisableFormatting));

        Assert.NotEqual(before.ElementHashes[G(n)], after.ElementHashes[G(n)]);
        Assert.NotEqual(before.PageHash, after.PageHash);
        foreach (var id in before.ElementHashes.Keys.Where(k => k != G(n)))
        {
            Assert.Equal(before.ElementHashes[id], after.ElementHashes[id]);
        }
    }

    [Fact]
    public void ASentPageLevelImage_IsAHashTarget()
    {
        var image = XDocument.Parse(Read).Descendants(One + "Image").First(e => (string?)e.Attribute("objectID") == G(70));
        var sent = $"<one:Page xmlns:one=\"{OneNoteXml.Namespace2013}\" ID=\"{PageId}\">{image.ToString(SaveOptions.DisableFormatting)}</one:Page>";

        var result = PageWriteCheck.Validate(sent, PageId, Read, []);

        Assert.Null(result.Problem);
        Assert.Equal([G(70)], result.HashTargets);
    }

    [Fact]
    public void ALargePagesParts_CarryThePageLevelObjectsHashes()
    {
        var tree = ElementSpans.Page(Read);

        var hashed = tree.Nodes.Select(n => n.HashId).OfType<string>().Order(StringComparer.Ordinal);
        Assert.Equal(PageBasis.From(Read).ElementHashes.Keys.Order(StringComparer.Ordinal), hashed);
    }
}
