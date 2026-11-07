using System.Globalization;
using System.Text;
using OneNoteMcp.Xml;
using Xunit;
using static OneNoteMcp.Tests.Envelope;

namespace OneNoteMcp.Tests;

/// <summary>AD-20's no-loss and size properties over many shapes and thresholds.</summary>
public sealed partial class LargeResultTests
{
    public static TheoryData<int, int, int, int> Shapes() => new()
    {
        // outlines, oes, hugeT, inlineMaxChars
        { 5, 50, 0, 20_000 },
        { 40, 5, 0, 20_000 },
        { 3, 300, 30_000, 25_000 },
        { 25, 25, 90_000, 40_000 },
        { 60, 10, 0, 100_000 },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task AnyShape_NoTextBlockOverTheThreshold_OffsetReadsReassemble_AndTheWalkReachesEveryText(int outlines, int oes, int hugeT, int inlineMax)
    {
        using var h = new LargeResultHarness(inlineMaxChars: inlineMax);
        h.Gateway.Basic = BigXml.Page(outlines, oes, hugeT);

        var root = Ok(await h.Page());
        if (!HasIndex(root))
        {
            Assert.Equal(h.Gateway.Basic, XmlOf(root));
            return;
        }

        var id = ResultId(root);
        Assert.Equal(h.Gateway.Basic, await ReadByOffsets(h, id, "/", h.Gateway.Basic.Length));
        var parts = await Walk(h, id, "/");
        Assert.All(parts, p => Assert.Contains(p, h.Gateway.Basic, StringComparison.Ordinal));
        Assert.Contains(parts, p => p.Contains("LAST TEXT OF THE PAGE", StringComparison.Ordinal));
        h.AssertNoBlockOverTheThreshold();
    }

    [Fact]
    public async Task ManyTinyOEs_WhereHashesOutweighText_StillFitAndReassemble()
    {
        // Each OE is ~40 characters, and its hash entry in meta.basis is ~90: the header, not the body, sets the size.
        var sb = new StringBuilder($"<?xml version=\"1.0\"?>\n<one:Page xmlns:one=\"{BigXml.Ns}\" ID=\"{{P}}\" lastModifiedTime=\"2026-11-06T00:00:00.000Z\"><one:Outline objectID=\"{{O}}\"><one:OEChildren>");
        for (var i = 0; i < 3_000; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<one:OE objectID=\"{{E{i}}}{{1}}{{B0}}\"/>");
        }

        sb.Append("<one:OE><one:T><![CDATA[LAST TEXT OF THE PAGE]]></one:T></one:OE></one:OEChildren></one:Outline></one:Page>");
        using var h = new LargeResultHarness();
        h.Gateway.Basic = sb.ToString();
        var expected = PageBasis.From(h.Gateway.Basic);

        var id = ResultId(Ok(await h.Page()));

        Assert.Equal(h.Gateway.Basic, await ReadByOffsets(h, id, "/", h.Gateway.Basic.Length));
        var parts = await Walk(h, id, "/");
        Assert.Contains(parts, p => p.Contains("LAST TEXT OF THE PAGE", StringComparison.Ordinal));

        // The runs' hashes together cover every OE, each from the piBasic basis.
        var children = Ok(await h.Read(id, expand: "{O}/0"));
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Entries(children).Where(e => e.TryGetProperty("run", out _)))
        {
            var run = entry.GetProperty("run").EnumerateArray().Select(x => x.GetString()!).ToArray();
            foreach (var hash in Ok(await h.Read(id, run: run)).GetProperty("meta").GetProperty("basis").GetProperty("elementHashes").EnumerateObject())
            {
                seen[hash.Name] = hash.Value.GetString()!;
            }
        }

        Assert.Equal(3_000, seen.Count);
        Assert.All(seen, kv => Assert.Equal(expected.ElementHashes[kv.Key], kv.Value));
        h.AssertNoBlockOverTheThreshold();
    }

    [Fact]
    public async Task XmlThatCantBeIndexed_IsStoredWhole_WithAnEmptyIndex_AndAWarning_NotInvalidXml()
    {
        // AD-20 no loss: a body the span scanner rejects is still delivered, by offset reads of /.
        using var h = new LargeResultHarness();
        var body = "<one:Page xmlns:one=\"" + BigXml.Ns + "\"><one:T>" + BigXml.Filler(50_000) + "</one:Page>";
        var delivery = new Core.LargeResults.ResultDelivery(h.Store, Server.EnvelopeMeasure.Instance);
        var success = new Core.Results.ToolSuccess(body, new Core.Results.ToolMeta("c"), [])
        {
            Source = new Core.Results.ResultSource(Core.Results.ResultKind.HierarchyXml),
        };

        var outcome = delivery.Deliver(success);

        Assert.NotNull(outcome.Stored);
        Assert.Null(outcome.Success.Xml);
        Assert.Empty(outcome.Success.Index!.Entries);
        Assert.Equal("/", outcome.Success.Index.Ref);
        Assert.Equal(body.Length, outcome.Success.Index.Chars);
        var warning = Assert.Single(outcome.Success.Warnings);
        Assert.Equal("resultNotIndexed", warning.Code);
        Assert.DoesNotContain("quick brown", warning.Message, StringComparison.Ordinal);

        Assert.Equal(body, await ReadByOffsets(h, outcome.Stored.ResultId, "/", body.Length));
        h.AssertNoBlockOverTheThreshold();
    }

    [Fact]
    public async Task OffsetReads_NeverSplitASurrogatePair()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Binary = string.Concat(Enumerable.Repeat("\U0001F600", 30_000)); // 60,000 UTF-16 chars, all pairs

        var id = ResultId(Ok(await h.BinaryRead()));
        var sb = new StringBuilder();
        while (sb.Length < h.Gateway.Binary.Length)
        {
            var part = Ok(await h.Read(id, "/", offset: sb.Length)).GetProperty("meta").GetProperty("binary").GetString()!;
            Assert.False(char.IsHighSurrogate(part[^1]));
            sb.Append(part);
        }

        Assert.Equal(h.Gateway.Binary, sb.ToString());
        h.AssertNoBlockOverTheThreshold();
    }
}
