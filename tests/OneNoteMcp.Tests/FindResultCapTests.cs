using System.Globalization;
using System.Text.Json;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Backlog 15: a stored <c>find_pages</c> / <c>find_meta</c> result's first text block went over
/// <c>results.inlineMaxChars</c> on MCP Test (29 protected sections; 16 blocks over, the largest 102,870). These build the
/// search success the way <c>SearchResults.Result</c> does (body, <c>meta.filtered</c>, <c>meta.withheld</c> and the
/// withheld-section warning) and deliver it through the real <see cref="ResultDelivery"/> and <see cref="EnvelopeMeasure"/>.
/// Phill, 2026-11-19: a list that doesn't fit becomes a second stored result (Q1), one warning covers every withheld
/// section (Q2), and the rule lives in <see cref="ResultDelivery"/> for every stored result (Q3).
/// </summary>
public sealed class FindResultCapTests : IDisposable
{
    private const int InlineMax = 100_000;

    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly FakeClock clock = new();
    private readonly ResultStore store;
    private readonly ResultDelivery delivery;

    public FindResultCapTests()
    {
        var settings = new ResultSettings(InlineMax, 30, 64);
        store = new ResultStore(clock, () => settings);
        delivery = new ResultDelivery(store, EnvelopeMeasure.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => store.Dispose();

    /// <summary>A page ID shaped like OneNote's (two GUID-sized groups).</summary>
    private static string PageId(int i) =>
        string.Create(CultureInfo.InvariantCulture, $"{{{i:D8}-0000-4000-8000-000000000001}}{{1}}{{E1{i:D30}0000000000}}");

    private static string SectionId(int i) =>
        string.Create(CultureInfo.InvariantCulture, $"{{{i:D8}-5EC0-4000-8000-000000000001}}{{1}}{{B0}}");

    private static List<FilteredPage> Filtered(int count) => [.. Enumerable.Range(0, count).Select(i => new FilteredPage(PageId(i), "noMatch"))];

    private static ToolSuccess Search(int withheldSections, int filteredPages, int bodySections = 60, int bodyPages = 40)
    {
        var withheld = Enumerable.Range(0, withheldSections).Select(SectionId).ToList();
        var broad = new BroadAccess(withheld, []);
        var body = BigXml.Hierarchy(bodySections, bodyPages);
        return new ToolSuccess(body, new ToolMeta("c", Filtered: Filtered(filteredPages), PossiblyIncomplete: true, Withheld: broad.Meta), broad.Warnings)
        {
            Source = new ResultSource(ResultKind.HierarchyXml),
        };
    }

    // Red on 2026-11-19 before the fix (the plan's measurement): 29 withheld + 1,000 filtered → 140,072 chars; 9 + 1,000
    // → 125,192; 29 + 2,000 → 258,072; 29 + 0 and 29 + 200 fit.
    [Theory]
    [InlineData(29, 0)]
    [InlineData(29, 200)]
    [InlineData(29, 1_000)]
    [InlineData(9, 1_000)]
    [InlineData(29, 2_000)]
    [InlineData(300, 0)]
    [InlineData(300, 5_000)]
    [InlineData(5_000, 0)] // D189: thousands withheld, the header alone over the threshold
    [InlineData(5_000, 200)]
    [InlineData(5_000, 5_000)]
    public void TheFirstTextBlock_StaysWithinInlineMax(int withheld, int filtered)
    {
        var size = EnvelopeMeasure.Instance.TextBlock(delivery.Deliver(Search(withheld, filtered)).Success);

        Assert.True(size <= InlineMax, string.Create(CultureInfo.InvariantCulture, $"withheld {withheld}, filtered {filtered}: first text block {size} chars, over {InlineMax}."));
    }

    [Fact]
    public void AHeaderThatFits_IsUnchanged()
    {
        var first = delivery.Deliver(Search(29, 200)).Success;

        Assert.Equal(200, first.Meta.Filtered!.Count);
        Assert.Null(first.Meta.FilteredCount);
        Assert.Null(first.Meta.FilteredResultId);
    }

    [Fact]
    public async Task AFilteredListThatDoesntFit_IsASecondStoredResult_ReadBackWhole()
    {
        var first = delivery.Deliver(Search(29, 2_000)).Success;

        Assert.Null(first.Meta.Filtered);
        Assert.Equal(2_000, first.Meta.FilteredCount);
        var listId = Assert.IsType<string>(first.Meta.FilteredResultId);

        // Reassembled through read_result exactly as a model would read it, part by part.
        var read = new ReadResultUseCase(store, EnvelopeMeasure.Instance);
        var text = new System.Text.StringBuilder();
        var offset = 0;
        while (true)
        {
            var part = await read.ExecuteAsync(new ToolCallContext("c"), listId, "/", null, null, null, offset, null, Ct);
            Assert.True(EnvelopeMeasure.Instance.TextBlock(part) <= InlineMax);
            var chunk = part.Meta.Binary ?? part.Xml ?? string.Empty;
            text.Append(chunk);
            offset += chunk.Length;
            if (chunk.Length == 0 || offset >= part.Meta.Result!.TotalChars)
            {
                break;
            }
        }

        var list = JsonSerializer.Deserialize<List<FilteredPage>>(text.ToString(), Camel)!;
        Assert.Equal(Filtered(2_000), list);
    }

    [Fact]
    public async Task ReadResultsIndexes_CarryTheListsCountAndId_AndArePagedLikeTheFirstIndex()
    {
        // The first index is page 0 of expand "/" (AD-20), so both are sized with the same, moved, header.
        var first = delivery.Deliver(Search(29, 2_000)).Success;
        var read = new ReadResultUseCase(store, EnvelopeMeasure.Instance);

        var root = await read.ExecuteAsync(new ToolCallContext("c"), first.Meta.Result!.ResultId, null, null, "/", null, null, null, Ct);

        Assert.Equal(first.Meta.Result.Pages, root.Meta.Result!.Pages);
        Assert.Equal(2_000, root.Meta.FilteredCount);
        Assert.Equal(first.Meta.FilteredResultId, root.Meta.FilteredResultId);
        Assert.Equal(first.Index!.Entries.Count, root.Index!.Entries.Count);
        Assert.True(EnvelopeMeasure.Instance.TextBlock(root) <= InlineMax);
    }

    [Fact]
    public void ASmallBody_WithAFilteredListOverTheCap_IsStored_AndTheListMoves()
    {
        // The plan's matrix row "Small body, long header": the header alone makes the result too long, so it is stored.
        var first = delivery.Deliver(Search(9, 2_000, bodySections: 2, bodyPages: 2)).Success;

        Assert.NotNull(first.Meta.Result);
        Assert.Equal(2_000, first.Meta.FilteredCount);
        Assert.True(EnvelopeMeasure.Instance.TextBlock(first) <= InlineMax);
    }

    // D188: a moved filtered list is a stored result too, so its store event and evictions are logged like the main one's.
    [Fact]
    public void AMovedFilteredList_ComesBackWithItsStoreEvent_BeforeTheMainOne()
    {
        var outcome = delivery.Deliver(Search(29, 2_000));

        var list = Assert.Single(outcome.MovedLists);
        Assert.Equal(outcome.Success.Meta.FilteredResultId, list.ResultId);
        Assert.Equal(JsonSerializer.Serialize(Filtered(2_000), Camel).Length, list.TotalChars);
        Assert.True(list.Change.TotalBytes < outcome.Stored!.Change.TotalBytes, "the list is stored before the main result");
        Assert.Empty(delivery.Deliver(Search(29, 200)).MovedLists);
    }

    [Fact]
    public async Task AMovedFilteredList_IsLogged_WithItsEvictions_IdsAndSizesOnly()
    {
        // A 16 MB store nearly filled by one old result: storing the moved list evicts it, before the main result is stored.
        var settings = new ResultSettings(InlineMax, 30, 16);
        using var small = new ResultStore(clock, () => settings);
        var fillerText = new string('x', (int)((settings.MaxStoreBytes - 50_000) / sizeof(char)) - 1_000);
        var fillerTree = OneNoteMcp.Xml.ElementSpans.Text(fillerText);
        var filler = new StoredResult("filler", ResultKind.Text, fillerTree, null, new ToolSuccess(null, new ToolMeta("old", Binary: fillerText), []), new int[fillerTree.Nodes.Count], ResultGrants.None);
        small.Add(filler, settings);
        var logger = new LargeResultTests.ListLogger();
        var runner = new ToolRunner(logger, new ResultDelivery(small, EnvelopeMeasure.Instance));

        var result = await runner.RunAsync("find_pages", new ReadUseCase(), (_, _) => Task.FromResult(Search(29, 2_000)), Ct);

        var meta = ((JsonElement)result.StructuredContent!).GetProperty("meta");
        var listId = meta.GetProperty("filteredResultId").GetString()!;
        var mainId = meta.GetProperty("result").GetProperty("resultId").GetString()!;
        var stored = logger.Lines.Where(l => l.StartsWith("result stored", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, stored.Count);
        Assert.Contains($"resultId={listId}", stored[0], StringComparison.Ordinal);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"totalChars={JsonSerializer.Serialize(Filtered(2_000), Camel).Length}"), stored[0], StringComparison.Ordinal);
        Assert.Contains("tool=find_pages", stored[0], StringComparison.Ordinal);
        Assert.Contains($"resultId={mainId}", stored[1], StringComparison.Ordinal);
        Assert.Single(logger.Lines, l => l.StartsWith("result evicted resultId=filler", StringComparison.Ordinal));
        Assert.All(logger.Lines, l => Assert.DoesNotContain("noMatch", l, StringComparison.Ordinal));
        Assert.All(logger.Lines, l => Assert.DoesNotContain(PageId(0), l, StringComparison.Ordinal));
    }

    // D189: meta.withheld moves like meta.filtered once the header still doesn't fit after the filtered move.
    [Fact]
    public void AFewWithheld_StayInline()
    {
        var first = delivery.Deliver(Search(29, 2_000)).Success;

        Assert.Equal(29, first.Meta.Withheld!.Count);
        Assert.Null(first.Meta.WithheldCount);
        Assert.Null(first.Meta.WithheldResultId);
    }

    [Fact]
    public async Task AWithheldListThatDoesntFit_IsAStoredResult_ReadBackWhole_UnderTheMainResultsGrants()
    {
        // A once grant holds for as long as the entry lives, so the list reads back whole.
        var grants = new ResultGrants([new ResultGrant("key", SectionId(9_999), Core.Access.ReadGrantLifetime.Once, null, WholeResult: true)]);
        var outcome = delivery.Deliver(Search(5_000, 0) with { Grants = grants });
        var first = outcome.Success;

        Assert.Null(first.Meta.Withheld);
        Assert.Equal(5_000, first.Meta.WithheldCount);
        var listId = Assert.IsType<string>(first.Meta.WithheldResultId);
        Assert.Equal(listId, Assert.Single(outcome.MovedLists).ResultId);
        Assert.Same(grants, store.Get(listId).Entry.Grants);
        Assert.Same(grants, store.Get(first.Meta.Result!.ResultId).Entry.Grants);
        Assert.True(EnvelopeMeasure.Instance.TextBlock(first) <= InlineMax);

        var ids = JsonSerializer.Deserialize<List<string>>(await ReadWhole(listId), Camel)!;
        Assert.Equal(Enumerable.Range(0, 5_000).Select(SectionId), ids);
    }

    [Fact]
    public async Task AMovedWithheldList_UnderALapsedGrant_IsReadNotGranted_AndExpired_IsResultExpired()
    {
        // An hours grant with no stored grant behind it has lapsed: the list is refused like the main result.
        var grants = new ResultGrants([new ResultGrant("key", SectionId(9_999), Core.Access.ReadGrantLifetime.Hours, clock.GetUtcNow().AddHours(1), WholeResult: true)]);
        var first = delivery.Deliver(Search(5_000, 0) with { Grants = grants }).Success;
        var read = new ReadResultUseCase(store, EnvelopeMeasure.Instance);

        var refused = await Assert.ThrowsAsync<Core.OneNoteException>(() => read.ExecuteAsync(new ToolCallContext("c"), first.Meta.WithheldResultId!, "/", null, null, null, 0, null, Ct));
        Assert.Equal(Core.ErrorCode.ReadNotGranted, refused.Code);

        clock.Advance(TimeSpan.FromMinutes(31));
        var expired = await Assert.ThrowsAsync<Core.OneNoteException>(() => read.ExecuteAsync(new ToolCallContext("c"), first.Meta.WithheldResultId!, "/", null, null, null, 0, null, Ct));
        Assert.Equal(Core.ErrorCode.ResultExpired, expired.Code);
    }

    [Fact]
    public async Task BothListsMove_FilteredFirst_AndEveryIndexCarriesBoth()
    {
        var outcome = delivery.Deliver(Search(5_000, 5_000));
        var first = outcome.Success;

        Assert.Null(first.Meta.Filtered);
        Assert.Null(first.Meta.Withheld);
        Assert.Equal(5_000, first.Meta.FilteredCount);
        Assert.Equal(5_000, first.Meta.WithheldCount);
        Assert.Equal([first.Meta.FilteredResultId!, first.Meta.WithheldResultId!], outcome.MovedLists.Select(l => l.ResultId));
        Assert.True(EnvelopeMeasure.Instance.TextBlock(first) <= InlineMax);

        var read = new ReadResultUseCase(store, EnvelopeMeasure.Instance);
        var root = await read.ExecuteAsync(new ToolCallContext("c"), first.Meta.Result!.ResultId, null, null, "/", null, null, null, Ct);
        Assert.Equal(first.Meta.WithheldResultId, root.Meta.WithheldResultId);
        Assert.Equal(5_000, root.Meta.WithheldCount);
        Assert.Equal(first.Meta.FilteredResultId, root.Meta.FilteredResultId);
        Assert.Equal(first.Meta.Result.Pages, root.Meta.Result!.Pages);

        var ids = JsonSerializer.Deserialize<List<string>>(await ReadWhole(first.Meta.WithheldResultId!), Camel)!;
        Assert.Equal(5_000, ids.Count);
        Assert.Equal(5_000, JsonSerializer.Deserialize<List<FilteredPage>>(await ReadWhole(first.Meta.FilteredResultId!), Camel)!.Count);
    }

    [Fact]
    public void AMovedWithheldList_IsOnTheWire_AsWithheldCountAndWithheldResultId()
    {
        var first = delivery.Deliver(Search(5_000, 0)).Success;

        var json = JsonSerializer.Serialize(first.Meta, Camel);
        Assert.Contains("\"withheldCount\":5000", json, StringComparison.Ordinal);
        Assert.Contains("\"withheldResultId\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain(SectionId(0), json, StringComparison.Ordinal);
    }

    /// <summary>A stored text result reassembled through read_result part by part, as a model would read it.</summary>
    private async Task<string> ReadWhole(string resultId)
    {
        var read = new ReadResultUseCase(store, EnvelopeMeasure.Instance);
        var text = new System.Text.StringBuilder();
        var offset = 0;
        while (true)
        {
            var part = await read.ExecuteAsync(new ToolCallContext("c"), resultId, "/", null, null, null, offset, null, Ct);
            Assert.True(EnvelopeMeasure.Instance.TextBlock(part) <= InlineMax);
            var chunk = part.Meta.Binary ?? part.Xml ?? string.Empty;
            text.Append(chunk);
            offset += chunk.Length;
            if (chunk.Length == 0 || offset >= part.Meta.Result!.TotalChars)
            {
                return text.ToString();
            }
        }
    }

    private sealed class ReadUseCase : IUseCase
    {
        public Core.OperationClass Class => Core.OperationClass.Read;
    }

    [Fact]
    public void OneWarning_CoversEveryWithheldSection_AndWithheldKeepsEveryId()
    {
        var withheld = Enumerable.Range(0, 29).Select(SectionId).ToList();
        var broad = new BroadAccess(withheld, []) { ByPath = [SectionId(3), SectionId(4)] };

        var warning = Assert.Single(broad.Warnings, w => w.Code == ToolWarnings.ReadNotGranted);

        Assert.Contains("29", warning.Message, StringComparison.Ordinal);
        Assert.Contains("27 password-protected", warning.Message, StringComparison.Ordinal);
        Assert.Contains("2 ", warning.Message, StringComparison.Ordinal);
        Assert.Contains("meta.withheld", warning.Message, StringComparison.Ordinal);
        Assert.Equal(29, broad.Meta!.Count);
    }
}
