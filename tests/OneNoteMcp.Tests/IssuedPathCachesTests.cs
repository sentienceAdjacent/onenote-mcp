using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

public sealed class IssuedPathCachesTests
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";
    private const string Oe = "{5E900000-0000-4000-8000-000000000001}{11}{B0}";

    private static string Page(string cache, string oe = Oe) =>
        $"<one:Page {Ns} ID=\"p\"><one:Outline><one:OEChildren><one:OE objectID=\"{oe}\">"
        + $"<one:InsertedFile pathCache=\"{cache}\" pathSource=\"C:\\a.txt\" preferredName=\"a.txt\" /></one:OE></one:OEChildren></one:Outline></one:Page>";

    private static SentFilePath Cache(string value, string oe = Oe) => new(value, oe, "InsertedFile", "pathCache");

    [Fact]
    public void APathCacheARead_Returned_IsIssued_ForThatPageOwnerAndElementOnly()
    {
        var issued = new IssuedPathCaches();
        issued.Remember("page1", Page(@"C:\t\{1}.bin"));

        Assert.True(issued.Issued("page1", Cache(@"C:\t\{1}.bin")));
        Assert.False(issued.Issued("page2", Cache(@"C:\t\{1}.bin"))); // another page
        Assert.False(issued.Issued("page1", Cache(@"C:\t\{1}.bin", "{5E900000-0000-4000-8000-000000000001}{12}{B0}"))); // another OE
        Assert.False(issued.Issued("page1", Cache(@"C:\t\{2}.bin"))); // never returned
        Assert.False(issued.Issued("page1", new SentFilePath(@"C:\t\{1}.bin", Oe, "MediaFile", "pathCache"))); // another element
        Assert.False(issued.Issued("page1", new SentFilePath(@"C:\t\{1}.bin", Oe, "InsertedFile", "pathSource"))); // not a pathCache
    }

    [Fact]
    public void EveryReadAddsItsOwnTempName()
    {
        var issued = new IssuedPathCaches();
        issued.Remember("page1", Page(@"C:\t\{1}.bin"));
        issued.Remember("page1", Page(@"C:\t\{2}.bin"));

        Assert.True(issued.Issued("page1", Cache(@"C:\t\{1}.bin")));
        Assert.True(issued.Issued("page1", Cache(@"C:\t\{2}.bin")));
    }

    [Fact]
    public void ItHoldsAtMostItsCapacity_ForgettingTheOldestFirst()
    {
        var issued = new IssuedPathCaches();
        for (var i = 0; i <= IssuedPathCaches.Capacity; i++)
        {
            issued.Remember("page1", Page($@"C:\t\{{{i}}}.bin"));
        }

        Assert.False(issued.Issued("page1", Cache(@"C:\t\{0}.bin")));
        Assert.True(issued.Issued("page1", Cache(@"C:\t\{1}.bin")));
        Assert.True(issued.Issued("page1", Cache($@"C:\t\{{{IssuedPathCaches.Capacity}}}.bin")));
    }

    [Fact]
    public void XmlWithoutAPathCache_OrThatIsNotWellFormed_AddsNothing()
    {
        var issued = new IssuedPathCaches();
        issued.Remember("page1", $"<one:Page {Ns} ID=\"p\" />");
        issued.Remember("page1", "<one:Page pathCache=");

        Assert.Equal(0, issued.Count);
    }
}
