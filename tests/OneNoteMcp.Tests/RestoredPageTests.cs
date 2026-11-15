using System.Xml.Linq;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 2.11's XML and file pieces, against the captured fixtures: <see cref="RestoredPage"/> (the transform, Q2's drops
/// and Q4's template objects), <see cref="NotebookSections"/> (group paths, recycle bin skipped),
/// <see cref="JournalFiles.Find"/> and <see cref="RestoreAttachments"/>.
/// </summary>
public sealed class RestoredPageTests : IDisposable
{
    private const string NewPageId = "{C4EA7E00-0000-4000-8000-000000000001}{1}{E1}";
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private static string ImagePage => FakeGateway.ReadFixture("mcp-test/page/image-BinaryData.xml");

    // --- RestoredPage.Build -------------------------------------------------------------------------------------------

    [Fact]
    public void Build_SetsTheRootId_RemovesEveryObjectId_AndKeepsInlineImageData()
    {
        var built = RestoredPage.Build(ImagePage, NewPageId, _ => true);

        var page = XDocument.Parse(built.Xml).Root!;
        Assert.Equal(NewPageId, (string?)page.Attribute("ID"));
        Assert.DoesNotContain(page.DescendantsAndSelf(), e => e.Attribute("objectID") is not null);
        var data = Assert.Single(page.Descendants(One + "Data"));
        Assert.Equal(XDocument.Parse(ImagePage).Descendants(One + "Data").Single().Value, data.Value);
        Assert.Empty(built.Dropped);
        Assert.Equal(0, built.KeptFiles);
        Assert.Empty(SchemaValidation.Problems(built.Xml));
    }

    [Fact]
    public void Build_RefusesADoctype()
    {
        var withDoctype = "<?xml version=\"1.0\"?>\n<!DOCTYPE x [<!ENTITY e \"boom\">]>\n" + ImagePage[(ImagePage.IndexOf("<one:Page", StringComparison.Ordinal))..];

        Assert.Throws<OneNoteXmlException>(() => RestoredPage.Build(withDoctype, NewPageId, _ => true));
    }

    [Fact]
    public void Build_RefusesARootThatIsntAPage() =>
        Assert.Throws<OneNoteXmlException>(() => RestoredPage.Build($"<one:Section xmlns:one=\"{OneNoteXml.Namespace2013}\" />", NewPageId, _ => true));

    [Fact]
    public void Build_InsertedFiles_LosePathSource_AndAreKeptOnlyWhenThePredicateAcceptsPathCache()
    {
        var xml = WithFiles(("InsertedFile", "C:\\a\\keep.docx", "keep"), ("InsertedFile", "C:\\a\\drop.docx", "drop"), ("MediaFile", "C:\\a\\m.mp3", "dropmedia"), ("InsertedFile", "C:\\a\\none.docx", null));
        var asked = new List<string>();

        var built = RestoredPage.Build(xml, NewPageId, p => { asked.Add(p); return p == "keep"; });

        var files = XDocument.Parse(built.Xml).Descendants().Where(e => e.Name == One + "InsertedFile" || e.Name == One + "MediaFile").ToList();
        var kept = Assert.Single(files);
        Assert.Equal("keep", (string?)kept.Attribute("pathCache"));
        Assert.Null(kept.Attribute("pathSource"));
        Assert.Equal(1, built.KeptFiles);
        Assert.Equal(2, built.Dropped[RestoredPage.AttachmentKind]);
        Assert.Equal(1, built.Dropped[RestoredPage.MediaKind]);
        Assert.Equal(["keep", "drop", "dropmedia"], asked); // a file with no pathCache never reaches the predicate
        Assert.DoesNotContain("C:\\a\\", built.Xml, StringComparison.Ordinal);
        Assert.Empty(SchemaValidation.Problems(built.Xml));
    }

    [Fact]
    public void Build_WithoutFiles_DropsEveryAttachmentAndMediaFile()
    {
        var xml = WithFiles(("InsertedFile", "C:\\a\\keep.docx", "keep"), ("MediaFile", "C:\\a\\m.mp3", "keep"));

        var built = RestoredPage.Build(xml, NewPageId, _ => true, keepFiles: false);

        Assert.DoesNotContain("InsertedFile", built.Xml, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaFile", built.Xml, StringComparison.Ordinal);
        Assert.Equal(0, built.KeptFiles);
        Assert.Equal(1, built.Dropped[RestoredPage.AttachmentKind]);
        Assert.Equal(1, built.Dropped[RestoredPage.MediaKind]);
    }

    [Theory]
    [InlineData("File", "path", "C:\\pictures\\a.png")]
    [InlineData("CallbackID", "callbackID", "{X}{1}{B0}")]
    public void Build_AnImageByPathOrCallback_IsDroppedAndCounted(string element, string attribute, string value)
    {
        var xml = Edit(ImagePage, p => p.Descendants(One + "Data").Single().ReplaceWith(new XElement(One + element, new XAttribute(attribute, value))));

        var built = RestoredPage.Build(xml, NewPageId, _ => true);

        Assert.DoesNotContain("one:Image", built.Xml, StringComparison.Ordinal);
        Assert.Equal(1, built.Dropped[RestoredPage.ImageKind]);
        Assert.Empty(SchemaValidation.Problems(built.Xml));
    }

    // --- Template objects (Q4) ------------------------------------------------------------------------------------------

    [Fact]
    public void TemplateObjectIds_AreTheTopLevelContentObjects_NotTheTitle()
    {
        var page = $"<one:Page xmlns:one=\"{OneNoteXml.Namespace2013}\" ID=\"{NewPageId}\">"
            + "<one:Title><one:OE objectID=\"{T}\"><one:T><![CDATA[]]></one:T></one:OE></one:Title>"
            + "<one:Outline objectID=\"{O}\"><one:OEChildren><one:OE objectID=\"{OE}\"><one:T>x</one:T></one:OE></one:OEChildren></one:Outline>"
            + "<one:Image objectID=\"{I}\"><one:CallbackID callbackID=\"{C}\" /></one:Image>"
            + "<one:InkDrawing objectID=\"{K}\"><one:CallbackID callbackID=\"{C2}\" /></one:InkDrawing>"
            + "</one:Page>";

        Assert.Equal(["{O}", "{I}", "{K}"], RestoredPage.TemplateObjectIds(page));
        Assert.True(RestoredPage.HasContent(page));
        Assert.True(RestoredPage.HasTitle(page));
        Assert.False(RestoredPage.HasContent($"<one:Page xmlns:one=\"{OneNoteXml.Namespace2013}\" ID=\"{NewPageId}\"><one:Title><one:OE><one:T /></one:OE></one:Title></one:Page>"));
    }

    // --- NotebookSections ---------------------------------------------------------------------------------------------

    [Fact]
    public void NotebookSections_ListsLiveSections_AndSkipsTheRecycleBin()
    {
        var sections = NotebookSections.Find(FakeGateway.ReadFixture("mcp-test/hierarchy/notebook-Sections.xml"));

        Assert.Equal(["Seed", "Fixtures"], sections.Select(s => s.Name));
        Assert.All(sections, s => Assert.Empty(s.Groups));
        Assert.All(sections, s => Assert.False(s.InRecycleBin));
    }

    [Fact]
    public void NotebookSections_GivesGroupPaths_OutermostFirst_AndTheSectionsState()
    {
        var xml = $"<one:Notebook xmlns:one=\"{OneNoteXml.Namespace2013}\" ID=\"{{N}}\" name=\"W\" path=\"C:\\W\">"
            + "<one:SectionGroup name=\"A\" ID=\"{GA}\"><one:SectionGroup name=\"B\" ID=\"{GB}\">"
            + "<one:Section name=\"S\" ID=\"{S}\" path=\"C:\\W\\A\\B\\S.one\" encrypted=\"true\" locked=\"true\" />"
            + "</one:SectionGroup></one:SectionGroup>"
            + "<one:Section name=\"T\" ID=\"{T}\" path=\"C:\\W\\T.one\" isInRecycleBin=\"true\" />"
            + "</one:Notebook>";

        var sections = NotebookSections.Find(xml);

        var nested = sections[0];
        Assert.Equal(["A", "B"], nested.Groups);
        Assert.Equal("A/B/S", nested.NamePath);
        Assert.True(nested.Encrypted);
        Assert.True(nested.Locked);
        Assert.Equal("C:\\W\\A\\B\\S.one", nested.Path);
        Assert.True(sections[1].InRecycleBin);
    }

    // --- JournalFiles.Find ----------------------------------------------------------------------------------------------

    [Fact]
    public void JournalFind_FindsAnEntryByItsId_InEitherGuidForm_AndNeverATempFile()
    {
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var folder = temp.Combine("journal");
        Directory.CreateDirectory(folder);
        var name = JournalFiles.EntryName(new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc), 7, id);
        File.WriteAllText(Path.Combine(folder, JournalFiles.TempName(JournalFiles.EntryName(DateTime.UtcNow, 8, other))), string.Empty);
        File.WriteAllText(Path.Combine(folder, name), string.Empty);
        var dashed = Guid.NewGuid();
        File.WriteAllText(Path.Combine(folder, $"20261112T000000000-9-{dashed:D}.json.gz"), string.Empty);

        Assert.Equal(Path.Combine(folder, name), JournalFiles.Find(folder, id));
        Assert.EndsWith($"{dashed:D}.json.gz", JournalFiles.Find(folder, dashed), StringComparison.Ordinal);
        Assert.Null(JournalFiles.Find(folder, other));
        Assert.Null(JournalFiles.Find(temp.Combine("missing"), id));
    }

    // --- RestoreAttachments (Q2) ------------------------------------------------------------------------------------------

    [Fact]
    public void RestoreAttachments_KeepsOnlyAGuidBinFile_DirectlyInTheTempFolder_ThatExists()
    {
        var tempFolder = temp.Combine("tmp");
        Directory.CreateDirectory(Path.Combine(tempFolder, "sub"));
        var good = Path.Combine(tempFolder, "{" + Guid.NewGuid().ToString("D").ToUpperInvariant() + "}.bin");
        var nested = Path.Combine(tempFolder, "sub", "{" + Guid.NewGuid().ToString("D") + "}.bin");
        var named = Path.Combine(tempFolder, "report.docx");
        foreach (var file in new[] { good, nested, named })
        {
            File.WriteAllText(file, "x");
        }

        Assert.True(RestoreAttachments.Keeps(good, tempFolder));
        Assert.True(RestoreAttachments.Keeps(good, tempFolder + "\\"));
        Assert.False(RestoreAttachments.Keeps(nested, tempFolder));
        Assert.False(RestoreAttachments.Keeps(named, tempFolder));
        Assert.False(RestoreAttachments.Keeps(Path.Combine(tempFolder, "{" + Guid.NewGuid().ToString("D") + "}.bin"), tempFolder)); // gone
        Assert.False(RestoreAttachments.Keeps(Path.Combine(tempFolder, "sub", "..", Path.GetFileName(nested)), tempFolder));
        Assert.False(RestoreAttachments.Keeps(@"\\server\share\{00000000-0000-0000-0000-000000000000}.bin", tempFolder));
        Assert.False(RestoreAttachments.Keeps(@"{00000000-0000-0000-0000-000000000000}.bin", tempFolder)); // relative
        Assert.False(RestoreAttachments.Keeps(string.Empty, tempFolder));
    }

    [Theory]
    [InlineData("Recovered.one", true)]
    [InlineData("recovered.ONE", true)]
    [InlineData("Group Notes.one", true)]
    [InlineData("Recovered", false)]
    [InlineData("a\\b.one", false)]
    [InlineData("..one", false)]
    [InlineData("COM1.one", false)]
    [InlineData("a|b.one", false)]
    [InlineData(".hidden.one", false)]
    [InlineData(" lead.one", false)]
    public void SectionFileNames_AcceptOnlyOneRelativeFileName(string name, bool ok) =>
        Assert.Equal(ok, SectionFileNames.Problem(name) is null);

    // --- Helpers ------------------------------------------------------------------------------------------------------

    private static string Edit(string xml, Action<XElement> edit)
    {
        var document = XDocument.Parse(xml);
        edit(document.Root!);
        return document.ToString();
    }

    /// <summary>The image fixture with one OE per file added to its Outline: (element, pathSource, pathCache or null).</summary>
    private static string WithFiles(params (string Element, string Source, string? Cache)[] files) => Edit(ImagePage, p =>
    {
        var children = p.Descendants(One + "OEChildren").First();
        foreach (var (element, source, cache) in files)
        {
            var file = new XElement(One + element, new XAttribute("pathSource", source), new XAttribute("preferredName", "f"));
            if (cache is not null)
            {
                file.Add(new XAttribute("pathCache", cache));
            }

            children.Add(new XElement(One + "OE", new XAttribute("objectID", "{F" + children.Elements().Count() + "}"), file));
        }
    });
}
