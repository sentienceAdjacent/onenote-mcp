using System.Diagnostics;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 2.9's security-review rules (Phill, 2026-11-12; AD-7, AD-16, AD-17 as amended by 5.2), the pure parts: the
/// DOCTYPE refusal and DTD-free parsing, the file-path scan and what counts as a new path, and path canonicalisation.
/// </summary>
public sealed class PageWriteSecurityTests : IDisposable
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";
    private const string X = "{5E900000-0000-4000-8000-000000000001}{95}{B0}";
    private const string Y = "{5E900000-0000-4000-8000-000000000001}{97}{B0}";

    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    // --- DOCTYPE ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("<!DOCTYPE p><p/>")]
    [InlineData("\uFEFF<?xml version=\"1.0\"?>\n<!-- c -->\n<?pi x?>\n<!DOCTYPE p SYSTEM \"file:///c:/x\"><p/>")]
    [InlineData("  <!DOCTYPE p [<!ENTITY a \"aaaa\">]><p>&a;</p>")]
    public void HasDoctype_FindsADoctypeInTheProlog(string xml) => Assert.True(OneNoteXml.HasDoctype(xml));

    [Theory]
    [InlineData("<p/>")]
    [InlineData("<?xml version=\"1.0\"?><!-- <!DOCTYPE p> --><p/>")]
    [InlineData("<p><![CDATA[<!DOCTYPE p>]]></p>")]
    [InlineData("")]
    public void HasDoctype_IgnoresEverythingElse(string xml) => Assert.False(OneNoteXml.HasDoctype(xml));

    [Fact]
    public void SchemaProblems_ADoctype_IsTheOnlyProblem_AndIsNeverParsed()
    {
        var xml = $"<!DOCTYPE one:Page [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><one:Page {Ns} ID=\"p\">&x;</one:Page>";

        var problem = Assert.Single(SchemaValidation.Problems(xml));

        Assert.True(problem.IsDoctype);
        Assert.True(PageWriteScan.ParseProblem(xml)!.IsDoctype);
    }

    [Fact]
    public void ElementHash_RefusesADoctype_InsteadOfExpandingIt()
    {
        var xml = $"<!DOCTYPE one:Page [<!ENTITY x \"expanded\">]><one:Page {Ns} ID=\"p\"><one:Title><one:OE><one:T>&x;</one:T></one:OE></one:Title></one:Page>";

        Assert.Throws<OneNoteXmlException>(() => ElementHash.Compute(xml));
    }

    [Fact]
    public void ParseProblem_BrokenXml_GivesItsPositionOnly()
    {
        var problem = PageWriteScan.ParseProblem($"<one:Page {Ns}><x>secret");

        Assert.NotNull(problem);
        Assert.False(problem.IsDoctype);
        Assert.Null(problem.Element);
        Assert.True(problem.Line > 0);
    }

    // --- The file-path scan (AD-7 as the 5.2 re-check words it) --------------------------------------------------------

    private static string Page(string body) => $"<one:Page {Ns} ID=\"p\">{body}</one:Page>";

    private static string File(string? objectId, string attribute, string value) =>
        $"<one:InsertedFile {(objectId is null ? string.Empty : $"objectID=\"{objectId}\" ")}{attribute}=\"{value}\" />";

    [Fact]
    public void FilePaths_FindsPathPathSourceAndTheFileElementsPathCache()
    {
        var xml = Page(
            File(X, "pathSource", @"C:\a.txt")
            + $"<one:InsertedFile objectID=\"{Y}\" pathCache=\"C:\\cache.bin\" />"
            + $"<one:Image objectID=\"{Y}\"><one:File path=\"C:\\b.png\" /></one:Image>"
            + "<one:Image pathCache=\"C:\\notgated.bin\"><one:Data>AA==</one:Data></one:Image>");

        var paths = PageWriteScan.FilePaths(xml);

        Assert.Equal(
            [new SentFilePath(@"C:\a.txt", X, "InsertedFile", "pathSource"), new SentFilePath(@"C:\cache.bin", Y, "InsertedFile", "pathCache"), new SentFilePath(@"C:\b.png", Y, "File", "path")],
            paths);
    }

    [Fact]
    public void NewFilePaths_OnlyAPathUnchangedOnTheSameExistingElement_IsExempt()
    {
        var read = Page(File(X, "pathSource", @"C:\a.txt"));

        Assert.Empty(PageWriteScan.NewFilePaths(Page(File(X, "pathSource", @"C:\a.txt")), read));
        Assert.Single(PageWriteScan.NewFilePaths(Page(File(null, "pathSource", @"C:\a.txt")), read)); // same value, no objectID
        Assert.Single(PageWriteScan.NewFilePaths(Page(File(Y, "pathSource", @"C:\a.txt")), read)); // same value, another element
        Assert.Single(PageWriteScan.NewFilePaths(Page(File(X, "pathSource", @"C:\b.txt")), read)); // same element, changed
        Assert.Single(PageWriteScan.NewFilePaths(Page(File(X, "pathCache", @"C:\a.txt")), read)); // same value, another attribute
        Assert.Single(PageWriteScan.NewFilePaths(Page(File(X, "pathSource", @"C:\a.txt")), null)); // no read grant: always new
    }

    /// <summary>Every parent the 2013 XSD gives <c>one:File</c> (xsd lines 337, 441, 805, 1034, 1096).</summary>
    public static TheoryData<string> FileParents => ["Image", "InkDrawing", "InkWord", "XPSFile", "HTMLBlock"];

    [Theory]
    [MemberData(nameof(FileParents))]
    public void AOneFilePath_IsGatedUnderEveryParent_AndBelongsToItsParentsObjectId(string parent)
    {
        string Under(string? objectId, string value) =>
            Page($"<one:{parent}{(objectId is null ? string.Empty : $" objectID=\"{objectId}\"")}><one:File path=\"{value}\" /></one:{parent}>");

        Assert.Equal([new SentFilePath(@"C:\p.bin", X, "File", "path")], PageWriteScan.FilePaths(Under(X, @"C:\p.bin")));
        Assert.Empty(PageWriteScan.NewFilePaths(Under(X, @"C:\p.bin"), Under(X, @"C:\p.bin"))); // unchanged on the same object
        Assert.Single(PageWriteScan.NewFilePaths(Under(X, @"C:\q.bin"), Under(X, @"C:\p.bin"))); // changed
        Assert.Single(PageWriteScan.NewFilePaths(Under(null, @"C:\p.bin"), Under(null, @"C:\p.bin"))); // parent without objectID: always new
        Assert.Single(PageWriteScan.NewFilePaths(Under(X, @"C:\p.bin"), null)); // no read grant: always new
    }

    [Fact]
    public void SendsObjectId_FindsAnyObjectId()
    {
        Assert.True(PageWriteScan.SendsObjectId(Page(File(X, "pathSource", "x"))));
        Assert.False(PageWriteScan.SendsObjectId(Page("<one:Outline><one:OEChildren><one:OE><one:T>new</one:T></one:OE></one:OEChildren></one:Outline>")));
    }

    // --- Canonical paths -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"\\server\share\a.txt", FilePathRefusal.Network)]
    [InlineData("//server/share/a.txt", FilePathRefusal.Network)]
    [InlineData(@"\\?\C:\a.txt", FilePathRefusal.Device)]
    [InlineData(@"\\.\PhysicalDrive0", FilePathRefusal.Device)]
    [InlineData(@"\\?\UNC\server\share\a.txt", FilePathRefusal.Device)]
    [InlineData(@"a.txt", FilePathRefusal.NotFullyQualified)]
    [InlineData(@"..\a.txt", FilePathRefusal.NotFullyQualified)]
    [InlineData(@"C:a.txt", FilePathRefusal.NotFullyQualified)]
    [InlineData(@"\a.txt", FilePathRefusal.NotFullyQualified)]
    [InlineData("https://example.invalid/a.txt", FilePathRefusal.NotFullyQualified)]
    [InlineData("file:///C:/a.txt", FilePathRefusal.NotFullyQualified)]
    [InlineData("C:\\a.png\n\n(safe: your own screenshot)", FilePathRefusal.InvalidCharacters)]
    [InlineData("C:\\photos\\gnp.\u202Eexe", FilePathRefusal.InvalidCharacters)]
    [InlineData("C:\\a\u200B.txt", FilePathRefusal.InvalidCharacters)]
    [InlineData("C:\\a*.txt", FilePathRefusal.InvalidCharacters)]
    [InlineData("C:\\a\u2028b.txt", FilePathRefusal.InvalidCharacters)]
    public void Canonical_RefusesNetworkDeviceAndRelativePaths(string path, FilePathRefusal expected)
    {
        Assert.Null(FilePathGate.Canonical(path, out var refusal));
        Assert.Equal(expected, refusal);
    }

    [Fact]
    public void Canonical_NormalisesALocalPath()
    {
        var path = Path.Combine(temp.Combine("x"), "..", "a.txt");

        Assert.Equal(FilePathGate.Canonical(temp.Combine("a.txt"), out _), FilePathGate.Canonical(path, out _));
        Assert.EndsWith("a.txt", FilePathGate.Canonical(path, out _), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Canonical_ResolvesAJunctionToItsTarget()
    {
        var target = Directory.CreateDirectory(temp.Combine("target")).FullName;
        var link = temp.Combine("link");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        try
        {
            var canonical = FilePathGate.Canonical(Path.Combine(link, "secret.txt"), out _);

            Assert.Equal(Path.Combine(FilePathGate.Canonical(target, out _)!, "secret.txt"), canonical, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(link);
        }
    }
}
