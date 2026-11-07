using Microsoft.Extensions.DependencyInjection;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 1.19's I/O matrix: find_pages' page-text cache (AD-12), keyed by page ID + <c>lastModifiedTime</c>. Every test
/// counts <c>GetPageContent</c> calls, and every cached result is compared with a cold search of the same notebook state
/// (a fresh use case with the cache off), so the cache changes only how many reads are made, never the result.
/// </summary>
public sealed class FindPagesCacheTests
{
    private const string Ns = "xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\"";
    private const string T1 = "2026-11-05T02:40:26.000Z";
    private const string T2 = "2026-11-05T02:41:00.000Z";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- First search / Repeat search -------------------------------------------------------------------------------

    [Fact]
    public async Task FixtureNotebook_TwoIdenticalSearches_SecondMakesNoReads_SameResult()
    {
        // The acceptance criterion, on the captured MCP Test search XML: two Seed pages need a read, five Fixtures
        // pages match by name, ten are in the recycle bin.
        var gateway = new CountingGateway { SearchXml = SearchResultFilterTests.Fixture };
        gateway.PageText[SearchToolTests.SeedA] = string.Empty;
        gateway.PageText[SearchToolTests.SeedB] = string.Empty;
        var useCase = new FindPagesUseCase(gateway, new PageTextCache(() => SearchSettings.Default));

        var first = await Search(useCase, "Fixture");
        Assert.Equal(2, gateway.Reads.Count);
        gateway.Reads.Clear();

        var second = await Search(useCase, "Fixture");

        Assert.Empty(gateway.Reads);
        AssertSameResult(first, second);
        AssertSameResult(await Cold(gateway, "Fixture"), second);
        Assert.Equal(new PageTextCacheUse(Hits: 2, Misses: 0, Evicted: 0, Entries: 2, Bytes: PageTextCache.SizeOf(SearchToolTests.SeedA, Extracted(string.Empty)) + PageTextCache.SizeOf(SearchToolTests.SeedB, Extracted(string.Empty))), second.SearchCache);
    }

    [Fact]
    public async Task FirstSearch_ReadsEveryCandidate_RepeatWithAnotherQuery_ReadsNone_ResultsAsCold()
    {
        var gateway = Notebook(("a", T1, "alpha beta"), ("b", T1, "beta gamma"), ("c", T1, "gamma"));
        var (useCase, cache) = WithCache(gateway);

        var first = await Search(useCase, "beta");
        Assert.Equal(["a", "b", "c"], gateway.Reads);
        Assert.Equal(3, cache.Count);
        Assert.Equal(new[] { ("a", "alpha beta"), ("b", "beta gamma"), ("c", "gamma") }.Sum(p => PageTextCache.SizeOf(p.Item1, Extracted(p.Item2))), cache.TotalBytes);
        AssertSameResult(await Cold(gateway, "beta"), first);
        gateway.Reads.Clear();

        foreach (var query in new[] { "gamma", "ALPHA", "beta gamma", "zeta", string.Empty })
        {
            var repeat = await Search(useCase, query);

            Assert.Empty(gateway.Reads);
            AssertSameResult(await Cold(gateway, query), repeat);
            gateway.Reads.Clear();
        }
    }

    // --- Page edited between searches -------------------------------------------------------------------------------

    [Fact]
    public async Task PageEdited_NewLastModifiedTime_IsReRead_EditReflected()
    {
        var gateway = Notebook(("a", T1, "old words"), ("b", T1, "other"));
        var (useCase, cache) = WithCache(gateway);
        await Search(useCase, "new");
        gateway.Reads.Clear();

        gateway.SearchXml = Hierarchy(Page("a", T2), Page("b", T1));
        gateway.PageText["a"] = "new words";
        var after = await Search(useCase, "new");

        Assert.Equal(["a"], gateway.Reads);
        Assert.Equal(["a"], Kept(after));
        AssertSameResult(await Cold(gateway, "new"), after);
        Assert.Equal(2, cache.Count);
        Assert.Equal(new PageTextCacheUse(Hits: 1, Misses: 1, Evicted: 0, Entries: 2, Bytes: cache.TotalBytes), after.SearchCache);

        // The re-read text is cached at the new time.
        gateway.Reads.Clear();
        Assert.Equal(["a"], Kept(await Search(useCase, "new")));
        Assert.Empty(gateway.Reads);
    }

    [Fact]
    public async Task KnownGap_TextChangedButLastModifiedTimeDidNot_MatchesTheCachedText()
    {
        // Spike 1: a page edited through COM while it isn't open in OneNote keeps its lastModifiedTime. The cache then
        // answers with the old text (documented in the README and find_pages' description; cacheMaxMB 0 avoids it).
        var gateway = Notebook(("a", T1, "old words"));
        var (useCase, _) = WithCache(gateway);
        await Search(useCase, "old");

        gateway.PageText["a"] = "new words";
        var stale = await Search(useCase, "old");

        Assert.Equal(["a"], Kept(stale));
        Assert.Empty(Kept(await Cold(gateway, "old")));
    }

    // --- Page deleted -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task PageDeleted_ReadGivesObjectNotFound_RemovedAsNotFound_EntryDropped()
    {
        var gateway = Notebook(("a", T1, "x"), ("b", T1, "y"));
        var (useCase, cache) = WithCache(gateway);
        await Search(useCase, "q");
        Assert.Equal(2, cache.Count);

        // OneNote still lists "a" (at a new time, so it is read), but the page is gone.
        gateway.SearchXml = Hierarchy(Page("a", T2), Page("b", T1));
        gateway.PageText.Remove("a");
        gateway.Reads.Clear();
        var after = await Search(useCase, "q");

        Assert.Equal(["a"], gateway.Reads);
        Assert.Contains(new FilteredPage("a", FilterReasons.NotFound), after.Meta.Filtered!);
        AssertSameResult(await Cold(gateway, "q"), after);
        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet("a", Time(T1), out _));
        Assert.False(cache.TryGet("a", Time(T2), out _));
    }

    [Fact]
    public async Task ColdSearch_NotFoundPage_IsNeverCached()
    {
        var gateway = Notebook(("b", T1, "y"));
        gateway.SearchXml = Hierarchy(Page("a", T1), Page("b", T1));
        var (useCase, cache) = WithCache(gateway);

        var result = await Search(useCase, "q");

        Assert.Contains(new FilteredPage("a", FilterReasons.NotFound), result.Meta.Filtered!);
        Assert.Equal(1, cache.Count);
        gateway.Reads.Clear();
        await Search(useCase, "q");
        Assert.Equal(["a"], gateway.Reads); // read again, and still notFound
    }

    // --- Cache off --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CacheOff_EverySearchReadsEveryCandidate_AndTurningItOffEmptiesTheCache()
    {
        var gateway = Notebook(("a", T1, "alpha"), ("b", T1, "beta"));
        var maxMB = SearchSettings.Default.CacheMaxMB;
        var cache = new PageTextCache(() => new SearchSettings(maxMB));
        var useCase = new FindPagesUseCase(gateway, cache);
        await Search(useCase, "alpha");
        Assert.Equal(2, cache.Count);

        maxMB = 0;
        for (var i = 0; i < 2; i++)
        {
            gateway.Reads.Clear();
            var result = await Search(useCase, "alpha");

            Assert.Equal(["a", "b"], gateway.Reads);
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.TotalBytes);
            AssertSameResult(await Cold(gateway, "alpha"), result);
            Assert.Equal(new PageTextCacheUse(Hits: 0, Misses: 0, Evicted: i == 0 ? 2 : 0, Entries: 0, Bytes: 0), result.SearchCache);
        }

        // Back on: the next call fills it again.
        maxMB = 1;
        await Search(useCase, "alpha");
        Assert.Equal(2, cache.Count);
    }

    // --- Cache full -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CacheFull_LeastRecentlyUsedDropped_ResultsUnchanged()
    {
        // cacheMaxMB 1 = 1,048,576 bytes = 524,288 UTF-16 chars: two of these 200,000-char pages fit, three don't.
        var big = new string('x', 200_000);
        var gateway = Notebook(("a", T1, big + " alpha"), ("b", T1, big + " beta"), ("c", T1, big + " gamma"));
        var cache = new PageTextCache(() => new SearchSettings(1));
        var useCase = new FindPagesUseCase(gateway, cache);

        var first = await Search(useCase, "beta");

        Assert.Equal(["a", "b", "c"], gateway.Reads);
        Assert.Equal(2, cache.Count); // "a", the least recently used, went when "c" came in
        Assert.True(cache.TotalBytes <= 1024L * 1024);
        Assert.Equal(1, first.SearchCache!.Evicted);
        AssertSameResult(await Cold(gateway, "beta"), first);

        // b and c are cached: a search whose candidates are just those two reads nothing.
        gateway.SearchXml = Hierarchy(Page("b", T1), Page("c", T1));
        gateway.Reads.Clear();
        var subset = await Search(useCase, "beta");

        Assert.Empty(gateway.Reads);
        AssertSameResult(await Cold(gateway, "beta"), subset);
        Assert.Equal(new PageTextCacheUse(Hits: 2, Misses: 0, Evicted: 0, Entries: 2, Bytes: cache.TotalBytes), subset.SearchCache);

        // All three again: plain LRU in document order means a evicts b, b evicts c, c evicts a, so every page is read
        // (as with no cache) and the result is still the same.
        gateway.SearchXml = Hierarchy(Page("a", T1), Page("b", T1), Page("c", T1));
        gateway.Reads.Clear();
        var again = await Search(useCase, "beta");

        Assert.Equal(["a", "b", "c"], gateway.Reads);
        AssertSameResult(first, again);
        Assert.True(cache.TotalBytes <= 1024L * 1024);
    }

    [Fact]
    public async Task CapLowered_TrimsBeforeTheSearch()
    {
        var big = new string('x', 300_000);
        var gateway = Notebook(("a", T1, big), ("b", T1, big));
        var maxMB = 64;
        var cache = new PageTextCache(() => new SearchSettings(maxMB));
        var useCase = new FindPagesUseCase(gateway, cache);
        await Search(useCase, "q");
        Assert.Equal(2, cache.Count);

        maxMB = 1;
        gateway.Reads.Clear();
        var result = await Search(useCase, "q");

        Assert.True(cache.TotalBytes <= 1024L * 1024);
        Assert.Equal(1, cache.Count);
        // The trim drops "a" (the older) before any lookup; caching "a" again evicts "b"; caching "b" evicts "a".
        Assert.Equal(new PageTextCacheUse(Hits: 0, Misses: 2, Evicted: 3, Entries: 1, Bytes: cache.TotalBytes), result.SearchCache);
        Assert.Equal(["a", "b"], gateway.Reads);
        AssertSameResult(await Cold(gateway, "q"), result);
    }

    // --- No usable lastModifiedTime ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public async Task MissingOrUnparsableLastModifiedTime_NeverCached_ReadEveryTime(string? time)
    {
        var gateway = Notebook(("a", T1, "alpha"));
        gateway.SearchXml = Hierarchy(Page("a", time));
        var (useCase, cache) = WithCache(gateway);

        for (var i = 0; i < 2; i++)
        {
            gateway.Reads.Clear();
            var result = await Search(useCase, "alpha");

            Assert.Equal(["a"], gateway.Reads);
            Assert.Equal(["a"], Kept(result));
        }

        Assert.Equal(0, cache.Count);
    }

    // --- What isn't read stays unread -------------------------------------------------------------------------------

    [Fact]
    public async Task RecycleBinAndNameMatches_AreNeverReadNorCached()
    {
        var xml = "<?xml version=\"1.0\"?>\n<one:Notebook " + Ns + " ID=\"n\">"
            + "<one:Section ID=\"bin\" isInRecycleBin=\"true\">" + Page("binned", T1) + "</one:Section>"
            + "<one:Section ID=\"s\"><one:Page ID=\"named\" name=\"Alpha notes\" lastModifiedTime=\"" + T1 + "\"/></one:Section>"
            + "</one:Notebook>";
        var gateway = new CountingGateway { SearchXml = xml };
        var (useCase, cache) = WithCache(gateway);

        await Search(useCase, "alpha");

        Assert.Empty(gateway.Reads);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task OtherReadError_FailsTheCall_CacheKeepsWhatItHad()
    {
        var gateway = Notebook(("a", T1, "alpha"), ("b", T1, "beta"));
        var (useCase, cache) = WithCache(gateway);
        await Search(useCase, "q");

        gateway.SearchXml = Hierarchy(Page("a", T1), Page("b", T2));
        gateway.Failures["b"] = new OneNoteException(ErrorCode.ModalDialogOpen, "GetPageContent failed: hrCOMBlocked (0x80042030).", unchecked((int)0x80042030));

        var error = await Assert.ThrowsAsync<OneNoteException>(() => Search(useCase, "q"));

        Assert.Equal(ErrorCode.ModalDialogOpen, error.Code);
        Assert.True(cache.TryGet("a", Time(T1), out var text));
        Assert.Equal(Extracted("alpha"), text);
    }

    // --- Server wiring ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ServerSetup_OneCachePerProcess_ReadsSearchCacheMaxMBFromSettingsJsonEachCall()
    {
        using var dir = new TempDirectory();
        var settingsPath = dir.Combine("settings.json");
        var gateway = Notebook(("a", T1, "alpha"), ("b", T1, "beta"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOneNoteMcp();
        services.AddSingleton<IOneNoteGateway>(gateway);
        services.AddSingleton(new SettingsStore(settingsPath));
        using var provider = services.BuildServiceProvider();

        var useCase = provider.GetRequiredService<FindPagesUseCase>();
        Assert.Same(useCase, provider.GetRequiredService<FindPagesUseCase>());
        var cache = provider.GetRequiredService<PageTextCache>();
        Assert.Same(cache, provider.GetRequiredService<PageTextCache>());

        // No settings file: the default cap, so the repeat makes no reads, through the registered cache.
        await Search(useCase, "alpha");
        Assert.Equal(2, cache.Count);
        gateway.Reads.Clear();
        await Search(useCase, "alpha");
        Assert.Empty(gateway.Reads);

        // cacheMaxMB 0 in settings.json applies at the next call: every candidate read, and the cache emptied.
        File.WriteAllText(settingsPath, """{"schemaVersion": 1, "search": {"cacheMaxMB": 0}}""");
        await Search(useCase, "alpha");
        Assert.Equal(["a", "b"], gateway.Reads);
        Assert.Equal(0, cache.Count);
    }

    // --- PageTextCache on its own -----------------------------------------------------------------------------------

    [Fact]
    public void Cache_TryGet_RefreshesRecency_StaleTimeDropsTheEntry()
    {
        var cache = PageTextCache.WithDefaults();
        var entry = PageTextCache.SizeOf("a", "aa"); // every entry here: a one-char ID, a two-char text
        var cap = 3 * entry;
        cache.Set("a", Time(T1), "aa", cap);
        cache.Set("b", Time(T1), "bb", cap);
        Assert.True(cache.TryGet("a", Time(T1), out _)); // "a" is now the most recently used

        var evicted = cache.Set("c", Time(T1), "cc", cap); // three entries fit
        evicted += cache.Set("d", Time(T1), "dd", cap);    // four don't: the oldest, "b", goes

        Assert.Equal(1, evicted);
        Assert.False(cache.TryGet("b", Time(T1), out _));
        Assert.True(cache.TryGet("a", Time(T1), out _));

        Assert.False(cache.TryGet("a", Time(T2), out _)); // another time: a miss, and the old entry is dropped
        Assert.False(cache.TryGet("a", Time(T1), out _));
        Assert.Equal(2, cache.Count);
        Assert.Equal(2 * entry, cache.TotalBytes);
    }

    [Fact]
    public void Cache_TextLargerThanTheCap_IsNotCached_AndReplacesNothing()
    {
        var cache = PageTextCache.WithDefaults();
        var cap = PageTextCache.SizeOf("a", new string('x', 50));
        cache.Set("a", Time(T1), "small", cap);

        cache.Set("a", Time(T2), new string('x', 51), cap); // two bytes over the cap

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.TotalBytes);
    }

    [Fact]
    public void Cache_TrimToZero_EmptiesIt_EmptyTextsIncluded()
    {
        var cache = PageTextCache.WithDefaults();
        cache.Set("a", Time(T1), string.Empty, 1_000);
        cache.Set("b", Time(T1), "b", 1_000);
        Assert.Equal(PageTextCache.SizeOf("a", string.Empty) + PageTextCache.SizeOf("b", "b"), cache.TotalBytes); // empty text still counts

        Assert.Equal(2, cache.Trim(0));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.TotalBytes);
    }

    [Fact]
    public async Task Cache_ConcurrentUse_KeepsItsTotalsConsistent()
    {
        var cache = PageTextCache.WithDefaults();
        await Parallel.ForAsync(0, 2_000, Ct, (i, token) =>
        {
            var id = i % 50;
            var key = Key(id);
            if (!cache.TryGet(key, TimeOf(id), out _))
            {
                cache.Set(key, TimeOf(id), new string('x', i % 7), ConcurrentCap);
            }

            if (i % 11 == 0)
            {
                cache.Remove(key);
            }

            if (i % 13 == 0)
            {
                cache.TryGet(key, Time(T1).AddDays(1), out _); // a stale lookup drops the entry
            }

            return ValueTask.CompletedTask;
        });

        Assert.True(cache.TotalBytes <= ConcurrentCap);
        Assert.Equal(cache.TotalBytes, Enumerable.Range(0, 50).Sum(id => Probe(cache, id)));
    }

    // --- Helpers ----------------------------------------------------------------------------------------------------

    private static string Key(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset TimeOf(int id) => Time(id % 2 == 0 ? T1 : T2);

    private static long Probe(PageTextCache cache, int id) =>
        cache.TryGet(Key(id), TimeOf(id), out var text) ? PageTextCache.SizeOf(Key(id), text) : 0;

    // Room for about five of the concurrency test's entries (IDs up to 2 chars, texts up to 6), so it evicts constantly.
    private const long ConcurrentCap = 5 * (PageTextCache.EntryOverheadBytes + (2 * 2) + (6 * 2));

    private static (FindPagesUseCase UseCase, PageTextCache Cache) WithCache(CountingGateway gateway)
    {
        var cache = new PageTextCache(() => SearchSettings.Default);
        return (new FindPagesUseCase(gateway, cache), cache);
    }

    private static Task<ToolSuccess> Search(FindPagesUseCase useCase, string query) =>
        useCase.ExecuteAsync(new ToolCallContext("c"), query, SearchToolTests.NotebookId, fromRoot: false, Ct);

    /// <summary>The same search with no cache at all, the result today's server gives; its reads aren't counted.</summary>
    private static async Task<ToolSuccess> Cold(CountingGateway gateway, string query)
    {
        var before = gateway.Reads.Count;
        var result = await new FindPagesUseCase(gateway, new PageTextCache(() => new SearchSettings(0)))
            .ExecuteAsync(new ToolCallContext("c"), query, SearchToolTests.NotebookId, fromRoot: false, Ct);
        gateway.Reads.RemoveRange(before, gateway.Reads.Count - before);
        return result;
    }

    private static void AssertSameResult(ToolSuccess expected, ToolSuccess actual)
    {
        Assert.Equal(expected.Xml, actual.Xml);
        Assert.Equal(expected.Meta.Filtered, actual.Meta.Filtered);
        Assert.Equal(expected.Meta.PossiblyIncomplete, actual.Meta.PossiblyIncomplete);
        Assert.True(actual.Meta.PossiblyIncomplete);
        Assert.Equal(expected.Warnings, actual.Warnings);
    }

    /// <summary>What the cache holds for a page served with <paramref name="body"/>: find_pages' text of it (empty title line first).</summary>
    private static string Extracted(string body) => PageText.Of(PageXml(body));

    private static List<string> Kept(ToolSuccess result) => [.. SearchResultFilter.Pages(result.Xml!).Select(p => p.Id)];

    private static DateTimeOffset Time(string value) => System.Xml.XmlConvert.ToDateTimeOffset(value);

    private static CountingGateway Notebook(params (string Id, string Time, string Text)[] pages)
    {
        var gateway = new CountingGateway { SearchXml = Hierarchy([.. pages.Select(p => Page(p.Id, p.Time))]) };
        foreach (var page in pages)
        {
            gateway.PageText[page.Id] = page.Text;
        }

        return gateway;
    }

    /// <summary>A candidate page: its name, "Untitled", holds no query term, so it's always read or looked up.</summary>
    private static string Page(string id, string? time) =>
        time is null
            ? $"<one:Page ID=\"{id}\" name=\"Untitled\" isIndexed=\"false\"/>"
            : $"<one:Page ID=\"{id}\" name=\"Untitled\" lastModifiedTime=\"{time}\" isIndexed=\"false\"/>";

    private static string Hierarchy(params string[] pages) =>
        "<?xml version=\"1.0\"?>\n<one:Notebook " + Ns + " name=\"N\" ID=\"" + SearchToolTests.NotebookId + "\"><one:Section name=\"S\" ID=\"{s}\">"
        + string.Concat(pages) + "</one:Section></one:Notebook>";

    private static string PageXml(string body) =>
        "<?xml version=\"1.0\"?>\n<one:Page " + Ns + " ID=\"{p}\" name=\"Untitled\"><one:Title><one:OE><one:T><![CDATA[]]></one:T></one:OE></one:Title>"
        + "<one:Outline><one:OEChildren><one:OE><one:T><![CDATA[" + body + "]]></one:T></one:OE></one:OEChildren></one:Outline></one:Page>";

    /// <summary>
    /// Answers <c>FindPages</c> with <see cref="SearchXml"/> and each piBasic read with a page holding
    /// <see cref="PageText"/>[id], recording every read. An ID with no text is <see cref="ErrorCode.ObjectNotFound"/>.
    /// </summary>
    private sealed class CountingGateway : IOneNoteGateway
    {
        public string SearchXml { get; set; } = string.Empty;

        public Dictionary<string, string> PageText { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, OneNoteException> Failures { get; } = new(StringComparer.Ordinal);

        public List<string> Reads { get; } = [];

        public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) => Task.FromResult(SearchXml);

        public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct)
        {
            Assert.Equal(PageInfo.Basic, pageInfo);
            Reads.Add(pageId);
            if (Failures.TryGetValue(pageId, out var failure))
            {
                return Task.FromException<string>(failure);
            }

            return PageText.TryGetValue(pageId, out var text)
                ? Task.FromResult(PageXml(text))
                : Task.FromException<string>(new OneNoteException(ErrorCode.ObjectNotFound, "GetPageContent failed: hrObjectDoesNotExist (0x80042014).", unchecked((int)0x80042014)));
        }

        public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => throw new NotSupportedException();

        public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct) => throw new NotSupportedException();
    }
}
