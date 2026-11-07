using System.Text.Json;
using Microsoft.Extensions.Logging;
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
/// Story 8's I/O matrix: <c>get_page_content</c> and <c>get_binary_page_content</c> through tool, use case and the
/// fixture-backed <see cref="FakeGateway"/>. XML and base64 are compared byte for byte against the captured fixtures
/// (AD-4), and <c>meta.basis</c> against <see cref="PageBasis.From"/> of the piBasic fixture (AD-17).
/// </summary>
public sealed class PageContentToolTests
{
    public const string NestedPageId = "{A20A4FC1-E5F2-4154-ADC8-8A513656DFEE}{1}{E19478355080928453447520102198986629748415351}";
    public const string ImagePageId = HierarchyReadToolTests.PageId;
    public const string ImageCallbackId = "{B870D211-3A03-48F1-BA23-6F34D7A02B73}{40}{B0}";
    public const string CloudPageId = "{C10D0000-0000-4000-8000-000000000001}{11}{E1000000000000000000000000000000000000000001}";
    public const string CloudCallbackId = "{C10D0000-0000-4000-8000-000000000001}{107}{B2}";
    public const string ZeroCallbackId = "{00000000-0000-0000-0000-000000000000}{1}{B0}";

    private const string Ns = OneNoteXml.Namespace2013;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ToolRunner Runner => new(NullLogger<ToolRunner>.Instance);

    // --- get_page_content -----------------------------------------------------------------------------------------

    [Fact]
    public async Task LocalPage_Default_ReturnsBasicXmlUnchanged_WithItsBasis()
    {
        var root = Ok(await PageTool(new FakeGateway()).GetPageContent(NestedPageId, cancellationToken: Ct));

        var basic = FakeGateway.ReadFixture("mcp-test/page/nested-Basic.xml");
        Assert.Equal(basic, root.GetProperty("xml").GetString());
        AssertBasis(PageBasis.From(basic), root);
        AssertMetaHasOnly(root, "correlationId", "basis");
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task LocalImagePage_Binary_ReturnsBinaryDataXmlUnchanged_BasisFromBasic_NoBinaryUnavailable()
    {
        var root = Ok(await PageTool(new FakeGateway()).GetPageContent(ImagePageId, includeBinary: true, Ct));

        Assert.Equal(FakeGateway.ReadFixture("mcp-test/page/image-BinaryData.xml"), root.GetProperty("xml").GetString());
        AssertBasis(PageBasis.From(FakeGateway.ReadFixture("mcp-test/page/image-Basic.xml")), root);
        AssertMetaHasOnly(root, "correlationId", "basis");
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task CloudPage_Binary_ReturnsBinaryDataXmlUnchanged_ReportsTheTwoMissingImages()
    {
        var root = Ok(await PageTool(new FakeGateway()).GetPageContent(CloudPageId, includeBinary: true, Ct));

        Assert.Equal(FakeGateway.ReadFixture("cloud/page-BinaryData.xml"), root.GetProperty("xml").GetString());
        AssertBasis(PageBasis.From(FakeGateway.ReadFixture("cloud/page-Basic.xml")), root);
        Assert.Equal(2, root.GetProperty("meta").GetProperty("binaryUnavailable").GetInt32());
        AssertMetaHasOnly(root, "correlationId", "basis", "binaryUnavailable");

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.BinaryUnavailable, warning.GetProperty("code").GetString());
        var message = warning.GetProperty("message").GetString()!;
        Assert.Contains(CloudPageId, message, StringComparison.Ordinal);
        Assert.Contains("2", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Cloud Test Page", message, StringComparison.Ordinal); // no content (AD-9)
        Assert.False(string.IsNullOrEmpty(warning.GetProperty("userAction").GetString()));
    }

    [Fact]
    public async Task CloudPage_Default_ReturnsBasicXml_WithBasis_NoWarning()
    {
        var root = Ok(await PageTool(new FakeGateway()).GetPageContent(CloudPageId, cancellationToken: Ct));

        var basic = FakeGateway.ReadFixture("cloud/page-Basic.xml");
        Assert.Equal(basic, root.GetProperty("xml").GetString());
        AssertBasis(PageBasis.From(basic), root);
        AssertMetaHasOnly(root, "correlationId", "basis");
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
    }

    [Theory]
    [InlineData(NestedPageId)]
    [InlineData(ImagePageId)]
    [InlineData(CloudPageId)]
    public async Task Basis_IsTheSame_WhateverIncludeBinaryIs(string pageId)
    {
        var tool = PageTool(new FakeGateway());

        var without = Ok(await tool.GetPageContent(pageId, includeBinary: false, Ct));
        var with = Ok(await tool.GetPageContent(pageId, includeBinary: true, Ct));

        Assert.Equal(Basis(without).GetRawText(), Basis(with).GetRawText());
    }

    [Theory]
    [InlineData(false, new[] { PageInfo.Basic })]
    [InlineData(true, new[] { PageInfo.Basic, PageInfo.BinaryData })]
    public async Task ReadOrder_BasicFirst_ThenBinaryData(bool includeBinary, PageInfo[] expected)
    {
        var gateway = new RecordingGateway(new FakeGateway());

        Ok(await PageTool(gateway).GetPageContent(ImagePageId, includeBinary, Ct));

        Assert.Equal(expected.Select(p => (ImagePageId, p)), gateway.PageReads);
    }

    [Fact]
    public async Task Basis_OnTheWire_IsLastModifiedTimePageHashAndElementHashes()
    {
        var root = Ok(await PageTool(new FakeGateway()).GetPageContent(NestedPageId, cancellationToken: Ct));

        Assert.Equal(["lastModifiedTime", "pageHash", "elementHashes"], Basis(root).EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Object, Basis(root).GetProperty("elementHashes").ValueKind);
    }

    [Fact]
    public async Task Basis_WithoutLastModifiedTime_OmitsIt()
    {
        const string page = $"<one:Page xmlns:one=\"{Ns}\" ID=\"{{p}}\"><one:Outline objectID=\"{{o}}\" /></one:Page>";
        var gateway = new ScriptedGateway(page);

        var root = Ok(await PageTool(gateway).GetPageContent("{p}", cancellationToken: Ct));

        Assert.Equal(page, root.GetProperty("xml").GetString());
        Assert.False(Basis(root).TryGetProperty("lastModifiedTime", out _));
        Assert.Equal(PageBasis.From(page).PageHash, Basis(root).GetProperty("pageHash").GetString());
        Assert.Equal(PageBasis.From(page).ElementHashes["{o}"], Basis(root).GetProperty("elementHashes").GetProperty("{o}").GetString());
    }

    [Theory]
    [InlineData("get_page_content")]
    [InlineData("get_binary_page_content")]
    public async Task UnknownPage_IsObjectNotFound(string tool)
    {
        var error = Error(tool == "get_page_content"
            ? await PageTool(new FakeGateway()).GetPageContent(HierarchyReadToolTests.UnknownId, cancellationToken: Ct)
            : await BinaryTool(new FakeGateway()).GetBinaryPageContent(HierarchyReadToolTests.UnknownId, ImageCallbackId, Ct));

        Assert.Equal("ObjectNotFound", error.GetProperty("code").GetString());
        Assert.Equal("0x80042014", error.GetProperty("hresult").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedPageXml_IsInvalidXml_AndTheMessageDoesNotQuoteIt(bool includeBinary)
    {
        const string secret = "SECRET-CONTENT";
        var gateway = new ScriptedGateway($"<one:Page xmlns:one=\"{Ns}\"><one:T>{secret}");

        var error = Error(await PageTool(gateway).GetPageContent("{p}", includeBinary, Ct));

        Assert.Equal("InvalidXml", error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.DoesNotContain(secret, error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedBinaryDataXml_IsInvalidXml_AndTheMessageDoesNotQuoteIt()
    {
        const string secret = "SECRET-CONTENT";
        var basic = $"<one:Page xmlns:one=\"{Ns}\" ID=\"{{p}}\" />";
        var gateway = new ScriptedGateway(basic, binaryData: $"<one:Page xmlns:one=\"{Ns}\"><one:T>{secret}");

        var error = Error(await PageTool(gateway).GetPageContent("{p}", includeBinary: true, Ct));

        Assert.Equal("InvalidXml", error.GetProperty("code").GetString());
        Assert.DoesNotContain(secret, error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageId_ReachesTheGateway_Unchanged()
    {
        const string odd = "  {weird}{1}{E1} ";
        var gateway = new RecordingGateway(new ScriptedGateway($"<one:Page xmlns:one=\"{Ns}\" />"));

        await PageTool(gateway).GetPageContent(odd, includeBinary: true, Ct);

        Assert.All(gateway.PageReads, r => Assert.Equal(odd, r.PageId));
    }

    // --- get_binary_page_content ----------------------------------------------------------------------------------

    [Fact]
    public async Task BinaryPresent_ReturnsTheBase64Unchanged()
    {
        var root = Ok(await BinaryTool(new FakeGateway()).GetBinaryPageContent(ImagePageId, ImageCallbackId, Ct));

        Assert.Equal(FakeGateway.ReadFixture("mcp-test/binary/image.b64.txt"), root.GetProperty("meta").GetProperty("binary").GetString());
        Assert.False(root.TryGetProperty("xml", out _));
        AssertMetaHasOnly(root, "correlationId", "binary");
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
    }

    [Theory]
    [InlineData(CloudPageId, CloudCallbackId)]
    [InlineData(ImagePageId, ZeroCallbackId)]
    public async Task BinaryMissing_IsOk_WithNoBinary_AndTheWarningNamesBothCauses(string pageId, string callbackId)
    {
        var logger = new CapturingLogger();

        var root = Ok(await new GetBinaryPageContentTool(new ToolRunner(logger), new GetBinaryPageContentUseCase(new FakeGateway()))
            .GetBinaryPageContent(pageId, callbackId, Ct));

        AssertMetaHasOnly(root, "correlationId");
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.BinaryUnavailable, warning.GetProperty("code").GetString());
        var message = warning.GetProperty("message").GetString()!;
        Assert.Contains(callbackId, message, StringComparison.Ordinal);
        Assert.Contains(pageId, message, StringComparison.Ordinal);
        Assert.Contains("this PC", message, StringComparison.Ordinal);
        Assert.Contains("callbackID is wrong", message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(warning.GetProperty("userAction").GetString()));

        // Degraded, not failed: ToolRunner logs the call as ok and never as an unmapped Internal failure.
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(logger.Entries, e => e.Message.Contains("result=ok", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OtherInternalErrors_PassThroughUnchanged()
    {
        var gateway = new FakeGateway { Failure = new OneNoteException(ErrorCode.Internal, "m", unchecked((int)0x80042003)) };

        var error = Error(await BinaryTool(gateway).GetBinaryPageContent(ImagePageId, ImageCallbackId, Ct));

        Assert.Equal("Internal", error.GetProperty("code").GetString());
        Assert.Equal("0x80042003", error.GetProperty("hresult").GetString());
    }

    [Fact]
    public async Task Ids_ReachTheBinaryGateway_Unchanged()
    {
        var gateway = new ScriptedGateway(string.Empty, binary: "AAAA");

        var root = Ok(await BinaryTool(gateway).GetBinaryPageContent(" {p} ", " {c} ", Ct));

        Assert.Equal((" {p} ", " {c} "), gateway.LastBinaryCall);
        Assert.Equal("AAAA", root.GetProperty("meta").GetProperty("binary").GetString());
    }

    // --- Shared ---------------------------------------------------------------------------------------------------

    [Fact]
    public void BothUseCases_AreReadClass()
    {
        var gateway = new FakeGateway();
        IUseCase[] useCases = [new GetPageContentUseCase(gateway), new GetBinaryPageContentUseCase(gateway)];

        Assert.All(useCases, u => Assert.Equal(OperationClass.Read, u.Class));
    }

    [Fact]
    public void Fixtures_ImageCountsDiffer_OnlyOnTheCloudPage()
    {
        // The ticket's unknown: Basic and BinaryData image counts are equal on every local fixture page.
        var pages = FakeGateway.Manifest.Entries
            .Where(e => e.Method == "GetPageContent" && e.Args[1] == "Basic" && e.File is not null)
            .Select(e => e.Args[0])
            .ToList();
        Assert.Contains(CloudPageId, pages);

        foreach (var pageId in pages)
        {
            var basic = FakeGateway.ReadFixture(Entry(pageId, "Basic"));
            var binary = FakeGateway.ReadFixture(Entry(pageId, "BinaryData"));
            Assert.Equal(pageId == CloudPageId ? 2 : 0, ImageCount.Missing(basic, binary));
        }
    }

    // --- Helpers --------------------------------------------------------------------------------------------------

    private static string Entry(string pageId, string pageInfo) =>
        FakeGateway.Manifest.Entries.Single(e => e.Method == "GetPageContent" && e.Args[0] == pageId && e.Args[1] == pageInfo).File!;

    private static GetPageContentTool PageTool(IOneNoteGateway gateway) => new(Runner, new GetPageContentUseCase(gateway));

    private static GetBinaryPageContentTool BinaryTool(IOneNoteGateway gateway) => new(Runner, new GetBinaryPageContentUseCase(gateway));

    private static JsonElement Basis(JsonElement root) => root.GetProperty("meta").GetProperty("basis");

    private static void AssertBasis(PageBasis expected, JsonElement root)
    {
        var basis = Basis(root);
        Assert.NotNull(expected.LastModifiedTime);
        Assert.Equal(expected.LastModifiedTime, basis.GetProperty("lastModifiedTime").GetString());
        Assert.Equal(expected.PageHash, basis.GetProperty("pageHash").GetString());
        var hashes = basis.GetProperty("elementHashes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        Assert.Equal(expected.ElementHashes.OrderBy(p => p.Key, StringComparer.Ordinal), hashes.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    private static JsonElement Ok(CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        var root = result.StructuredContent.Value;
        Assert.False(result.IsError, root.GetRawText());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.False(root.TryGetProperty("error", out _));
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

    /// <summary>Wraps a gateway and records every <c>GetPageContent</c> call in order.</summary>
    private sealed class RecordingGateway(IOneNoteGateway inner) : IOneNoteGateway
    {
        public List<(string PageId, PageInfo PageInfo)> PageReads { get; } = [];

        public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct)
        {
            PageReads.Add((pageId, pageInfo));
            return inner.GetPageContentAsync(pageId, pageInfo, ct);
        }

        public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) => inner.GetBinaryPageContentAsync(pageId, callbackId, ct);

        public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct) => inner.GetHierarchyAsync(startNodeId, scope, ct);

        public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) => inner.GetHierarchyParentAsync(objectId, ct);

        public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) => inner.FindPagesAsync(startNodeId, query, includeUnindexed, ct);

        public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) => inner.FindMetaAsync(startNodeId, name, includeUnindexed, ct);

        public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => inner.GetHyperlinkToObjectAsync(hierarchyId, pageContentObjectId, ct);

        public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => inner.GetWebHyperlinkToObjectAsync(hierarchyId, pageContentObjectId, ct);

        public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => inner.GetSpecialLocationAsync(location, ct);
    }

    /// <summary>Answers every page read with fixed text (BinaryData with <paramref name="binaryData"/> when given), for malformed and edge-case XML.</summary>
    private sealed class ScriptedGateway(string basic, string? binaryData = null, string binary = "") : IOneNoteGateway
    {
        public (string PageId, string CallbackId)? LastBinaryCall { get; private set; }

        public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct) =>
            Task.FromResult(pageInfo == PageInfo.BinaryData && binaryData is not null ? binaryData : basic);

        public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct)
        {
            LastBinaryCall = (pageId, callbackId);
            return Task.FromResult(binary);
        }

        public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILogger<ToolRunner>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
