using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Integration;

/// <summary>Story 17's Ballast builders, without OneNote: XSD validity, sizes, the PNG encoder, the plan and the names.</summary>
public sealed partial class BallastContentTests
{
    private const string PageId = "{11111111-2222-3333-4444-555555555555}{1}{E1}";

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private static string RepoRoot { get; } =
        typeof(BallastContentTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RepoRoot").Value!;

    public static TheoryData<string, string> PageBodies()
    {
        var data = new TheoryData<string, string>();
        var updates = BallastContent.OversizedUpdates(1);
        for (var i = 0; i < updates.Count; i++)
        {
            data.Add($"oversized[{i}]", updates[i]);
        }

        var tree = BallastContent.TreePlan(1);
        data.Add("tree page (plain)", BallastContent.TreePageBody(tree[0].Pages[0]));
        data.Add("tree page (marked)", BallastContent.TreePageBody(tree[0].Pages[BallastContent.MarkEvery - 1]));
        data.Add("vault page", BallastContent.VaultPageBody(0));
        data.Add("title", BallastContent.TitleBody(BallastContent.ImagesTitle));
        data.Add("image", BallastContent.ImageOutline(BallastContent.Png(4, 4, 7), 3));
        data.Add("inserted file", BallastContent.InsertedFileOutline(@"C:\Temp\a & b\ballast-file-01.bin", BallastContent.AttachmentName(0), 2));
        data.Add("completion meta", BallastContent.Meta(BallastContent.CompleteMeta, BallastContent.CompleteContent(3)));
        return data;
    }

    [Theory]
    [MemberData(nameof(PageBodies))]
    public void EveryGeneratedPageXml_IsXsdValid(string label, string body)
    {
        var errors = SchemaValidation.Validate(BallastContent.PageXml(PageId, body));
        Assert.True(errors.Count == 0, $"{label}: {string.Join(" / ", errors)}");
    }

    [Fact]
    public void Oversized_ReachesTarget_AndEndsWithCompletionMeta()
    {
        var updates = BallastContent.OversizedUpdates(1);
        Assert.True(updates.Sum(u => (long)u.Length) >= BallastContent.OversizedTargetChars);
        Assert.Equal(BallastContent.ParagraphsPerScale / BallastContent.ParagraphsPerOutline, updates.Count - 2);

        var last = XDocument.Parse(BallastContent.PageXml(PageId, updates[^1])).Root!;
        Assert.Equal(BallastContent.CompleteMeta, (string?)Assert.Single(last.Elements()).Attribute("name"));
        Assert.DoesNotContain(updates.Take(updates.Count - 1), u => u.Contains(BallastContent.CompleteMeta, StringComparison.Ordinal));

        var table = XDocument.Parse(BallastContent.PageXml(PageId, updates[^2])).Root!.Descendants(One + "Table").Single();
        Assert.Equal(BallastContent.TableRowsPerScale + 1, table.Elements(One + "Row").Count());

        var paragraphs = updates.Take(updates.Count - 2)
            .Sum(u => XDocument.Parse(BallastContent.PageXml(PageId, u)).Root!.Elements(One + "Outline").Descendants(One + "OE").Count());
        Assert.Equal(BallastContent.ParagraphsPerScale, paragraphs);
    }

    [Fact]
    public void Oversized_ScalesLinearly()
    {
        var one = BallastContent.OversizedUpdates(1).Sum(u => (long)u.Length);
        var two = BallastContent.OversizedUpdates(2).Sum(u => (long)u.Length);
        Assert.True(two >= 2 * BallastContent.OversizedTargetChars);
        Assert.True(two > one * 19 / 10);
    }

    [Fact]
    public void Png_HasSignatureIhdrIend_WithCorrectCrcs_AndInflatesToNoise()
    {
        var png = BallastContent.NoisePng(0);
        Assert.True(png.Length >= BallastContent.ImageTargetBytes, $"{png.Length} bytes");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);

        var chunks = Chunks(png);
        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Select(c => c.Type));
        Assert.All(chunks, c => Assert.True(c.CrcOk, $"{c.Type} CRC"));

        var ihdr = chunks[0].Data;
        Assert.Equal(13, ihdr.Length);
        Assert.Equal((uint)BallastContent.ImageSide, BinaryPrimitives.ReadUInt32BigEndian(ihdr));
        Assert.Equal((uint)BallastContent.ImageSide, BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(4)));
        Assert.Equal(new byte[] { 8, 2, 0, 0, 0 }, ihdr[8..]);
        Assert.Empty(chunks[2].Data);

        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(chunks[1].Data), CompressionMode.Decompress))
        {
            zlib.CopyTo(inflated);
        }

        var stride = (BallastContent.ImageSide * 3) + 1;
        var raw = inflated.ToArray();
        Assert.Equal(stride * BallastContent.ImageSide, raw.Length);
        Assert.All(Enumerable.Range(0, BallastContent.ImageSide), row => Assert.Equal(0, raw[row * stride]));
    }

    [Fact]
    public void Crc32_MatchesTheStandardCheckValue() =>
        Assert.Equal(0xCBF43926u, BallastContent.Crc32(Encoding.ASCII.GetBytes("123456789")));

    [Fact]
    public void Attachments_AreRandomAndSized()
    {
        var a = BallastContent.Attachment(0);
        Assert.Equal(BallastContent.AttachmentBytes, a.Length);
        Assert.NotEqual(a, BallastContent.Attachment(1));
        Assert.True(a.Distinct().Count() > 250);
    }

    [Fact]
    public void TreePlan_HasTheDesignedShape()
    {
        var plan = BallastContent.TreePlan(1);
        Assert.Equal(BallastContent.TreeDepth * BallastContent.SectionsPerLevel, plan.Count);
        Assert.Equal(500, plan.Sum(s => s.Pages.Count));
        Assert.Equal(100, plan.Sum(s => s.Pages.Count(p => p.Marked)));
        Assert.All(plan, s => Assert.Equal(s.Level, s.GroupPath.Count));
        Assert.All(plan, s => Assert.Equal(BallastContent.TreeGroupName, s.GroupPath[0]));
        Assert.Equal(plan.Count, plan.Select(s => s.Name).Distinct().Count());
        Assert.Equal(500, plan.SelectMany(s => s.Pages).Select(p => p.Title).Distinct().Count());

        Assert.Equal(1000, BallastContent.TreePlan(2).Sum(s => s.Pages.Count));
    }

    [Fact]
    public void Generation_IsDeterministic()
    {
        Assert.Equal(BallastContent.OversizedUpdates(1), BallastContent.OversizedUpdates(1));
        Assert.Equal(BallastContent.NoisePng(3), BallastContent.NoisePng(3));
        Assert.NotEqual(BallastContent.NoisePng(3), BallastContent.NoisePng(4));
        Assert.Equal(BallastContent.Attachment(2), BallastContent.Attachment(2));
        var page = BallastContent.TreePlan(1)[7].Pages[3];
        Assert.Equal(BallastContent.TreePageBody(page), BallastContent.TreePageBody(page));
        Assert.Equal(BallastContent.VaultPageBody(1), BallastContent.VaultPageBody(1));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("1", 1)]
    [InlineData("3", 3)]
    public void ParseScale_AcceptsPositiveIntegers(string? value, int expected) =>
        Assert.Equal(expected, BallastContent.ParseScale(value));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("two")]
    public void ParseScale_RejectsTheRest(string value) =>
        Assert.Throws<ArgumentException>(() => BallastContent.ParseScale(value));

    [Fact]
    public void CompletionContent_ReadsOnlyThePageLevelCompletionMeta()
    {
        var complete = BallastContent.PageXml(PageId, BallastContent.Meta(BallastContent.MarkMeta, "tree") + BallastContent.Meta(BallastContent.CompleteMeta, "v1"));
        Assert.Equal("v1", BallastFixture.CompletionContent(complete));
        Assert.Null(BallastFixture.CompletionContent(BallastContent.PageXml(PageId, BallastContent.Meta(BallastContent.MarkMeta, "tree"))));
    }

    /// <summary>
    /// The AC greps the harness output for every Ballast name, Meta name and vocabulary word. That grep only means
    /// something if none of them can appear there by accident: not in any string the harness or the server writes (keys,
    /// tool names, phases, codes), and not inside a hex ID.
    /// </summary>
    [Fact]
    public void NamesAndVocabulary_CollideWithNoHarnessOrServerLiteral()
    {
        var literals = new[] { "spikes/stress-harness", "src/OneNoteMcp.Core", "src/OneNoteMcp.Server" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(RepoRoot, dir), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => StringLiteral().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(literals);

        Assert.All(BallastContent.AllNames(1), name => Assert.StartsWith("ballast", name, StringComparison.OrdinalIgnoreCase));
        var terms = BallastContent.Vocabulary.Append("ballast").ToList();
        foreach (var term in terms)
        {
            var hit = literals.FirstOrDefault(l => l.Contains(term, StringComparison.OrdinalIgnoreCase));
            Assert.True(hit is null, $"'{term}' appears in the literal \"{hit}\"");
        }

        Assert.All(BallastContent.Vocabulary, w =>
        {
            Assert.True(w.Length >= 6, w);
            Assert.Matches("^[a-z]+$", w);
            Assert.Contains(w, c => c > 'f');
        });
        Assert.Equal(BallastContent.Vocabulary.Count, BallastContent.Vocabulary.Distinct().Count());
    }

    private sealed record Chunk(string Type, byte[] Data, bool CrcOk);

    private static List<Chunk> Chunks(byte[] png)
    {
        var chunks = new List<Chunk>();
        var at = 8;
        while (at < png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            var type = Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length).ToArray();
            var crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length));
            chunks.Add(new Chunk(type, data, crc == BallastContent.Crc32(png.AsSpan(at + 4, 4 + length))));
            at += 12 + length;
        }

        return chunks;
    }

    [GeneratedRegex("\"((?:[^\"\\\\\\r\\n]|\\\\.)*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex StringLiteral();
}
