using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Server.Tools;
using Xunit;

namespace OneNoteMcp.Tests;

public sealed class GetHierarchyToolTests
{
    private const string NotebooksXml =
        "<?xml version=\"1.0\"?>\n<one:Notebooks xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\">"
        + "<one:Notebook name=\"A\" ID=\"{1}{1}{B0}\" path=\"https://example/a\" lastModifiedTime=\"2026-11-03T00:00:00.000Z\" />"
        + "</one:Notebooks>";

    [Fact]
    public async Task HappyPath_ReturnsNotebooksXmlUnchanged_AtNotebooksScope()
    {
        var gateway = new FakeGateway { HierarchyXml = NotebooksXml };
        var tool = new GetHierarchyTool(gateway);

        var result = await tool.GetHierarchy(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        Assert.Equal(NotebooksXml, SingleText(result));
        Assert.Equal([(string.Empty, HierarchyScope.Notebooks)], gateway.Calls);
    }

    [Fact]
    public async Task NotRunning_ReturnsIsErrorStartingWithCode()
    {
        var gateway = new FakeGateway
        {
            Failure = new OneNoteException(ErrorCode.OneNoteNotRunning, "OneNote is not running. Start OneNote desktop, then retry."),
        };

        var result = await new GetHierarchyTool(gateway).GetHierarchy(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.StartsWith("OneNoteNotRunning", SingleText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComFailure_ReturnsInternalWithHexHResult()
    {
        var gateway = new FakeGateway
        {
            Failure = new OneNoteException(ErrorCode.Internal, "GetHierarchy failed with HRESULT 0x80004005.", unchecked((int)0x80004005)),
        };

        var result = await new GetHierarchyTool(gateway).GetHierarchy(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        var text = SingleText(result);
        Assert.StartsWith("Internal", text, StringComparison.Ordinal);
        Assert.Contains("0x80004005", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadStartId_PassesIdThrough_AndReturnsHResult()
    {
        var gateway = new FakeGateway
        {
            Failure = new OneNoteException(ErrorCode.Internal, "GetHierarchy failed with HRESULT 0x80042014.", unchecked((int)0x80042014)),
        };

        var result = await new GetHierarchyTool(gateway).GetHierarchy("{nope}", TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("0x80042014", SingleText(result), StringComparison.Ordinal);
        Assert.Equal([("{nope}", HierarchyScope.Notebooks)], gateway.Calls);
    }

    [Fact]
    public async Task UnexpectedException_NeverEscapesTheTool()
    {
        var tool = new GetHierarchyTool(new ThrowingGateway());

        var result = await tool.GetHierarchy(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.StartsWith("Internal", SingleText(result), StringComparison.Ordinal);
    }

    private static string SingleText(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private sealed class ThrowingGateway : IOneNoteGateway
    {
        public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }
}
