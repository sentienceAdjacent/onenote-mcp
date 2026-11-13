using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 9's I/O matrix: <c>find_pages</c> and <c>find_meta</c> through tool, use case and the fixture-backed
/// <see cref="FakeGateway"/> (wrapped by <see cref="SearchGateway"/> to record calls and serve the Seed pages), with the
/// XML compared byte for byte against the fixture minus the removed page spans (AD-4, AD-12).
/// </summary>
public sealed class SearchToolTests
{
    public const string NotebookId = HierarchyReadToolTests.NotebookId;
    public const string SeedA = "{01315E59-19DB-49CC-8D49-943DAD4E30CD}{1}{E19537398477438794744220128790704520923757071}";
    public const string SeedB = "{01315E59-19DB-49CC-8D49-943DAD4E30CD}{1}{E19566699264604354443720158093300928611933671}";
    public const string Nested = "{A20A4FC1-E5F2-4154-ADC8-8A513656DFEE}{1}{E19478355080928453447520102198986629748415351}";
    public const string Table = "{A20A4FC1-E5F2-4154-ADC8-8A513656DFEE}{1}{E19565193393940819141020118324689590846097081}";
    public const string Image = "{A20A4FC1-E5F2-4154-ADC8-8A513656DFEE}{1}{E19499018700455935598720161835466737046045861}";
    public const string Probe = "{A20A4FC1-E5F2-4154-ADC8-8A513656DFEE}{1}{E19516938799045219860520175612925891145658211}";
    public const string Subpage = "{A20A4FC1-E5F2-4154-ADC8-8A513656DFEE}{1}{E19576028730475398412520134068976138901756251}";

    private const string Ns = "xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\"";

    private static readonly string[] Fixtures = [Nested, Table, Image, Probe, Subpage];

    private static string FindPagesFixture => FakeGateway.ReadFixture("mcp-test/search/findpages-fixture.xml");

    private static string FindMetaFixture => FakeGateway.ReadFixture("mcp-test/search/findmeta-fixture.xml");

    private static List<string> BinIds => [.. SearchResultFilter.Pages(FindPagesFixture).Where(p => p.InRecycleBin).Select(p => p.Id)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Fixtures_FindPagesAndFindMeta_AreByteIdentical_SeventeenPages()
    {
        Assert.Equal(FindPagesFixture, FindMetaFixture);
        Assert.Equal(17, SearchResultFilter.Pages(FindPagesFixture).Count);
        Assert.Equal(10, BinIds.Count);
    }

    // --- Recycle bin / Text check / Candidate gone ------------------------------------------------------------------

    [Fact]
    public async Task FindPages_RecycleBin_TenRemovedAsRecycleBin_NeverRead_EveryOtherByteUnchanged()
    {
        var gateway = new SearchGateway();

        var root = Ok(await FindPages(gateway).FindPages("Fixture", NotebookId, cancellationToken: Ct));

        var filtered = Filtered(root);
        Assert.Equal(BinIds, filtered.Where(f => f.Reason == FilterReasons.RecycleBin).Select(f => f.PageId));
        Assert.Equal(SearchResultFilterTests.CutByRegex(FindPagesFixture, [.. filtered.Select(f => f.PageId)]), root.GetProperty("xml").GetString());
        Assert.True(root.GetProperty("meta").GetProperty("possiblyIncomplete").GetBoolean());
        Assert.DoesNotContain(gateway.PageReads, r => BinIds.Contains(r.PageId));
        Assert.Equal([(NotebookId, "Fixture", true)], gateway.FindPagesCalls);
    }

    [Fact]
    public async Task FindPages_TextCheck_SeedPagesNoMatch_FixturesPagesStay_ReadsOnlyPagesWhoseNameLacksATerm()
    {
        var gateway = new SearchGateway();

        var root = Ok(await FindPages(gateway).FindPages("Fixture", NotebookId, cancellationToken: Ct));

        // Document order: the two Seed pages come first, then (after the five Fixtures pages) the ten bin pages.
        Assert.Equal(
            [(SeedA, FilterReasons.NoMatch), (SeedB, FilterReasons.NoMatch), .. BinIds.Select(id => (id, FilterReasons.RecycleBin))],
            Filtered(root).Select(f => (f.PageId, f.Reason)));
        Assert.Equal(Fixtures, SearchResultFilter.Pages(root.GetProperty("xml").GetString()!).Select(p => p.Id));
        // The five Fixtures pages have "Fixture" in their names, so only the two Seed pages are read.
        Assert.Equal([SeedA, SeedB], gateway.PageReads.Select(r => r.PageId));
        Assert.All(gateway.PageReads, r => Assert.Equal(PageInfo.Basic, r.Info));
        AssertMetaHasOnly(root, "correlationId", "filtered", "possiblyIncomplete");
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task FindPages_TextCheck_MatchesBodyTextAndEveryTerm()
    {
        // The Seed page's body holds the term, its name doesn't: it stays. The other needs both terms.
        var gateway = new SearchGateway
        {
            SearchXml = Hierarchy(Page("a", "Untitled"), Page("b", "Untitled")),
        };
        gateway.PageXml["a"] = PageXml("<span lang='en'>Fix</span>ture ALPHA");
        gateway.PageXml["b"] = PageXml("fixture only");

        var root = Ok(await FindPages(gateway).FindPages("fixture alpha", NotebookId, cancellationToken: Ct));

        Assert.Equal([("b", FilterReasons.NoMatch)], Filtered(root).Select(f => (f.PageId, f.Reason)));
    }

    [Fact]
    public async Task FindPages_NameHoldsEveryTerm_KeptWithoutARead()
    {
        // "b"'s body lacks the terms, but its name holds both: kept, never read. "a" is read and fails the check.
        var gateway = new SearchGateway { SearchXml = Hierarchy(Page("a", "Untitled"), Page("b", "Alpha and BETA notes")) };
        gateway.PageXml["a"] = PageXml("nothing");
        gateway.PageXml["b"] = PageXml("nothing");

        var root = Ok(await FindPages(gateway).FindPages("alpha beta", NotebookId, cancellationToken: Ct));

        Assert.Equal([("a", FilterReasons.NoMatch)], Filtered(root).Select(f => (f.PageId, f.Reason)));
        Assert.Equal(["b"], SearchResultFilter.Pages(root.GetProperty("xml").GetString()!).Select(p => p.Id));
        Assert.Equal(["a"], gateway.PageReads.Select(r => r.PageId));
    }

    [Fact]
    public async Task FindPages_CandidateGone_IsNotFound_CallStillOk()
    {
        var gateway = new SearchGateway();
        gateway.PageFailures[SeedA] = new OneNoteException(ErrorCode.ObjectNotFound, "GetPageContent failed: hrObjectDoesNotExist (0x80042014).", unchecked((int)0x80042014));

        var root = Ok(await FindPages(gateway).FindPages("Fixture", NotebookId, cancellationToken: Ct));

        var filtered = Filtered(root);
        Assert.Contains(filtered, f => f.PageId == SeedA && f.Reason == FilterReasons.NotFound);
        Assert.Contains(filtered, f => f.PageId == SeedB && f.Reason == FilterReasons.NoMatch);
        Assert.Equal(Fixtures, SearchResultFilter.Pages(root.GetProperty("xml").GetString()!).Select(p => p.Id));
    }

    [Fact]
    public async Task FindPages_OtherReadError_FailsTheCall()
    {
        var gateway = new SearchGateway();
        gateway.PageFailures[SeedA] = new OneNoteException(ErrorCode.ModalDialogOpen, "GetPageContent failed: hrCOMBlocked (0x80042030).", unchecked((int)0x80042030));

        var error = Error(await FindPages(gateway).FindPages("Fixture", NotebookId, cancellationToken: Ct));

        Assert.Equal("ModalDialogOpen", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task FindPages_MalformedPageXml_IsInvalidXml()
    {
        var gateway = new SearchGateway { SearchXml = Hierarchy(Page("a", "n")) };
        gateway.PageXml["a"] = "<one:Page";

        var error = Error(await FindPages(gateway).FindPages("x", NotebookId, cancellationToken: Ct));

        Assert.Equal("InvalidXml", error.GetProperty("code").GetString());
    }

    // --- Meta filter ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FindMeta_MetaFilter_BinFirst_ThenNoMatch_OnlyFixtureNestedRemains_NoPageReads()
    {
        var gateway = new SearchGateway();

        var root = Ok(await FindMeta(gateway).FindMeta("onenote-mcp-fixture", NotebookId, cancellationToken: Ct));

        var xml = root.GetProperty("xml").GetString()!;
        Assert.Equal([Nested], SearchResultFilter.Pages(xml).Select(p => p.Id));
        var filtered = Filtered(root);
        Assert.Equal(
            [(SeedA, FilterReasons.NoMatch), (SeedB, FilterReasons.NoMatch), .. Fixtures.Skip(1).Select(id => (id, FilterReasons.NoMatch)), .. BinIds.Select(id => (id, FilterReasons.RecycleBin))],
            filtered.Select(f => (f.PageId, f.Reason)));
        Assert.Equal(SearchResultFilterTests.CutByRegex(FindMetaFixture, [.. filtered.Select(f => f.PageId)]), xml);
        Assert.Empty(gateway.PageReads);
        Assert.Equal([(NotebookId, "onenote-mcp-fixture", true)], gateway.FindMetaCalls);
        Assert.True(root.GetProperty("meta").GetProperty("possiblyIncomplete").GetBoolean());
    }

    [Fact]
    public async Task FindMeta_NameComparesOrdinally()
    {
        var gateway = new SearchGateway
        {
            SearchXml = Hierarchy(
                Page("upper", "x", "<one:Meta name=\"ONENOTE-MCP-FIXTURE\" content=\"1\"/>"),
                Page("exact", "x", "<one:Meta name=\"other\" content=\"1\"/><one:Meta name=\"onenote-mcp-fixture\" content=\"2\"/>")),
        };

        var root = Ok(await FindMeta(gateway).FindMeta("onenote-mcp-fixture", NotebookId, cancellationToken: Ct));

        Assert.Equal([("upper", FilterReasons.NoMatch)], Filtered(root).Select(f => (f.PageId, f.Reason)));
    }

    // --- Ancestor-only flag / Nothing to remove ---------------------------------------------------------------------

    [Fact]
    public async Task AncestorOnlyFlag_PageUnderABinSection_IsRemovedAsRecycleBin()
    {
        var xml = "<?xml version=\"1.0\"?>\n<one:Notebook " + Ns + " ID=\"n\">"
            + "<one:Section ID=\"s\" isInRecycleBin=\"true\"><one:Page ID=\"binned\" name=\"Fixture\"><one:Meta name=\"m\" content=\"1\"/></one:Page></one:Section>"
            + "<one:Section ID=\"t\"><one:Page ID=\"live\" name=\"Fixture\"><one:Meta name=\"m\" content=\"1\"/></one:Page></one:Section>"
            + "</one:Notebook>";
        var pagesGateway = new SearchGateway { SearchXml = xml };
        pagesGateway.PageXml["live"] = PageXml("Fixture");
        var metaGateway = new SearchGateway { SearchXml = xml };

        var pages = Ok(await FindPages(pagesGateway).FindPages("Fixture", NotebookId, cancellationToken: Ct));
        var meta = Ok(await FindMeta(metaGateway).FindMeta("m", NotebookId, cancellationToken: Ct));

        foreach (var root in new[] { pages, meta })
        {
            Assert.Equal([("binned", FilterReasons.RecycleBin)], Filtered(root).Select(f => (f.PageId, f.Reason)));
            Assert.Equal(SearchResultFilterTests.CutByRegex(xml, ["binned"]), root.GetProperty("xml").GetString());
        }

        Assert.Empty(pagesGateway.PageReads); // "live"'s name holds the term, so it isn't read
    }

    [Fact]
    public async Task NothingToRemove_ExactString_EmptyFilteredEmitted()
    {
        var xml = Hierarchy(Page("a", "Fixture one", "<one:Meta name=\"m\" content=\"1\"/>"), Page("b", "Fixture two", "<one:Meta name=\"m\" content=\"1\"/>"));
        var pagesGateway = new SearchGateway { SearchXml = xml };
        pagesGateway.PageXml["a"] = PageXml("x");
        pagesGateway.PageXml["b"] = PageXml("y");

        var results = new[]
        {
            await FindPages(pagesGateway).FindPages("fixture", NotebookId, cancellationToken: Ct),
            await FindMeta(new SearchGateway { SearchXml = xml }).FindMeta("m", NotebookId, cancellationToken: Ct),
        };

        foreach (var result in results)
        {
            var root = Ok(result);
            Assert.Equal(xml, root.GetProperty("xml").GetString());
            Assert.Equal(0, root.GetProperty("meta").GetProperty("filtered").GetArrayLength());
            Assert.Contains("\"filtered\":[]", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
            Assert.True(root.GetProperty("meta").GetProperty("possiblyIncomplete").GetBoolean());
        }
    }

    // --- Start rule -------------------------------------------------------------------------------------------------

    public static TheoryData<string, string, bool> BadStarts() => new()
    {
        { "find_pages", "", false },
        { "find_pages", NotebookId, true },
        { "find_meta", "", false },
        { "find_meta", NotebookId, true },
    };

    [Theory]
    [MemberData(nameof(BadStarts))]
    public async Task NeitherOrBoth_IsInvalidQuery_GatewayNotCalled_UserActionExplainsTheRule(string tool, string startNodeId, bool fromRoot)
    {
        var gateway = new SearchGateway();

        var error = Error(await Call(tool, gateway, "secret-query", startNodeId, fromRoot));

        Assert.Equal("InvalidQuery", error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        var userAction = error.GetProperty("userAction").GetString()!;
        Assert.Contains("startNodeId", userAction, StringComparison.Ordinal);
        Assert.Contains("fromRoot", userAction, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Empty(gateway.FindPagesCalls);
        Assert.Empty(gateway.FindMetaCalls);
        Assert.Empty(gateway.PageReads);
    }

    [Theory]
    [InlineData("find_pages")]
    [InlineData("find_meta")]
    public async Task FromRoot_GatewayGetsTheEmptyString(string tool)
    {
        var gateway = new SearchGateway { SearchXml = Hierarchy() };

        Ok(await Call(tool, gateway, "q", string.Empty, fromRoot: true));

        var calls = tool == "find_pages" ? gateway.FindPagesCalls : gateway.FindMetaCalls;
        Assert.Equal([(string.Empty, "q", true)], calls);
    }

    [Theory]
    [InlineData("find_pages")]
    [InlineData("find_meta")]
    public async Task StartNodeIdAndQuery_ReachTheGatewayUnchanged(string tool)
    {
        var gateway = new SearchGateway { SearchXml = Hierarchy() };
        gateway.Hierarchies[(" {weird}{1}{B0} ", HierarchyScope.Sections)] = "<one:Section " + Ns + " ID=\" {weird}{1}{B0} \" name=\"S\" path=\"C:\\s.one\" />";

        Ok(await Call(tool, gateway, " a  b ", " {weird}{1}{B0} ", fromRoot: false));

        var calls = tool == "find_pages" ? gateway.FindPagesCalls : gateway.FindMetaCalls;
        Assert.Equal([(" {weird}{1}{B0} ", " a  b ", true)], calls);

        // The AD-21 scope read gets the same start, unchanged.
        Assert.Contains((" {weird}{1}{B0} ", HierarchyScope.Sections), gateway.HierarchyCalls);
    }

    // --- Bad start / empty query ------------------------------------------------------------------------------------

    public static TheoryData<string, uint, string> GatewayErrors() => new()
    {
        { "find_pages", 0x80042014, "ObjectNotFound" },
        { "find_pages", 0x80042019, "InvalidQuery" },
        { "find_meta", 0x80042014, "ObjectNotFound" },
        { "find_meta", 0x80042019, "InvalidQuery" },
    };

    [Theory]
    [MemberData(nameof(GatewayErrors))]
    public async Task GatewayErrors_AreTheEnvelope(string tool, uint hresult, string code)
    {
        var gateway = new SearchGateway { SearchFailure = new OneNoteException(Enum.Parse<ErrorCode>(code), "m", unchecked((int)hresult)) };

        var error = Error(await Call(tool, gateway, string.Empty, NotebookId, fromRoot: false));

        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(ToolErrors.FormatHResult(unchecked((int)hresult)), error.GetProperty("hresult").GetString());
    }

    [Theory]
    [InlineData("find_pages")]
    [InlineData("find_meta")]
    public async Task MalformedSearchXml_IsInvalidXml(string tool)
    {
        var gateway = new SearchGateway { SearchXml = "<one:Notebook" };

        var error = Error(await Call(tool, gateway, "q", NotebookId, fromRoot: false));

        Assert.Equal("InvalidXml", error.GetProperty("code").GetString());
    }

    [Fact]
    public void BothUseCases_AreReadClass()
    {
        var gateway = new FakeGateway();

        Assert.Equal(OperationClass.Read, new FindPagesUseCase(gateway).Class);
        Assert.Equal(OperationClass.Read, new FindMetaUseCase(gateway).Class);
    }

    [Fact]
    public async Task Messages_NeverCarryTheQueryOrPageNames()
    {
        var gateway = new SearchGateway();
        gateway.PageFailures[SeedA] = new OneNoteException(ErrorCode.ModalDialogOpen, "GetPageContent failed: hrCOMBlocked (0x80042030).", unchecked((int)0x80042030));

        var result = await FindPages(gateway).FindPages("Fixture", NotebookId, cancellationToken: Ct);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

        Assert.DoesNotContain("Fixture", text, StringComparison.Ordinal);
    }

    // --- Helpers ----------------------------------------------------------------------------------------------------

    private static ToolRunner Runner => new(NullLogger<ToolRunner>.Instance);

    private static FindPagesTool FindPages(IOneNoteGateway gateway) => new(Runner, new FindPagesUseCase(gateway));

    private static FindMetaTool FindMeta(IOneNoteGateway gateway) => new(Runner, new FindMetaUseCase(gateway));

    private static Task<CallToolResult> Call(string tool, IOneNoteGateway gateway, string query, string startNodeId, bool fromRoot) => tool switch
    {
        "find_pages" => FindPages(gateway).FindPages(query, startNodeId, fromRoot, Ct),
        "find_meta" => FindMeta(gateway).FindMeta(query, startNodeId, fromRoot, Ct),
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    private static string Page(string id, string name, string children = "") =>
        children.Length == 0
            ? $"<one:Page ID=\"{id}\" name=\"{name}\" isIndexed=\"false\"/>"
            : $"<one:Page ID=\"{id}\" name=\"{name}\" isIndexed=\"false\">{children}</one:Page>";

    private static string Hierarchy(params string[] pages) =>
        "<?xml version=\"1.0\"?>\n<one:Notebook " + Ns + " name=\"N\" ID=\"" + NotebookId + "\"><one:Section name=\"S\" ID=\"{s}\">"
        + string.Concat(pages) + "</one:Section></one:Notebook>";

    /// <summary>A minimal piBasic page: an empty title and one paragraph holding <paramref name="body"/> as CDATA.</summary>
    private static string PageXml(string body) =>
        "<?xml version=\"1.0\"?>\n<one:Page " + Ns + " ID=\"{p}\" name=\"Untitled\"><one:Title><one:OE><one:T><![CDATA[]]></one:T></one:OE></one:Title>"
        + "<one:Outline><one:OEChildren><one:OE><one:T><![CDATA[" + body + "]]></one:T></one:OE></one:OEChildren></one:Outline></one:Page>";

    private static List<FilteredPage> Filtered(JsonElement root) =>
        [.. root.GetProperty("meta").GetProperty("filtered").EnumerateArray()
            .Select(f => new FilteredPage(f.GetProperty("pageId").GetString()!, f.GetProperty("reason").GetString()!))];

    private static JsonElement Ok(CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        var root = result.StructuredContent.Value;
        Assert.False(result.IsError, root.GetRawText());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("meta").GetProperty("correlationId").GetString()));
        TextBlockAssert.Matches(result);
        return root;
    }

    private static JsonElement Error(CallToolResult result)
    {
        Assert.True(result.IsError);
        Assert.NotNull(result.StructuredContent);
        var root = result.StructuredContent.Value;
        Assert.False(root.GetProperty("ok").GetBoolean());
        AssertMetaHasOnly(root, "correlationId");
        return root.GetProperty("error");
    }

    private static void AssertMetaHasOnly(JsonElement root, params string[] names) =>
        Assert.Equal(names, root.GetProperty("meta").EnumerateObject().Select(p => p.Name));

    /// <summary>
    /// <see cref="FakeGateway"/> plus scripting: records search calls and page reads, answers searches with
    /// <see cref="SearchXml"/> when set, and serves the two Seed "Untitled" pages (no piBasic fixture) from a hand-built
    /// page with no "Fixture" in it. <see cref="PageXml"/> and <see cref="PageFailures"/> override page reads by ID.
    /// </summary>
    private sealed class SearchGateway : IOneNoteGateway
    {
        private readonly FakeGateway inner = new();

        public SearchGateway()
        {
            PageXml[SeedA] = SearchToolTests.PageXml(string.Empty);
            PageXml[SeedB] = SearchToolTests.PageXml(string.Empty);
        }

        public string? SearchXml { get; init; }

        public OneNoteException? SearchFailure { get; init; }

        public Dictionary<string, string> PageXml { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, OneNoteException> PageFailures { get; } = new(StringComparer.Ordinal);

        public List<(string Start, string Query, bool IncludeUnindexed)> FindPagesCalls { get; } = [];

        public List<(string Start, string Name, bool IncludeUnindexed)> FindMetaCalls { get; } = [];

        public List<(string PageId, PageInfo Info)> PageReads { get; } = [];

        /// <summary>The inner <see cref="FakeGateway"/>'s scripted hierarchies (AD-21's scope reads).</summary>
        public Dictionary<(string Id, HierarchyScope Scope), string> Hierarchies => inner.Hierarchies;

        public List<(string StartNodeId, HierarchyScope Scope)> HierarchyCalls => inner.Calls;

        public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct)
        {
            FindPagesCalls.Add((startNodeId, query, includeUnindexed));
            return Search(() => inner.FindPagesAsync(startNodeId, query, includeUnindexed, ct));
        }

        public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct)
        {
            FindMetaCalls.Add((startNodeId, name, includeUnindexed));
            return Search(() => inner.FindMetaAsync(startNodeId, name, includeUnindexed, ct));
        }

        public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct)
        {
            PageReads.Add((pageId, pageInfo));
            if (PageFailures.TryGetValue(pageId, out var failure))
            {
                return Task.FromException<string>(failure);
            }

            return PageXml.TryGetValue(pageId, out var xml) ? Task.FromResult(xml) : inner.GetPageContentAsync(pageId, pageInfo, ct);
        }

        public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct) => inner.GetHierarchyAsync(startNodeId, scope, ct);

        public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) => inner.GetHierarchyParentAsync(objectId, ct);

        public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) => inner.GetBinaryPageContentAsync(pageId, callbackId, ct);

        public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => inner.GetHyperlinkToObjectAsync(hierarchyId, pageContentObjectId, ct);

        public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => inner.GetWebHyperlinkToObjectAsync(hierarchyId, pageContentObjectId, ct);

        public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => inner.GetSpecialLocationAsync(location, ct);

        public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> CreateNewPageAsync(string sectionId, NewPageStyle style, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> CreateSectionAsync(string parentId, string sectionFileName, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> CreateSectionGroupAsync(string parentId, string groupName, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> CreateNotebookAsync(string folderPath, CancellationToken ct) => throw new NotSupportedException();

        public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct) => throw new NotSupportedException();

        public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct) => throw new NotSupportedException();

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => Task.CompletedTask;

        private Task<string> Search(Func<Task<string>> serve) =>
            SearchFailure is not null ? Task.FromException<string>(SearchFailure)
            : SearchXml is not null ? Task.FromResult(SearchXml)
            : serve();
    }
}
