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
