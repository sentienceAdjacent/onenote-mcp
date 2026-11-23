using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 4.3's <c>sync_hierarchy</c> rows through tool, use case, the <c>AppState</c> pipeline and <see cref="FakeGateway"/>,
/// which records the call. The rows both <c>AppState</c> tools share are in <see cref="AppStatePipelineTests"/>. Nothing
/// here reaches real OneNote.
/// </summary>
public sealed class SyncHierarchyToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("{4B0B0000-0000-4000-8000-000000000001}{1}{B0}")] // a notebook
    [InlineData(FakeGateway.SyntheticSectionId)] // a section
    [InlineData("{C10D0000-0000-4000-8000-000000000001}{11}{E1000000000000000000000000000000000000000001}")] // a page
    public async Task AnyHierarchyId_IsOk_WithNoBody_AndSyncRequested_AndNoPreRead(string id)
    {
        var gateway = new FakeGateway();

        var root = Ok(await Tool(gateway).SyncHierarchy(id, Ct));

        Assert.Equal([id], gateway.SyncCalls);
        Assert.Empty(gateway.Calls); // no GetHierarchy
        Assert.Empty(gateway.PageCalls);
        Assert.False(root.TryGetProperty("xml", out _));
        Assert.Equal(["correlationId"], root.GetProperty("meta").EnumerateObject().Select(p => p.Name));
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal("syncRequested", warning.GetProperty("code").GetString());
        Assert.Equal(SyncHierarchyUseCase.RequestedMessage, warning.GetProperty("message").GetString());
        Assert.Contains("doesn't say when or whether the sync finishes", SyncHierarchyUseCase.RequestedMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownId_IsObjectNotFound_KeepsCodeHResultAndMessage()
    {
        var gateway = new FakeGateway { SyncFailure = new OneNoteException(ErrorCode.ObjectNotFound, "SyncHierarchy failed: hrObjectDoesNotExist (0x80042014).", unchecked((int)0x80042014)) };

        var result = await Tool(gateway).SyncHierarchy("{gone}", Ct);

        Assert.True(result.IsError);
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal("ObjectNotFound", error.GetProperty("code").GetString());
        Assert.Equal("0x80042014", error.GetProperty("hresult").GetString());
        Assert.Equal("SyncHierarchy failed: hrObjectDoesNotExist (0x80042014).", error.GetProperty("message").GetString());
        Assert.Equal(ToolErrors.UserAction(ErrorCode.ObjectNotFound), error.GetProperty("userAction").GetString());
    }

    [Fact]
    public async Task EmptyId_NamesTheArgument()
    {
        var gateway = new FakeGateway();

        var result = await Tool(gateway).SyncHierarchy(string.Empty, Ct);

        Assert.True(result.IsError);
        Assert.StartsWith("hierarchyId is empty", result.StructuredContent!.Value.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.SyncCalls);
    }

    [Fact]
    public void UseCase_IsAppState()
    {
        var gateway = new FakeGateway();
        Assert.Equal(OperationClass.AppState, new SyncHierarchyUseCase(gateway, new AppStatePipeline(gateway)).Class);
    }

    [Fact]
    public void Description_SaysOkMeansRequested_TheDeadline_AndOutcomeUnknown()
    {
        using var services = BuildServer();
        var sync = Assert.Single(services.GetServices<McpServerTool>().Select(t => t.ProtocolTool), t => t.Name == SyncHierarchyTool.Name);

        Assert.Contains("ok means OneNote accepted the request, not that a sync ran or finished", sync.Description, StringComparison.Ordinal);
        Assert.Contains("syncRequested", sync.Description, StringComparison.Ordinal);
        Assert.Contains("120 s deadline", sync.Description, StringComparison.Ordinal);
        Assert.Contains("outcomeUnknown", sync.Description, StringComparison.Ordinal);
        Assert.Equal(["hierarchyId"], sync.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    private static SyncHierarchyTool Tool(FakeGateway gateway) =>
        new(new ToolRunner(NullLogger<ToolRunner>.Instance), new SyncHierarchyUseCase(gateway, new AppStatePipeline(gateway)));

    private static ServiceProvider BuildServer()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddOneNoteMcp();
        return collection.BuildServiceProvider();
    }

    private static JsonElement Ok(CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        var root = result.StructuredContent.Value;
        Assert.False(result.IsError, root.GetRawText());
        Assert.True(root.GetProperty("ok").GetBoolean());
        TextBlockAssert.Matches(result);
        return root;
    }
}
