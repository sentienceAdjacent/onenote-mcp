using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OneNoteMcp.Core;
using OneNoteMcp.Server;
using OneNoteMcp.Xml;
using Xunit;
using static OneNoteMcp.Tests.Envelope;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 1.18's I/O matrix (AD-9 text block, AD-20 large-result delivery), through the real runner, store and tools.
/// Most tests run at inlineMaxChars 20,000 (the lowest the settings allow) so the fixtures stay small.
/// </summary>
public sealed partial class LargeResultTests
{
    // --- Small result -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task SmallPage_IsReturnedWhole_HeaderLineThenBody_WithTheFullBasis()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Basic = BigXml.Page(outlines: 2, oes: 3);

        var result = await h.Page();
        var root = Ok(result);

        Assert.Equal(h.Gateway.Basic, XmlOf(root));
        Assert.False(HasIndex(root));
        Assert.False(root.GetProperty("meta").TryGetProperty("result", out _));
        Assert.True(root.GetProperty("meta").GetProperty("basis").TryGetProperty("elementHashes", out _));
        var header = TextBlockAssert.Header(result);
        Assert.False(header.ContainsKey("xml"));
        Assert.NotNull(header["meta"]!["basis"]!["elementHashes"]);
        Assert.Equal(0, h.Store.Count);
    }

    [Fact]
    public async Task Binary_TextBlockBody_IsMetaBinary_AndTheHeaderLeavesItOut()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Binary = "iVBORw0KGgo=";

        var result = await h.BinaryRead();

        var text = TextBlockAssert.Matches(result);
        Assert.EndsWith("\niVBORw0KGgo=", text, StringComparison.Ordinal);
        Assert.Null(TextBlockAssert.Header(result)["meta"]!["binary"]);
    }

    [Fact]
    public void Error_TextBlock_IsTheWholeEnvelope()
    {
        var envelope = ToolEnvelope.Failure(
            Core.Results.ToolErrors.From(new OneNoteException(ErrorCode.ResultExpired, "gone"), OperationClass.Read),
            new Core.Results.ToolMeta("c"));

        var result = ToolRunner.ToResult(envelope);

        Assert.Equal(result.StructuredContent!.Value.GetRawText(), TextBlockAssert.Matches(result));
    }

    [Fact]
    public async Task ExactlyAtTheThreshold_IsReturnedWhole_OneOverIsStored()
    {
        using var h = new LargeResultHarness(inlineMaxChars: 380_000);
        h.Gateway.Hierarchy = BigXml.Hierarchy(sections: 4, pages: 60);
        var size = TextBlockAssert.Matches(await h.Hierarchy()).Length;
        Assert.True(size > 20_000, "the fixture must be over the lowest threshold");

        h.Settings = h.Settings with { InlineMaxChars = size };
        Assert.False(HasIndex(Ok(await h.Hierarchy())));
        Assert.Equal(0, h.Store.Count);

        h.Settings = h.Settings with { InlineMaxChars = size - 1 };
        Assert.True(HasIndex(Ok(await h.Hierarchy())));
        Assert.Equal(1, h.Store.Count);
    }

    // --- Oversized page ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task OversizedPage_ReturnsMetaResultAndTheFirstIndex_BasisWithoutElementHashes()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Basic = BigXml.Page(outlines: 12, oes: 20, hugeT: 45_000);

        var root = Ok(await h.Page());

        Assert.Null(XmlOf(root));
        var meta = root.GetProperty("meta");
        var result = meta.GetProperty("result");
        Assert.Matches("^[0-9a-f]{32}$", result.GetProperty("resultId").GetString());
        Assert.Equal(h.Gateway.Basic.Length, result.GetProperty("totalChars").GetInt32());
        Assert.Equal("2026-11-06T12:30:00.000Z", result.GetProperty("expiresAt").GetString());
        Assert.Equal(BigXml.Ns, result.GetProperty("namespaces").GetProperty("xmlns:one").GetString());
        Assert.Equal(1, result.GetProperty("pages").GetInt32());

        var basis = meta.GetProperty("basis");
        var expected = PageBasis.From(h.Gateway.Basic);
        Assert.Equal(expected.PageHash, basis.GetProperty("pageHash").GetString());
        Assert.Equal(expected.LastModifiedTime, basis.GetProperty("lastModifiedTime").GetString());
        Assert.False(basis.TryGetProperty("elementHashes", out _));

        var index = Index(root);
        Assert.Equal("/", index.GetProperty("ref").GetString());
        Assert.Equal("one:Page", index.GetProperty("element").GetString());
        Assert.Equal(h.Gateway.Basic.Length, index.GetProperty("chars").GetInt32());
        Assert.Equal(0, index.GetProperty("page").GetInt32());
        h.AssertNoBlockOverTheThreshold();
    }

    [Fact]
    public async Task FirstIndex_IsPageZeroOfExpandRoot()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Basic = BigXml.Page(outlines: 12, oes: 20, hugeT: 45_000);

        var first = Ok(await h.Page());
        var expand = Ok(await h.Read(ResultId(first), expand: "/"));

        Assert.Equal(Index(first).GetRawText(), Index(expand).GetRawText());
        Assert.Equal(first.GetProperty("meta").GetProperty("result").GetProperty("pages").GetInt32(), expand.GetProperty("meta").GetProperty("result").GetProperty("pages").GetInt32());
    }

    [Fact]
    public async Task OversizedPage_IndexEntries_RefsElementsCharsChildrenContainsLabels()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Basic = BigXml.Page(outlines: 12, oes: 20, hugeT: 45_000);
        var tree = ElementSpans.Page(h.Gateway.Basic);

        var root = Ok(await h.Page());
        var entries = Entries(root).ToList();

        // Every child of one:Page is covered once, in document order, by an element entry or a run.
        var covered = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.TryGetProperty("ref", out var r))
            {
                var node = tree.Find(r.GetString()!)!;
                covered.Add(node.Ref);
                Assert.Equal(node.Element, entry.GetProperty("element").GetString());
                Assert.Equal(node.Length, entry.GetProperty("chars").GetInt32());
                Assert.Equal(node.Children.Count, entry.GetProperty("children").GetInt32());
            }
            else
            {
                var run = entry.GetProperty("run").EnumerateArray().Select(x => x.GetString()!).ToList();
                var first = tree.Find(run[0])!;
                var last = tree.Find(run[1])!;
                var count = entry.GetProperty("count").GetInt32();
                Assert.Equal(last.IndexInParent - first.IndexInParent + 1, count);
                Assert.Equal(last.End - first.Start, entry.GetProperty("chars").GetInt32());
                covered.AddRange(first.Parent!.Children.Skip(first.IndexInParent).Take(count).Select(n => n.Ref));
            }
        }

        Assert.Equal(tree.Root.Children.Select(n => n.Ref), covered);

        // The huge Outline is its own entry, labelled with its text; the table Outline counts its table.
        var huge = entries.Single(e => e.TryGetProperty("ref", out var r) && r.GetString() == "{H}{1}{B0}");
        Assert.StartsWith("L000000 the quick brown fox", huge.GetProperty("label").GetString(), StringComparison.Ordinal);
        Assert.Equal(80, huge.GetProperty("label").GetString()!.Length);
        h.AssertNoBlockOverTheThreshold();
    }

    [Fact]
    public async Task OversizedPage_ContainsAndLabel_FollowTheTextRule()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Basic = BigXml.Page(outlines: 12, oes: 20, hugeT: 45_000);
        var root = Ok(await h.Page());
        var id = ResultId(root);

        // Any ref can be expanded, not only those listed so far. The table Outline's only child is its one:OEChildren,
        // which has no objectID, so its ref is positional.
        var outline = Ok(await h.Read(id, expand: "{TB}{1}{B0}"));
        var children = Assert.Single(Entries(outline));
        Assert.Equal("{TB}{1}{B0}/0", children.GetProperty("ref").GetString());
        Assert.Equal("one:OEChildren", children.GetProperty("element").GetString());
        Assert.Equal(1, children.GetProperty("contains").GetProperty("table").GetInt32());
        Assert.False(children.GetProperty("contains").TryGetProperty("image", out _));
        Assert.Equal("cell text", children.GetProperty("label").GetString());

        // A small Outline's one:Position and one:OEChildren fit together, so they are one run.
        var first = Ok(await h.Read(id, expand: "{O0}{1}{B0}"));
        var run = Assert.Single(Entries(first));
        Assert.Equal(["{O0}{1}{B0}/0", "{O0}{1}{B0}/1"], run.GetProperty("run").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(2, run.GetProperty("count").GetInt32());
        Assert.False(run.TryGetProperty("firstLabel", out _)); // one:Position has no text
        Assert.StartsWith("Outline 0 line 0 lorem ipsum", run.GetProperty("lastLabel").GetString(), StringComparison.Ordinal);

        var root2 = Ok(await h.Read(id, expand: "/"));
        var image = Entries(root2).Where(e => e.TryGetProperty("ref", out _)).Select(e => e.GetProperty("ref").GetString()).ToList();
        Assert.Contains("{H}{1}{B0}", image);
    }

    [Fact]
    public async Task OversizedPage_ConcatenatedOffsetReadsOfRoot_ReproduceTheBodyExactly()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Basic = BigXml.Page(outlines: 12, oes: 20, hugeT: 45_000, newline: "\r\n");
        var id = ResultId(Ok(await h.Page()));

        var body = await ReadByOffsets(h, id, "/", h.Gateway.Basic.Length);

        Assert.Equal(h.Gateway.Basic, body);
        h.AssertNoBlockOverTheThreshold();
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(7_777)]
    public async Task OffsetReads_WithAnExplicitLength_StillReassembleExactly(int length)
    {
        using var h = new LargeResultHarness();
        h.Gateway.Basic = BigXml.Page(outlines: 12, oes: 20, hugeT: 45_000);
        var id = ResultId(Ok(await h.Page()));

        var sb = new StringBuilder();
        while (sb.Length < h.Gateway.Basic.Length)
        {
            var part = XmlOf(Ok(await h.Read(id, "/", offset: sb.Length, length: length)))!;
            Assert.True(part.Length <= length);
            sb.Append(part);
        }

        Assert.Equal(h.Gateway.Basic, sb.ToString());
    }

    [Fact]
    public async Task OversizedPage_WalkingTheIndex_ReachesEveryTextIncludingTheLast()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Basic = BigXml.Page(outlines: 12, oes: 20, hugeT: 45_000);
        var id = ResultId(Ok(await h.Page()));

        var parts = await Walk(h, id, "/");

        foreach (var part in parts)
        {
            Assert.Contains(part, h.Gateway.Basic, StringComparison.Ordinal); // exact substrings, never re-serialised
        }

        foreach (Match t in OneT().Matches(h.Gateway.Basic))
        {
            Assert.Contains(parts, p => p.Contains(t.Value, StringComparison.Ordinal));
        }

        Assert.Contains(parts, p => p.Contains("LAST TEXT OF THE PAGE", StringComparison.Ordinal));
        h.AssertNoBlockOverTheThreshold();
    }

    [Fact]
    public async Task Run_ReturnsTheSiblingsSpanUnchanged_WithTheirHashes()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Basic = BigXml.Page(outlines: 1, oes: 400);
        var tree = ElementSpans.Page(h.Gateway.Basic);
        var expected = PageBasis.From(h.Gateway.Basic);
        var id = ResultId(Ok(await h.Page()));

        var children = Ok(await h.Read(id, expand: "{O0}{1}{B0}/1"));
        var run = Entries(children).First(e => e.TryGetProperty("run", out _));
        var refs = run.GetProperty("run").EnumerateArray().Select(x => x.GetString()!).ToArray();

        var part = Ok(await h.Read(id, run: refs));

        var first = tree.Find(refs[0])!;
        var last = tree.Find(refs[1])!;
        Assert.Equal(h.Gateway.Basic[first.Start..last.End], XmlOf(part));
        var hashes = part.GetProperty("meta").GetProperty("basis").GetProperty("elementHashes");
        var members = first.Parent!.Children.Skip(first.IndexInParent).Take(last.IndexInParent - first.IndexInParent + 1).ToList();
        Assert.Equal(members.Count, hashes.EnumerateObject().Count());
        foreach (var member in members)
        {
            Assert.Equal(expected.ElementHashes[member.Ref], hashes.GetProperty(member.Ref).GetString());
        }

        Assert.Equal(expected.PageHash, part.GetProperty("meta").GetProperty("basis").GetProperty("pageHash").GetString());
        Assert.Equal(id, ResultId(part));
        h.AssertNoBlockOverTheThreshold();
    }

    [Fact]
    public async Task PartHashes_ComeFromPiBasic_EvenWhenTheBodyIsPiBinaryData()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Basic = BigXml.Page(outlines: 12, oes: 20);
        h.Gateway.BinaryData = BigXml.Page(outlines: 12, oes: 20, binary: true);
        var expected = PageBasis.From(h.Gateway.Basic);

        var root = Ok(await h.Page(includeBinary: true));
        var id = ResultId(root);
        var part = Ok(await h.Read(id, "{O3}{1}{B0}"));

        Assert.Contains("<one:Outline objectID=\"{O3}{1}{B0}\"", XmlOf(part), StringComparison.Ordinal);
        var hashes = part.GetProperty("meta").GetProperty("basis").GetProperty("elementHashes");
        Assert.Equal(expected.ElementHashes["{O3}{1}{B0}"], hashes.GetProperty("{O3}{1}{B0}").GetString());
        Assert.Equal(21, hashes.EnumerateObject().Count()); // the Outline and its 20 OEs
        Assert.Equal(expected.PageHash, root.GetProperty("meta").GetProperty("basis").GetProperty("pageHash").GetString());
    }

    [Fact]
    public async Task ExpandOfAnOE_CarriesThatElementsOwnHash()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Basic = BigXml.Page(outlines: 12, oes: 20, hugeT: 45_000);
        var expected = PageBasis.From(h.Gateway.Basic);
        var id = ResultId(Ok(await h.Page()));

        var expand = Ok(await h.Read(id, expand: "{HOE}{1}{B0}"));

        var hashes = expand.GetProperty("meta").GetProperty("basis").GetProperty("elementHashes");
        Assert.Equal(expected.ElementHashes["{HOE}{1}{B0}"], Assert.Single(hashes.EnumerateObject()).Value.GetString());
    }

    // --- Oversized hierarchy ----------------------------------------------------------------------------------------

    [Fact]
    public async Task OversizedHierarchy_IsLabelledByName_RefsAreIds()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Hierarchy = BigXml.Hierarchy(sections: 6, pages: 150);

        var root = Ok(await h.Hierarchy());
        var notebook = Assert.Single(Entries(root));
        Assert.Equal("{NB}{1}{B0}", notebook.GetProperty("ref").GetString());
        Assert.Equal("Notebook A", notebook.GetProperty("label").GetString());
        Assert.False(root.GetProperty("meta").TryGetProperty("basis", out _));

        var sections = Entries(Ok(await h.Read(ResultId(root), expand: "{NB}{1}{B0}"))).ToList();
        Assert.Equal(6, sections.Count);
        for (var s = 0; s < 6; s++)
        {
            Assert.Equal($"{{S{s}}}{{1}}{{B0}}", sections[s].GetProperty("ref").GetString());
            Assert.StartsWith($"Section {s}", sections[s].GetProperty("label").GetString(), StringComparison.Ordinal);
        }

        var parts = await Walk(h, ResultId(root), "/");
        for (var s = 0; s < 6; s++)
        {
            for (var p = 0; p < 150; p++)
            {
                var pageId = $"ID=\"{{S{s}P{p}}}{{1}}{{E1}}\"";
                Assert.Contains(parts, part => part.Contains(pageId, StringComparison.Ordinal));
            }
        }

        h.AssertNoBlockOverTheThreshold();
    }

    [Fact]
    public async Task ManyLargeSiblings_IndexIsPaged_EveryPageFits_AndThePagesCoverEverySibling()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Hierarchy = BigXml.Hierarchy(sections: 60, pages: 70, nameChars: 300);

        var root = Ok(await h.Hierarchy());
        var id = ResultId(root);
        var first = Ok(await h.Read(id, expand: "{NB}{1}{B0}"));
        var pages = first.GetProperty("meta").GetProperty("result").GetProperty("pages").GetInt32();
        Assert.True(pages > 1, "the fixture must need more than one index page");

        var refs = new List<string>();
        for (var page = 0; page < pages; page++)
        {
            var response = Ok(await h.Read(id, expand: "{NB}{1}{B0}", page: page));
            Assert.Equal(page, Index(response).GetProperty("page").GetInt32());
            refs.AddRange(Entries(response).Select(e => e.GetProperty("ref").GetString()!));
        }

        Assert.Equal(Enumerable.Range(0, 60).Select(s => $"{{S{s}}}{{1}}{{B0}}"), refs);
        h.AssertNoBlockOverTheThreshold();

        var past = Error(await h.Read(id, expand: "{NB}{1}{B0}", page: pages));
        Assert.Equal("InvalidQuery", past.GetProperty("code").GetString());
    }

    // --- Oversized single element -----------------------------------------------------------------------------------

    [Fact]
    public async Task HugeLeaf_RefReturnsItsEmptyIndex_OffsetReadsCoverIt_PastTheEndIsInvalidQuery()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Basic = BigXml.Page(outlines: 1, oes: 2, hugeT: 60_000);
        var tree = ElementSpans.Page(h.Gateway.Basic);
        var t = tree.Find("{HOE}{1}{B0}")!.Children.Single();
        Assert.Equal("{HOE}{1}{B0}/0", t.Ref);
        var id = ResultId(Ok(await h.Page()));

        var asRef = Ok(await h.Read(id, t.Ref));
        Assert.Null(XmlOf(asRef));
        Assert.Empty(Entries(asRef));
        Assert.Equal(t.Length, Index(asRef).GetProperty("chars").GetInt32());
        Assert.Equal("one:T", Index(asRef).GetProperty("element").GetString());

        Assert.Equal(h.Gateway.Basic[t.Start..t.End], await ReadByOffsets(h, id, t.Ref, t.Length));

        var past = Error(await h.Read(id, t.Ref, offset: t.Length));
        Assert.Equal("InvalidQuery", past.GetProperty("code").GetString());
        Assert.False(past.GetProperty("retryable").GetBoolean());
        h.AssertNoBlockOverTheThreshold();
    }

    [Fact]
    public async Task OversizedBinary_IndexIsEmpty_OffsetReadsOfRootReturnTheBase64InMetaBinary()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Binary = Convert.ToBase64String(Enumerable.Range(0, 60_000).Select(i => (byte)i).ToArray());

        var root = Ok(await h.BinaryRead());
        Assert.False(root.GetProperty("meta").TryGetProperty("binary", out _));
        Assert.Empty(Entries(root));
        Assert.Empty(root.GetProperty("meta").GetProperty("result").GetProperty("namespaces").EnumerateObject());
        var id = ResultId(root);

        var sb = new StringBuilder();
        while (sb.Length < h.Gateway.Binary.Length)
        {
            var part = Ok(await h.Read(id, "/", offset: sb.Length));
            Assert.Null(XmlOf(part));
            sb.Append(part.GetProperty("meta").GetProperty("binary").GetString());
        }

        Assert.Equal(h.Gateway.Binary, sb.ToString());
        h.AssertNoBlockOverTheThreshold();
    }

    // --- Expired, unknown, bad ref, bad modes ---------------------------------------------------------------------

    [Fact]
    public async Task UnreadForTtl_IsResultExpired_WithRepeatTheOriginalCall()
    {
        using var h = new LargeResultHarness(ttlMinutes: 30);
        h.Gateway.Basic = BigXml.Page(outlines: 12, oes: 20, hugeT: 45_000);
        var id = ResultId(Ok(await h.Page()));

        h.Clock.Advance(TimeSpan.FromMinutes(20));
        Ok(await h.Read(id, expand: "/"));
        h.Clock.Advance(TimeSpan.FromMinutes(20)); // 40 minutes after the call, 20 after the last read
        var still = Ok(await h.Read(id, expand: "/"));
        Assert.Equal("2026-11-06T13:10:00.000Z", still.GetProperty("meta").GetProperty("result").GetProperty("expiresAt").GetString());

        h.Clock.Advance(TimeSpan.FromMinutes(30));
        var error = Error(await h.Read(id, expand: "/"));

        Assert.Equal("ResultExpired", error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal("Repeat the original call to get a new resultId.", error.GetProperty("userAction").GetString());
        Assert.Equal(0, h.Store.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00000000000000000000000000000000")]
    public async Task UnknownResultId_IsResultExpired(string resultId)
    {
        using var h = new LargeResultHarness();

        var error = Error(await h.Read(resultId, expand: "/"));

        Assert.Equal("ResultExpired", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task EvictedPastMaxStore_IsResultExpired()
    {
        // 16 MB holds 8,388,608 UTF-16 chars; two 3M-char binaries fit, a third evicts the least recently read.
        using var h = new LargeResultHarness(maxStoreMB: 16);
        h.Gateway.Binary = new string('A', 3_000_000);
        var a = ResultId(Ok(await h.BinaryRead()));
        var b = ResultId(Ok(await h.BinaryRead()));
        Ok(await h.Read(a, "/", offset: 0)); // a is now read more recently than b
        var c = ResultId(Ok(await h.BinaryRead()));

        Assert.Equal("ResultExpired", Error(await h.Read(b, "/", offset: 0)).GetProperty("code").GetString());
        Ok(await h.Read(a, "/", offset: 0));
        Ok(await h.Read(c, "/", offset: 0));
    }

    [Fact]
    public async Task BadRef_IsUnknownObjectId()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Basic = BigXml.Page(outlines: 12, oes: 20, hugeT: 45_000);
        var id = ResultId(Ok(await h.Page()));

        foreach (var call in new Func<Task<ModelContextProtocol.Protocol.CallToolResult>>[]
        {
            () => h.Read(id, "{nope}"),
            () => h.Read(id, expand: "{nope}"),
            () => h.Read(id, run: ["{O0}{1}{B0}", "{nope}"]),
            () => h.Read(id, "{nope}", offset: 0),
        })
        {
            var error = Error(await call());
            Assert.Equal("UnknownObjectId", error.GetProperty("code").GetString());
            Assert.DoesNotContain("lorem", error.GetRawText(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("", null, "", null, null, null)] // no mode
    [InlineData("/", null, "/", null, null, null)] // ref and expand
    [InlineData("/", "a|b", "", null, null, null)] // ref and run
    [InlineData("", null, "/", null, 0, null)] // offset without ref
    [InlineData("/", null, "", null, null, 10)] // length without offset
    [InlineData("/", null, "", 0, null, null)] // page with ref
    [InlineData("", "a", "", null, null, null)] // run of one
    [InlineData("/", null, "", null, -1, null)] // negative offset
    [InlineData("/", null, "", null, 0, 0)] // zero length
    public async Task BadModes_AreInvalidQuery(string @ref, string? run, string expand, int? page, int? offset, int? length)
    {
        using var h = new LargeResultHarness();
        h.Gateway.Basic = BigXml.Page(outlines: 12, oes: 20, hugeT: 45_000);
        var id = ResultId(Ok(await h.Page()));

        var error = Error(await h.Read(id, @ref, run?.Split('|'), expand, page, offset, length));

        Assert.Equal("InvalidQuery", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RunOfNonSiblings_IsInvalidQuery()
    {
        using var h = new LargeResultHarness();
        h.Gateway.Basic = BigXml.Page(outlines: 12, oes: 20, hugeT: 45_000);
        var id = ResultId(Ok(await h.Page()));

        var error = Error(await h.Read(id, run: ["{O0}{1}{B0}", "{O1E0}{1}{B0}"]));
        Assert.Equal("InvalidQuery", error.GetProperty("code").GetString());

        var backwards = Error(await h.Read(id, run: ["{O1}{1}{B0}", "{O0}{1}{B0}"]));
        Assert.Equal("InvalidQuery", backwards.GetProperty("code").GetString());
    }

    // --- Settings and logging ---------------------------------------------------------------------------------------

    [Fact]
    public async Task LoweringTheThresholdBetweenCalls_StillNeverReturnsABlockOverIt()
    {
        using var h = new LargeResultHarness(inlineMaxChars: 60_000);
        h.Gateway.Basic = BigXml.Page(outlines: 30, oes: 20, hugeT: 45_000);
        var id = ResultId(Ok(await h.Page()));
        var runs = Entries(Ok(await h.Read(id, expand: "/")))
            .Where(e => e.TryGetProperty("run", out _))
            .Select(e => e.GetProperty("run").EnumerateArray().Select(x => x.GetString()!).ToArray())
            .ToList();
        Assert.NotEmpty(runs);

        h.Settings = h.Settings with { InlineMaxChars = 20_000 };
        foreach (var run in runs)
        {
            var response = Ok(await h.Read(id, run: run));
            if (HasIndex(response))
            {
                Assert.NotEmpty(Entries(response));
                Assert.Equal(run, Index(response).GetProperty("run").EnumerateArray().Select(x => x.GetString()!));
            }
        }

        h.AssertNoBlockOverTheThreshold();
    }

    [Fact]
    public async Task StoringAResult_LogsItsIdAndSizes_NeverItsContent()
    {
        var logger = new ListLogger();
        using var h = new LargeResultHarness(logger: logger);
        h.Gateway.Basic = BigXml.Page(outlines: 12, oes: 20, hugeT: 45_000);

        var id = ResultId(Ok(await h.Page()));

        var stored = Assert.Single(logger.Lines, l => l.StartsWith("result stored", StringComparison.Ordinal));
        Assert.Contains($"resultId={id}", stored, StringComparison.Ordinal);
        Assert.Contains($"totalChars={h.Gateway.Basic.Length}", stored, StringComparison.Ordinal);
        Assert.Equal(h.Store.TotalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), Regex.Match(stored, "storeBytes=([0-9]+)").Groups[1].Value);
        Assert.True(h.Store.TotalBytes > h.Gateway.Basic.Length * 2L, "the span tree is counted on top of the body");
        Assert.All(logger.Lines, l => Assert.DoesNotContain("lorem", l, StringComparison.Ordinal));
    }

    // --- helpers ----------------------------------------------------------------------------------------------------

    private static async Task<string> ReadByOffsets(LargeResultHarness h, string id, string @ref, int chars)
    {
        var sb = new StringBuilder();
        while (sb.Length < chars)
        {
            var part = XmlOf(Ok(await h.Read(id, @ref, offset: sb.Length)))!;
            Assert.NotEmpty(part);
            sb.Append(part);
        }

        Assert.Equal(chars, sb.Length);
        return sb.ToString();
    }

    /// <summary>Reads every part of <paramref name="ref"/>'s subtree the way a model would: by ref and run, expanding what doesn't fit, and by offset for a huge leaf.</summary>
    private static async Task<List<string>> Walk(LargeResultHarness h, string id, string @ref)
    {
        var parts = new List<string>();
        var expand = Ok(await h.Read(id, expand: @ref));
        var pages = expand.GetProperty("meta").GetProperty("result").GetProperty("pages").GetInt32();
        var entries = new List<JsonElement>(Entries(expand));
        for (var page = 1; page < pages; page++)
        {
            entries.AddRange(Entries(Ok(await h.Read(id, expand: @ref, page: page))));
        }

        if (entries.Count == 0)
        {
            parts.Add(await ReadByOffsets(h, id, @ref, Index(expand).GetProperty("chars").GetInt32()));
            return parts;
        }

        foreach (var entry in entries)
        {
            if (entry.TryGetProperty("ref", out var r))
            {
                var response = Ok(await h.Read(id, r.GetString()!));
                if (XmlOf(response) is { } xml)
                {
                    parts.Add(xml);
                }
                else
                {
                    parts.AddRange(await Walk(h, id, r.GetString()!));
                }
            }
            else
            {
                var run = entry.GetProperty("run").EnumerateArray().Select(x => x.GetString()!).ToArray();
                var response = Ok(await h.Read(id, run: run));
                Assert.NotNull(XmlOf(response));
                parts.Add(XmlOf(response)!);
            }
        }

        return parts;
    }

    [GeneratedRegex("<one:T>.*?</one:T>", RegexOptions.Singleline)]
    private static partial Regex OneT();

    private sealed class ListLogger : ILogger<ToolRunner>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
