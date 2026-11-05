using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>The story-3 I/O matrix for <c>get_hierarchy</c>, through the real runner and envelope (AD-9).</summary>
public sealed class GetHierarchyToolTests
{
    private const string NotebooksXml =
        "<?xml version=\"1.0\"?>\n<one:Notebooks xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\">"
        + "<one:Notebook name=\"A &amp; B\" ID=\"{1}{1}{B0}\" path=\"https://example/a\" lastModifiedTime=\"2026-11-03T00:00:00.000Z\" />"
        + "</one:Notebooks>";

    [Fact]
    public async Task HappyPath_ReturnsOkEnvelope_WithXmlByteIdentical()
    {
        var gateway = new FakeGateway { HierarchyXml = NotebooksXml };

        var result = await Tool(gateway).GetHierarchy(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        var root = Structured(result);
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(NotebooksXml, root.GetProperty("xml").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("warnings").ValueKind);
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("meta").GetProperty("correlationId").GetString()));
        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal([(string.Empty, HierarchyScope.Notebooks)], gateway.Calls);
    }

    [Fact]
    public async Task TextBlock_CarriesTheSameJson()
    {
        var result = await Tool(new FakeGateway { HierarchyXml = NotebooksXml }).GetHierarchy(cancellationToken: TestContext.Current.CancellationToken);

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Equal(result.StructuredContent!.Value.GetRawText(), text);
        using var parsed = JsonDocument.Parse(text);
        Assert.Equal(NotebooksXml, parsed.RootElement.GetProperty("xml").GetString());
    }

    [Fact]
    public async Task EachCall_GetsItsOwnCorrelationId()
    {
        var tool = Tool(new FakeGateway { HierarchyXml = NotebooksXml });

        var a = Structured(await tool.GetHierarchy(cancellationToken: TestContext.Current.CancellationToken));
        var b = Structured(await tool.GetHierarchy(cancellationToken: TestContext.Current.CancellationToken));

        Assert.NotEqual(CorrelationId(a), CorrelationId(b));
    }

    [Fact]
    public async Task StartNodeId_ReachesTheGatewayUnchanged()
    {
        var gateway = new FakeGateway { HierarchyXml = NotebooksXml };

        await Tool(gateway).GetHierarchy("{nope}", TestContext.Current.CancellationToken);

        Assert.Equal([("{nope}", HierarchyScope.Notebooks)], gateway.Calls);
    }

    [Fact]
    public async Task NotRunning_IsRetryableError_WithoutHResult()
    {
        var gateway = new FakeGateway
        {
            Failure = new OneNoteException(ErrorCode.OneNoteNotRunning, "OneNote is not running. Start OneNote desktop, then retry."),
        };

        var result = await Tool(gateway).GetHierarchy(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        var root = Structured(result);
        Assert.False(root.GetProperty("ok").GetBoolean());
        var error = root.GetProperty("error");
        Assert.Equal("OneNoteNotRunning", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Equal("Start OneNote desktop, then retry.", error.GetProperty("userAction").GetString());
        Assert.Equal("OneNote is not running. Start OneNote desktop, then retry.", error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("hresult", out _));
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.False(string.IsNullOrEmpty(CorrelationId(root)));
        Assert.False(root.TryGetProperty("xml", out _));
        Assert.False(root.TryGetProperty("warnings", out _));
    }

    [Fact]
    public async Task MappedHResult_FromCom_IsObjectNotFound_NamingTheHResult()
    {
        var error = await ErrorFromCom(unchecked((int)0x80042014), "{nope}");

        Assert.Equal("ObjectNotFound", error.GetProperty("code").GetString());
        Assert.Equal("0x80042014", error.GetProperty("hresult").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Contains("hrObjectDoesNotExist", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("Re-read the hierarchy or page and use a current ID.", error.GetProperty("userAction").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnmappedHResult_FromCom_IsInternal_NamingTheHResult()
    {
        var error = await ErrorFromCom(unchecked((int)0x80004005));

        Assert.Equal("Internal", error.GetProperty("code").GetString());
        Assert.Equal("0x80004005", error.GetProperty("hresult").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Contains("E_FAIL", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("Report this error, with its HRESULT if it has one.", error.GetProperty("userAction").GetString());
    }

    [Fact]
    public async Task UnknownHResult_FromCom_IsInternal_WithHexOnly()
    {
        var error = await ErrorFromCom(unchecked((int)0x80041234));

        Assert.Equal("Internal", error.GetProperty("code").GetString());
        Assert.Equal("0x80041234", error.GetProperty("hresult").GetString());
        Assert.Equal("GetHierarchy failed: 0x80041234.", error.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(ErrorCode.ComUnavailable)]
    [InlineData(ErrorCode.Timeout)]
    public async Task ComUnavailableAndTimeout_AreRetryable_ForThisReadTool(ErrorCode code)
    {
        var gateway = new FakeGateway { Failure = new OneNoteException(code, "m") };

        var error = Structured(await Tool(gateway).GetHierarchy(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("error");

        Assert.True(error.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task OutcomeUnknown_IsCarried_NotRetryable_WithFixedUserAction()
    {
        var gateway = new FakeGateway
        {
            Failure = new OneNoteException(ErrorCode.ComUnavailable, "m", unchecked((int)0x800706BA), outcomeUnknown: true),
        };

        var error = Structured(await Tool(gateway).GetHierarchy(cancellationToken: TestContext.Current.CancellationToken)).GetProperty("error");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal("Re-read, then retry if the change is absent.", error.GetProperty("userAction").GetString());
    }

    [Fact]
    public async Task UnexpectedException_NeverEscapesTheTool()
    {
        var result = await Tool(new ThrowingGateway(new InvalidOperationException("boom"))).GetHierarchy(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        var error = Structured(result).GetProperty("error");
        Assert.Equal("Internal", error.GetProperty("code").GetString());
        Assert.Equal("Unexpected InvalidOperationException.", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Cancellation_IsInternalEnvelope_NotAThrow()
    {
        var result = await Tool(new ThrowingGateway(new OperationCanceledException())).GetHierarchy(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal("Internal", Structured(result).GetProperty("error").GetProperty("code").GetString());
    }

    private static GetHierarchyTool Tool(IOneNoteGateway gateway) =>
        new(new ToolRunner(NullLogger<ToolRunner>.Instance), new GetHierarchyUseCase(gateway));

    private static async Task<JsonElement> ErrorFromCom(int hresult, string startNodeId = "")
    {
        var app = new FakeApplication(() => throw new COMException("com", hresult));
        using var gateway = new ComGateway(new FixedProbe(true), () => app);

        var result = await Tool(gateway).GetHierarchy(startNodeId, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        return Structured(result).GetProperty("error");
    }

    private static JsonElement Structured(CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        return result.StructuredContent.Value;
    }

    private static string? CorrelationId(JsonElement root) => root.GetProperty("meta").GetProperty("correlationId").GetString();

    private sealed class ThrowingGateway(Exception exception) : IOneNoteGateway
    {
        public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct) => throw exception;

        public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) => throw exception;

        public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct) => throw exception;

        public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) => throw exception;

        public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) => throw exception;

        public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) => throw exception;

        public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => throw exception;

        public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => throw exception;

        public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => throw exception;
    }

    private sealed class FixedProbe(bool running) : IProcessProbe
    {
        public bool IsOneNoteRunning() => running;

        public IntegrityReport CheckIntegrity() => new(0x2000, []);
    }
}
