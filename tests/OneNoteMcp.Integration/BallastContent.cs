using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Integration;

/// <summary>One section of the Ballast tree: its group path from the notebook down, its name, and its pages.</summary>
internal sealed record BallastTreeSection(int Level, IReadOnlyList<string> GroupPath, string Name, IReadOnlyList<BallastTreePage> Pages);

/// <summary>One tree page. <see cref="Marked"/> pages carry the <c>ballast-mark</c> Meta (every fifth).</summary>
internal sealed record BallastTreePage(string Title, bool Marked);

/// <summary>
/// Story 17's pure builders for the synthetic Ballast fixture: made-up text from a fixed vocabulary, partial
/// <c>UpdatePageContent</c> bodies, noise PNGs, random attachments and the tree plan. Everything is deterministic and
/// runs without OneNote; <see cref="BallastFixture"/> sends it through the guarded write. Nothing here is real content.
/// </summary>
internal static class BallastContent
{
    public const string ScaleVariable = "ONENOTE_MCP_BALLAST_SCALE";

    // Names. Every one starts with "Ballast" (or "ballast-" for Meta and files), which appears in no harness output key.
    public const string SectionName = "Ballast";
    public const string TreeGroupName = "Ballast Tree";
    public const string VaultSectionName = "Ballast Vault";
    public const string OversizedTitle = "Ballast Oversized";
    public const string ImagesTitle = "Ballast Images";
    public const string FilesTitle = "Ballast Files";
    public const string CompleteMeta = "ballast-complete";
    public const string MarkMeta = "ballast-mark";
    public const string OversizedMeta = "ballast-oversized";

    // Shape at scale 1 (the plan's design note 3).
    public const int TreeDepth = 5;
    public const int SectionsPerLevel = 4;
    public const int PagesPerSection = 25;
    public const int MarkEvery = 5;
    public const int ImagesPerScale = 8;
    public const int ImageSide = 512;
    public const int AttachmentsPerScale = 6;
    public const int AttachmentBytes = 512 * 1024;
    public const int ParagraphsPerScale = 4000;
    public const int ParagraphsPerOutline = 500;
    public const int TableRowsPerScale = 300;
    public const int TableColumns = 4;
    public const int VaultPages = 3;

    // Targets past spike 1.15's real maxima.
    public const int OversizedTargetChars = 1_000_000;
    public const int PagesScopeTargetChars = 100_000;
    public const int ImageTargetBytes = 500 * 1024;

    /// <summary>
    /// Made-up words, at least six letters, each with a letter outside a–f so none can match inside a hex ID. The
    /// harness output must never contain one (the AC's grep); <c>BallastContentTests</c> checks the harness's literals.
    /// </summary>
    public static readonly IReadOnlyList<string> Vocabulary =
    [
        "zorvane", "plimtrok", "quaskel", "vrindom", "thulmex", "krosvip", "gwarnel", "yuspith",
        "drovlak", "mirquon", "snevtor", "haxlune", "jorbisk", "plovenu", "rizthal", "wumbrek",
        "tolvisk", "qerndal", "fyshmor", "zelquin", "brontix", "kalvrum", "nipstoq", "vorhule",
        "glimzak", "stroyven", "oxplund", "trasmik", "hulvonz", "praznek", "skilvor", "muxtrel",
        "dwonsil", "yarplex", "chovrin", "lubzark", "fentrox", "grizlop", "quilbrex", "nozmerk",
        "tavlish", "rymbost", "plerzunk", "voskwyn", "jeltrum", "xandrop", "sputhvel", "krimolz",
    ];

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary><see cref="ScaleVariable"/>: unset or empty is 1; otherwise an integer ≥ 1.</summary>
    public static int ParseScale(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 1;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var scale) && scale >= 1
            ? scale
            : throw new ArgumentException($"{ScaleVariable} must be an integer ≥ 1.", nameof(value));
    }

    /// <summary>The completion Meta's content: pages whose size depends on the scale are rebuilt when it changes.</summary>
    public static string CompleteContent(int scale) => string.Create(CultureInfo.InvariantCulture, $"v1;scale={scale}");

    public const string CompleteContentUnscaled = "v1";

    // --- Page XML -------------------------------------------------------------------------------------------------

    /// <summary>A partial <c>UpdatePageContent</c> document: only the page ID and <paramref name="body"/>, no objectIDs.</summary>
    public static string PageXml(string pageId, string body) =>
        $"""<?xml version="1.0"?><one:Page xmlns:one="{OneNoteXml.Namespace2013}" ID="{Attr(pageId)}">{body}</one:Page>""";

    public static string Meta(string name, string content) => $"""<one:Meta name="{Attr(name)}" content="{Attr(content)}" />""";

    public static string Title(string title) => $"<one:Title><one:OE><one:T><![CDATA[{title}]]></one:T></one:OE></one:Title>";

    /// <summary>
    /// The oversized page's updates, in order: Meta + title + first outline, the other outlines, the table, and last
    /// the completion Meta. Together they're at least <see cref="OversizedTargetChars"/> per scale.
    /// </summary>
    public static IReadOnlyList<string> OversizedUpdates(int scale)
    {
        var rng = new BallastRng(Seed(OversizedTitle));
        var updates = new List<string>();
        var paragraphs = ParagraphsPerScale * scale;
        var y = 80;
        for (var start = 0; start < paragraphs; start += ParagraphsPerOutline)
        {
            var count = Math.Min(ParagraphsPerOutline, paragraphs - start);
            var body = new StringBuilder();
            if (start == 0)
            {
                body.Append(Meta(OversizedMeta, CompleteContent(scale))).Append(Title(OversizedTitle));
            }

            body.Append(OutlineStart(y, start / ParagraphsPerOutline));
            for (var i = 0; i < count; i++)
            {
                body.Append("<one:OE><one:T><![CDATA[").Append(Paragraph(rng, 36)).Append("]]></one:T></one:OE>");
            }

            body.Append(OutlineEnd);
            updates.Add(body.ToString());
            y += count * 40;
        }

        updates.Add(TableOutline(rng, TableRowsPerScale * scale, y, updates.Count));
        updates.Add(Meta(CompleteMeta, CompleteContent(scale)));
        return updates;
    }

    /// <summary>A tree page in one update: Meta (mark on every fifth, completion always), title, three paragraphs.</summary>
    public static string TreePageBody(BallastTreePage page)
    {
        var rng = new BallastRng(Seed(page.Title));
        var body = new StringBuilder();
        if (page.Marked)
        {
            body.Append(Meta(MarkMeta, "tree"));
        }

        body.Append(Meta(CompleteMeta, CompleteContentUnscaled)).Append(Title(page.Title)).Append(OutlineStart(80, 0));
        for (var i = 0; i < 3; i++)
        {
            body.Append("<one:OE><one:T><![CDATA[").Append(Paragraph(rng, 20)).Append("]]></one:T></one:OE>");
        }

        return body.Append(OutlineEnd).ToString();
    }

    public static string VaultPageTitle(int index) => string.Create(CultureInfo.InvariantCulture, $"Ballast Vault P{index + 1:00}");

    /// <summary>A vault page in one update: completion Meta, title, two paragraphs.</summary>
    public static string VaultPageBody(int index)
    {
        var title = VaultPageTitle(index);
        var rng = new BallastRng(Seed(title));
        return Meta(CompleteMeta, CompleteContentUnscaled) + Title(title) + OutlineStart(80, 0)
            + $"<one:OE><one:T><![CDATA[{Paragraph(rng, 24)}]]></one:T></one:OE>"
            + $"<one:OE><one:T><![CDATA[{Paragraph(rng, 24)}]]></one:T></one:OE>"
            + OutlineEnd;
    }

    /// <summary>The first update of the images or files page: its title only.</summary>
    public static string TitleBody(string title) => Title(title);

    /// <summary>One image in its own Outline, base64 inline.</summary>
    public static string ImageOutline(byte[] png, int index) =>
        OutlineStart(80 + (index * 300), index)
        + $"""<one:OE><one:Image format="png"><one:Size width="256" height="256" /><one:Data>{Convert.ToBase64String(png)}</one:Data></one:Image></one:OE>"""
        + OutlineEnd;

    /// <summary>One attachment in its own Outline. OneNote copies the file in, so the source may be deleted afterwards.</summary>
    public static string InsertedFileOutline(string path, string preferredName, int index) =>
        OutlineStart(80 + (index * 80), index)
        + $"""<one:OE><one:InsertedFile pathSource="{Attr(path)}" preferredName="{Attr(preferredName)}" /></one:OE>"""
        + OutlineEnd;

    public static string AttachmentName(int index) => string.Create(CultureInfo.InvariantCulture, $"ballast-file-{index + 1:00}.bin");

    // --- Binaries -------------------------------------------------------------------------------------------------

    /// <summary>Random bytes for attachment <paramref name="index"/>, the same on every run.</summary>
    public static byte[] Attachment(int index)
    {
        var bytes = new byte[AttachmentBytes];
        new BallastRng(Seed(AttachmentName(index))).Fill(bytes);
        return bytes;
    }

    /// <summary>Noise image <paramref name="index"/>: <see cref="ImageSide"/> square, RGB noise, so it doesn't compress.</summary>
    public static byte[] NoisePng(int index) =>
        Png(ImageSide, ImageSide, Seed(string.Create(CultureInfo.InvariantCulture, $"ballast-image-{index}")));

    /// <summary>A minimal PNG: signature, IHDR (8-bit RGB), one zlib IDAT of filter-0 rows of noise, IEND.</summary>
    public static byte[] Png(int width, int height, ulong seed)
    {
        var stride = (width * 3) + 1;
        var raw = new byte[stride * height];
        var rng = new BallastRng(seed);
        for (var row = 0; row < height; row++)
        {
            raw[row * stride] = 0; // filter type None
            rng.Fill(raw.AsSpan((row * stride) + 1, stride - 1));
        }

        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            compressed = buffer.ToArray();
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // colour type: RGB
        // compression, filter and interlace methods stay 0

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>The standard CRC-32 (IEEE, reflected, as PNG uses) over <paramref name="data"/>.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    // --- Plan -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The tree: <see cref="TreeDepth"/> nested groups, the first named <see cref="TreeGroupName"/>; each holds
    /// <see cref="SectionsPerLevel"/> sections of <see cref="PagesPerSection"/> × scale pages, every fifth marked.
    /// </summary>
    public static IReadOnlyList<BallastTreeSection> TreePlan(int scale)
    {
        var sections = new List<BallastTreeSection>();
        var path = new List<string>();
        for (var level = 1; level <= TreeDepth; level++)
        {
            path.Add(GroupName(level));
            for (var s = 1; s <= SectionsPerLevel; s++)
            {
                var name = string.Create(CultureInfo.InvariantCulture, $"Ballast L{level} S{s}");
                var pages = Enumerable.Range(1, PagesPerSection * scale)
                    .Select(p => new BallastTreePage(string.Create(CultureInfo.InvariantCulture, $"{name} P{p:000}"), p % MarkEvery == 0))
                    .ToList();
                sections.Add(new BallastTreeSection(level, [.. path], name, pages));
            }
        }

        return sections;
    }

    public static string GroupName(int level) =>
        level == 1 ? TreeGroupName : string.Create(CultureInfo.InvariantCulture, $"{TreeGroupName} L{level}");

    /// <summary>Every name the fixture gives a section, group, page, Meta or file, for the AC's grep.</summary>
    public static IEnumerable<string> AllNames(int scale)
    {
        yield return SectionName;
        yield return TreeGroupName;
        yield return VaultSectionName;
        yield return OversizedTitle;
        yield return ImagesTitle;
        yield return FilesTitle;
        yield return CompleteMeta;
        yield return MarkMeta;
        yield return OversizedMeta;
        for (var level = 1; level <= TreeDepth; level++)
        {
            yield return GroupName(level);
        }

        foreach (var section in TreePlan(scale))
        {
            yield return section.Name;
            foreach (var page in section.Pages)
            {
                yield return page.Title;
            }
        }

        for (var i = 0; i < VaultPages; i++)
        {
            yield return VaultPageTitle(i);
        }

        for (var i = 0; i < AttachmentsPerScale * scale; i++)
        {
            yield return AttachmentName(i);
        }
    }

    // --- Text -----------------------------------------------------------------------------------------------------

    /// <summary>A paragraph of <paramref name="words"/> vocabulary words: capitalised, comma every seventh, full stop.</summary>
    public static string Paragraph(BallastRng rng, int words)
    {
        var text = new StringBuilder();
        for (var i = 0; i < words; i++)
        {
            var word = Vocabulary[rng.Next(Vocabulary.Count)];
            if (i == 0)
            {
                text.Append(char.ToUpperInvariant(word[0])).Append(word, 1, word.Length - 1);
            }
            else
            {
                text.Append(i % 7 == 0 ? ", " : " ").Append(word);
            }
        }

        return text.Append('.').ToString();
    }

    /// <summary>A stable 64-bit seed from a name (FNV-1a), unlike <see cref="string.GetHashCode()"/>.</summary>
    public static ulong Seed(string name)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in name)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return hash;
    }

    private static string OutlineStart(int y, int z) =>
        string.Create(CultureInfo.InvariantCulture, $"""<one:Outline><one:Position x="36" y="{y}" z="{z}" /><one:OEChildren>""");

    private const string OutlineEnd = "</one:OEChildren></one:Outline>";

    private static string TableOutline(BallastRng rng, int rows, int y, int z)
    {
        var table = new StringBuilder(OutlineStart(y, z));
        table.Append("""<one:OE><one:Table bordersVisible="true" hasHeaderRow="true"><one:Columns>""");
        for (var c = 0; c < TableColumns; c++)
        {
            table.Append(CultureInfo.InvariantCulture, $"""<one:Column index="{c}" width="140" />""");
        }

        table.Append("</one:Columns>");
        for (var r = 0; r <= rows; r++)
        {
            table.Append("<one:Row>");
            for (var c = 0; c < TableColumns; c++)
            {
                var text = r == 0 ? Vocabulary[rng.Next(Vocabulary.Count)] : Paragraph(rng, 6);
                table.Append("<one:Cell><one:OEChildren><one:OE><one:T><![CDATA[").Append(text).Append("]]></one:T></one:OE></one:OEChildren></one:Cell>");
            }

            table.Append("</one:Row>");
        }

        return table.Append("</one:Table></one:OE>").Append(OutlineEnd).ToString();
    }

    private static string Attr(string value) => SecurityElement.Escape(value);

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        Encoding.ASCII.GetBytes(type, header[4..]);
        stream.Write(header);
        stream.Write(data);

        var crcInput = new byte[4 + data.Length];
        header[4..].CopyTo(crcInput);
        data.CopyTo(crcInput.AsSpan(4));
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        stream.Write(crc);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}

/// <summary>xorshift64*: small, fast and the same on every machine and runtime, which <see cref="Random"/> doesn't promise.</summary>
internal sealed class BallastRng(ulong seed)
{
    private ulong _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    public ulong NextUInt64()
    {
        _state ^= _state >> 12;
        _state ^= _state << 25;
        _state ^= _state >> 27;
        return _state * 2685821657736338717UL;
    }

    public int Next(int maxExclusive) => (int)(NextUInt64() % (ulong)maxExclusive);

    public void Fill(Span<byte> bytes)
    {
        var i = 0;
        while (i < bytes.Length)
        {
            var value = NextUInt64();
            for (var k = 0; k < 8 && i < bytes.Length; k++, i++)
            {
                bytes[i] = (byte)(value >> (8 * k));
            }
        }
    }
}
