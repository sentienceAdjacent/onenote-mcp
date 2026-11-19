using System.Globalization;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Server;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Backlog 15: a stored <c>find_pages</c> / <c>find_meta</c> result's first text block went over
/// <c>results.inlineMaxChars</c> on MCP Test (29 protected sections; 16 blocks over, the largest 102,870). These build the
/// search success the way <c>SearchResults.Result</c> does (body, <c>meta.filtered</c>, <c>meta.withheld</c> and one
/// <c>readNotGranted</c> warning per withheld section) and deliver it through the real <see cref="ResultDelivery"/> and
/// <see cref="EnvelopeMeasure"/>.
/// </summary>
public sealed class FindResultCapTests : IDisposable
{
    private const int InlineMax = 100_000;

    private readonly FakeClock clock = new();
    private readonly ResultStore store;
    private readonly ResultDelivery delivery;

    public FindResultCapTests()
    {
        var settings = new ResultSettings(InlineMax, 30, 64);
        store = new ResultStore(clock, () => settings);
        delivery = new ResultDelivery(store, EnvelopeMeasure.Instance);
    }

    public void Dispose() => store.Dispose();

    /// <summary>A page ID shaped like OneNote's (two GUID-sized groups).</summary>
    private static string PageId(int i) =>
        string.Create(CultureInfo.InvariantCulture, $"{{{i:D8}-0000-4000-8000-000000000001}}{{1}}{{E1{i:D30}0000000000}}");

    private static string SectionId(int i) =>
        string.Create(CultureInfo.InvariantCulture, $"{{{i:D8}-5EC0-4000-8000-000000000001}}{{1}}{{B0}}");

    private ToolSuccess Search(int withheldSections, int filteredPages, int bodySections = 60, int bodyPages = 40)
    {
        var withheld = Enumerable.Range(0, withheldSections).Select(SectionId).ToList();
        var broad = new BroadAccess(withheld, []);
        var filtered = Enumerable.Range(0, filteredPages).Select(i => new FilteredPage(PageId(i), "noMatch")).ToList();
        var body = BigXml.Hierarchy(bodySections, bodyPages);
        return new ToolSuccess(body, new ToolMeta("c", Filtered: filtered, PossiblyIncomplete: true, Withheld: broad.Meta), broad.Warnings)
        {
            Source = new ResultSource(ResultKind.HierarchyXml),
        };
    }

    private int FirstBlock(ToolSuccess success) => EnvelopeMeasure.Instance.TextBlock(delivery.Deliver(success).Success);

    // Red on 2026-11-19 (the measurement for backlog 15's plan): 29 withheld + 1,000 filtered → 140,072 chars; 9 + 1,000
    // → 125,192; 29 + 2,000 → 258,072; 29 + 0 and 29 + 200 fit. Skipped until the plan's open questions are answered.
    [Theory(Skip = "Backlog 15: red until the fix; see the plan's Open Questions.")]
    [InlineData(29, 0)]
    [InlineData(29, 200)]
    [InlineData(29, 1_000)]
    [InlineData(9, 1_000)]
    [InlineData(29, 2_000)]
    public void TheFirstTextBlock_StaysWithinInlineMax(int withheld, int filtered)
    {
        var size = FirstBlock(Search(withheld, filtered));

        Assert.True(size <= InlineMax, string.Create(CultureInfo.InvariantCulture, $"withheld {withheld}, filtered {filtered}: first text block {size} chars, over {InlineMax}."));
    }
}
