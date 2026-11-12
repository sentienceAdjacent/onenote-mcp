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

    // --- The HTML stopgap -----------------------------------------------------------------------------------------------

    [Theory]
    // file: URLs, and local or network paths in src/href
    [InlineData("<img src=\"file:///C:/a.png\">")]
    [InlineData("FILE://server/share/a.png")]
    [InlineData("<a href=\"C:\\x\\a.txt\">a</a>")]
    [InlineData("<a href=\"c:/x/a.txt\">a</a>")]
    [InlineData("<a href='\\\\server\\share'>s</a>")]
    [InlineData("<img src=//host/a.png>")]
    [InlineData("<img SRC = \"\\\\?\\C:\\a.png\">")]
    [InlineData("&#102;ile:///C:/a")]
    [InlineData("&lt;img src=&quot;C:\\a.png&quot;&gt;")]
    [InlineData("&amp;#102;ile:///C:/a")]
    // deny by default: a URL must be absolute http, https, mailto or onenote
    [InlineData("<a href=\"page.html\">p</a>")]
    [InlineData("<a href=\"#x\">p</a>")]
    [InlineData("<a href=\"/x/a.txt\">p</a>")]
    [InlineData("<a href=\"//host/a\">p</a>")]
    [InlineData("<a href=\"ftp://host/a\">p</a>")]
    [InlineData("<a href=\"\">p</a>")]
    [InlineData("<a href>p</a>")]
    [InlineData("<a/href=\"C:\\x\">p</a>")]
    [InlineData("<a href=C:&#92;x>p</a>")]
    [InlineData("<a href=\"C:\\x")] // never closed
    [InlineData("<a\nhref=\"C:\\x\">p</a>")]
    [InlineData("<a xlink:href=\"C:\\x\">p</a>")]
    // other URL-bearing attributes
    [InlineData("<table background=\"C:\\a.png\"><tr><td>x</td></tr></table>")]
    [InlineData("<button formaction=\"C:\\x\">b</button>")]
    [InlineData("<q cite=\"\\\\server\\a\">q</q>")]
    [InlineData("<a href=\"https://example.invalid/\" ping=\"\\\\server\\a\">p</a>")]
    [InlineData("<td data=\"C:\\a\">x</td>")]
    [InlineData("<td lowsrc=\"C:\\a\">x</td>")]
    [InlineData("<td dynsrc=\"C:\\a\">x</td>")]
    [InlineData("<td poster=\"C:\\a\">x</td>")]
    [InlineData("<td srcset=\"https://example.invalid/a 1x, C:\\b 2x\">x</td>")]
    [InlineData("<td action=\"C:\\a\">x</td>")]
    // normalisation: entities without semicolons, tabs, newlines and control characters
    [InlineData("<a href=\"jav&#x09;ascript:x\">p</a>")]
    [InlineData("<a href=\"&#102ile:///C:/a\">p</a>")]
    [InlineData("<a href=\"&#x66ile:///C:/a\">p</a>")]
    [InlineData("<a href=\"fi\tle:///C:/a\">p</a>")]
    [InlineData("<a href=\"fi\nle:///C:/a\">p</a>")]
    [InlineData("<a href=\"\u0001file:///C:/a\">p</a>")]
    [InlineData("<a href=\"&#104;ttps://example.invalid/\">p</a>")] // a value is checked at every decoding, not only the last
    [InlineData("<a href=\"https&colon;//example.invalid/\">p</a>")]
    // onenote: only within the page (#...) or to a web notebook (http/https), never to a local, relative or network path
    [InlineData("<a href=\"onenote:///C:\\x\\s.one#p\">p</a>")]
    [InlineData("<a href=\"onenote:C:\\x\\s.one#p\">p</a>")]
    [InlineData("<a href=\"onenote:\\\\server\\share\\s.one#p\">p</a>")]
    [InlineData("<a href=\"onenote://server/share/s.one#p\">p</a>")]
    [InlineData("<a href=\"onenote:..\\s.one#p\">p</a>")]
    [InlineData("<a href=\"onenote:s.one#p\">p</a>")]
    [InlineData("<a href=\"onenote:file:///C:/x/s.one\">p</a>")]
    [InlineData("<a href=\"onenote:\">p</a>")]
    [InlineData("<a href=\"onenote:&#9;\\\\server\\s.one\">p</a>")]
    [InlineData("<a href=\"onenote:#p&amp;base-path=\\\\server\\s.one\">p</a>")]
    [InlineData("<a href=\"onenote:#p&amp;base-path=C:\\x\\s.one\">p</a>")]
    [InlineData("<a href=\"onenote:#p&amp;BASE-PATH=x.one\">p</a>")]
    [InlineData("<a href=\"onenote:https://example.invalid/n.one#p&amp;base-path=C:\\x\">p</a>")]
    [InlineData("<a href=\"onenote:#p&amp;x=C:\\s.one\">p</a>")]
    [InlineData("<a href=\"onenote:#p&amp;x=c:/s.one\">p</a>")]
    [InlineData("<a href=\"onenote:#p&amp;x=//server/s.one\">p</a>")]
    [InlineData("<a href=\"onenote:#p&amp;x=..%5Cs.one\">p</a>")]
    [InlineData("<a href=\"onenote:#p&amp;x=%5C%5Cserver%5Cs.one\">p</a>")]
    [InlineData("<a href=\"onenote:#p&amp;x=file:///C:/s.one\">p</a>")]
    [InlineData("<a href=\"onenote:#p&amp;x=1\">p</a>")] // an unknown parameter
    [InlineData("<a href=\"onenote:#p&amp;section-id=C:\\x\">p</a>")] // a known parameter without a GUID
    [InlineData("<a href=\"onenote:#p&amp;section-id={0f6e7c1a-2b3d-4e5f-8a9b-0c1d2e3f4a5b}&amp;base-path=x\">p</a>")]
    [InlineData("<a href=\"onenote:#p%26base-path%3DC:%5Cx\">p</a>")] // a parameter hidden in the title
    [InlineData("<a href=\"onenote:#p%2526base-path%253Dx\">p</a>")]
    [InlineData("<a href=\"onenote:#%2525252525252541\">p</a>")] // still changing after every decoding round
    [InlineData("<a href=\"onenote:#p\\x\">p</a>")]
    [InlineData("<a href=\"onenote:https://\\\\server\\s.one#p\">p</a>")]
    [InlineData("<a href=\"onenote:https:///s.one#p\">p</a>")] // no host
    [InlineData("<a href=\"onenote:https://example.invalid/n.one%5C..%5Cx#p\">p</a>")]
    [InlineData("<a href=\"onenote:https://example.invalid/n.one?base-path=x#p\">p</a>")]
    // tags a parser could read differently: both the tokenizer and the plain scan count
    [InlineData("<a title=\"a>b\" href=\"C:\\x\">p</a>")] // a > inside quotes: only the tokenizer sees the href
    [InlineData("<!-- <a title=\" --> <a href=\"C:\\x\">p</a>")] // a comment ends before the quote closes
    [InlineData("<!--<a href=\"C:\\x\">-->")] // inert to an HTML5 parser, not necessarily to OneNote's: only the plain scan sees it
    [InlineData("<a title='x\"' href=\"C:\\x\">p</a>")]
    [InlineData("<a href=\"https://example.invalid/\" href=\"C:\\x\">p</a>")]
    [InlineData("<svg:image href=\"https://example.invalid/a.png\"/>")]
    // resource-loading tags, whatever their URL
    [InlineData("<img src=\"https://example.invalid/a.png\">")]
    [InlineData("<object data=\"https://example.invalid/a\"></object>")]
    [InlineData("<embed src=\"https://example.invalid/a\">")]
    [InlineData("<link rel=stylesheet href=\"https://example.invalid/a.css\">")]
    [InlineData("<iframe src=\"https://example.invalid/\"></iframe>")]
    [InlineData("<IFRAME>")]
    [InlineData("<style>p{}</style>")]
    [InlineData("<svg><image href=\"https://example.invalid/a.png\"/></svg>")]
    [InlineData("<video poster=\"https://example.invalid/a.png\"></video>")]
    [InlineData("<audio src=\"https://example.invalid/a.mp3\"></audio>")]
    [InlineData("<script>x</script>")]
    [InlineData("<base href=\"https://example.invalid/\">")]
    [InlineData("<meta http-equiv=refresh>")]
    [InlineData("<form action=\"https://example.invalid/\"></form>")]
    // CSS in style attributes
    [InlineData("<span style=\"background:url(C:\\a.png)\">x</span>")]
    [InlineData("<span style=\"background:url('//host/a.png')\">x</span>")]
    [InlineData("<span style=\"background:url(https://example.invalid/a.png)\">x</span>")] // url() fetches, whatever the URL
    [InlineData("<span style=\"background:u\\72l(x)\">x</span>")]
    [InlineData("<span style=\"background-image:image-set('a.png' 1x)\">x</span>")]
    [InlineData("<span style=\"behavior:expression(x)\">x</span>")]
    [InlineData("<span style=\"@import 'a.css'\">x</span>")]
    [InlineData("<span style=\"background:url&#40;C:\\a.png)\">x</span>")]
    public void HasRefusedHtml_RefusesLocalReferencesResourceTagsAndAnyUrlNotAbsoluteWebMailOrOneNote(string text) =>
        Assert.True(PageWriteScan.HasRefusedHtml(text));

    [Theory]
    [InlineData("<a href=\"https://example.invalid/a\">web</a>")]
    [InlineData("<a href=\"HTTP://example.invalid/a\">web</a>")]
    [InlineData("<a href=\" https://example.invalid/a \">web</a>")]
    [InlineData("<a href=https://example.invalid/?a=1&amp;b=2>web</a>")]
    [InlineData("<a href=\"mailto:someone@example.invalid\">mail</a>")]
    [InlineData("<a href=\"onenote:#page\">p</a>")]
    [InlineData("<a href=\"onenote:https://example.invalid/n.one#p\">p</a>")]
    [InlineData("<a href=\"ONENOTE:HTTP://example.invalid/n.one#p\">p</a>")]
    [InlineData("<a href=\"onenote:#Page&amp;section-id={0f6e7c1a-2b3d-4e5f-8a9b-0c1d2e3f4a5b}&amp;page-id={1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d}&amp;end\">p</a>")]
    [InlineData("<a href=\"onenote:#My%20Page&amp;section-id={0f6e7c1a-2b3d-4e5f-8a9b-0c1d2e3f4a5b}&amp;page-id={1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d}&amp;object-id={0f6e7c1a-2b3d-4e5f-8a9b-0c1d2e3f4a5b}&amp;end\">p</a>")]
    [InlineData("<a href=\"onenote:https://example.invalid/Docs/My%20Notebook/S.one#P&amp;section-id={0f6e7c1a-2b3d-4e5f-8a9b-0c1d2e3f4a5b}&amp;page-id={1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d}&amp;end\">p</a>")]
    [InlineData("<a href=\"onenote:https://example.invalid/Docs/My%20Notebook/S.one\">p</a>")] // a section, no page
    [InlineData("my profile: settings")]
    [InlineData("the share is \\\\server\\share")]
    [InlineData("C:\\temp is a folder")]
    [InlineData("data-src=\"x\" srcset")]
    [InlineData("x < y and y > z")]
    [InlineData("a<b>bold</b><i>i</i><u>u</u><br><br/>")]
    [InlineData("<span lang=en-US>x</span>")]
    [InlineData("<span style='font-family:Calibri;font-size:11.0pt;color:#595959'>x</span>")]
    [InlineData("<span style=\"color:rgb(1, 2, 3);background:rgba(0,0,0,0.5)\">x</span>")]
    [InlineData("<span style=\"font-weight:bold;text-decoration:underline\">x</span>")]
    [InlineData("see url(x) in the CSS docs")]
    [InlineData("an image of a cat")]
    public void HasRefusedHtml_AllowsOrdinaryTextFormattingAndWebMailOrOneNoteLinks(string text) =>
        Assert.False(PageWriteScan.HasRefusedHtml(text));

    [Fact]
    public void HtmlProblems_CountsNewBlocksAndReferences_ButNotUnchangedOnesOnTheSameElement()
    {
        string Oe(string? id, string inner) => $"<one:OE{(id is null ? string.Empty : $" objectID=\"{id}\"")}>{inner}</one:OE>";
        string Outline(string body) => Page($"<one:Outline objectID=\"{Y}\"><one:OEChildren>{body}</one:OEChildren></one:Outline>");
        const string Link = "<one:T><![CDATA[<a href=\"file:///C:/a.txt\">a</a>]]></one:T>";
        const string Block = "<one:HTMLBlock><one:Data><![CDATA[<p>x</p>]]></one:Data></one:HTMLBlock>";
        var read = Outline(Oe(X, Link) + Block);

        Assert.Equal((0, 0), PageWriteScan.HtmlProblems(read, read));
        Assert.Equal((1, 1), PageWriteScan.HtmlProblems(read, null)); // no read grant: everything counts
        Assert.Equal((0, 1), PageWriteScan.HtmlProblems(Outline(Oe(null, Link)), read)); // the same link on a new OE
        Assert.Equal((1, 0), PageWriteScan.HtmlProblems(Outline(Block.Replace("x", "y", StringComparison.Ordinal)), read)); // a changed block
        Assert.Equal((1, 0), PageWriteScan.HtmlProblems(Page($"<one:Outline><one:OEChildren>{Block}</one:OEChildren></one:Outline>"), read)); // no owner
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

    /// <summary>A scripted file system: <c>links</c> maps a path to its immediate target; every path asked about is recorded.</summary>
    private sealed class FakeLinks(Dictionary<string, string> links) : FilePathGate.ILinkReader
    {
        public List<string> Asked { get; } = [];

        public string? ImmediateTarget(string path)
        {
            Asked.Add(path);
            return links.GetValueOrDefault(path);
        }
    }

    [Theory]
    [InlineData(@"\\evil\share\loot")]
    [InlineData(@"\??\UNC\evil\share\loot")]
    [InlineData("//evil/share/loot")]
    public void Canonical_AChainLocalLocalUnc_IsRefusedAtTheUncHop_WhichIsNeverTouched(string uncTarget)
    {
        var links = new FakeLinks(new(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\a"] = @"C:\b",
            [@"C:\b"] = uncTarget,
        });

        Assert.Null(FilePathGate.Canonical(@"C:\a\x.png", links, out var refusal));

        Assert.Equal(FilePathRefusal.Network, refusal);
        Assert.Equal([@"C:\a", @"C:\b"], links.Asked);
        Assert.DoesNotContain(links.Asked, p => p.Contains("evil", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Canonical_ADeviceHop_IsRefusedWithoutTouchingIt()
    {
        var links = new FakeLinks(new(StringComparer.OrdinalIgnoreCase) { [@"C:\a"] = @"\\?\GLOBALROOT\Device\Mup\evil" });

        Assert.Null(FilePathGate.Canonical(@"C:\a\x.png", links, out var refusal));

        Assert.Equal(FilePathRefusal.Device, refusal);
        Assert.Equal([@"C:\a"], links.Asked);
    }

    [Theory]
    [InlineData(@"\foo")]
    [InlineData("/foo")]
    [InlineData(@"X:foo")]
    [InlineData(@"\Device\Mup\evil\share")]
    public void Canonical_ARootedButNotFullyQualifiedHop_IsRefused_NeverResolvedAgainstTheProcessDrive(string target)
    {
        var links = new FakeLinks(new(StringComparer.OrdinalIgnoreCase) { [@"C:\a"] = target });

        Assert.Null(FilePathGate.Canonical(@"C:\a\x.png", links, out var refusal));

        Assert.Equal(FilePathRefusal.NotFullyQualified, refusal);
        Assert.Equal([@"C:\a"], links.Asked);
    }

    [Fact]
    public void Canonical_ARelativeHop_IsStillFollowed_AgainstTheLinksFolder()
    {
        var links = new FakeLinks(new(StringComparer.OrdinalIgnoreCase) { [@"C:\dir\a"] = @"..\x" });

        Assert.Equal(@"C:\x\y.png", FilePathGate.Canonical(@"C:\dir\a\y.png", links, out _));
        Assert.Equal([@"C:\dir", @"C:\dir\a", @"C:\x", @"C:\x\y.png"], links.Asked);
    }

    [Fact]
    public void Canonical_FollowsLocalHopsOneAtATime_RelativeTargetsIncluded()
    {
        var links = new FakeLinks(new(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\a"] = @"C:\b\c",
            [@"C:\b"] = @"..\d", // relative to the link's folder, C:\
            [@"C:\d\c"] = @"\??\C:\e",
        });

        Assert.Equal(@"C:\e\x.png", FilePathGate.Canonical(@"C:\a\x.png", links, out _));
        Assert.Equal([@"C:\a", @"C:\b", @"C:\d", @"C:\d\c", @"C:\e", @"C:\e\x.png"], links.Asked);
    }

    [Fact]
    public void Canonical_MoreThanTheHopCap_IsRefused()
    {
        var chain = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i <= FilePathGate.MaxLinkHops; i++)
        {
            chain[$@"C:\l{i}"] = $@"C:\l{i + 1}";
        }

        Assert.Null(FilePathGate.Canonical(@"C:\l0\x.png", new FakeLinks(chain), out var refusal));
        Assert.Equal(FilePathRefusal.TooManyLinks, refusal);

        chain.Remove($@"C:\l{FilePathGate.MaxLinkHops}"); // exactly the cap: allowed
        Assert.Equal($@"C:\l{FilePathGate.MaxLinkHops}\x.png", FilePathGate.Canonical(@"C:\l0\x.png", new FakeLinks(chain), out _));
    }

    [Fact]
    public void Canonical_ALinkCycle_IsRefused()
    {
        var links = new FakeLinks(new(StringComparer.OrdinalIgnoreCase) { [@"C:\a"] = @"C:\b", [@"C:\b"] = @"C:\a" });

        Assert.Null(FilePathGate.Canonical(@"C:\a\x.png", links, out var refusal));
        Assert.Equal(FilePathRefusal.TooManyLinks, refusal);
        Assert.True(links.Asked.Count <= FilePathGate.MaxLinkHops + 1);
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
