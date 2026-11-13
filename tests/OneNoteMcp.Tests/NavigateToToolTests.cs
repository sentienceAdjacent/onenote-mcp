using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 1.21's <c>navigate_to</c> rows: the user-visible <c>NavigateTo</c> (AD-11) through tool, use case and
/// <see cref="FakeGateway"/>, which records the call. Nothing here reaches real OneNote.
/// </summary>
public sealed class NavigateToToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ShowAPage_IsOk_WithNoBody_AndPassesThePageId()
    {
        var gateway = new FakeGateway();

        var root = Ok(await Tool(gateway).NavigateTo(PageContentToolTests.CloudPageId, cancellationToken: Ct));

        Assert.Equal([(PageContentToolTests.CloudPageId, string.Empty)], gateway.NavigateCalls);
        Assert.False(root.TryGetProperty("xml", out _));
        Assert.False(root.TryGetProperty("index", out _));
        Assert.Equal(["correlationId"], root.GetProperty("meta").EnumerateObject().Select(p => p.Name));
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task ObjectOnAPage_PassesBothIdsUnchanged()
    {
        var gateway = new FakeGateway();

        Ok(await Tool(gateway).NavigateTo(" {p} ", " {oe} ", Ct));

        Assert.Equal([(" {p} ", " {oe} ")], gateway.NavigateCalls);
    }

    [Theory]
    [InlineData(ErrorCode.ObjectNotFound, 0x80042014u, false)]
    [InlineData(ErrorCode.ModalDialogOpen, 0x80042030u, true)]
    [InlineData(ErrorCode.Timeout, null, false)] // AppState, not Read: a timeout isn't retryable (AD-9)
    [InlineData(ErrorCode.ComUnavailable, 0x800706BAu, false)]
    public async Task Errors_MapAsForEveryCall(ErrorCode code, uint? hresult, bool retryable)
    {
        var gateway = new FakeGateway { Failure = new OneNoteException(code, "m", hresult is { } h ? unchecked((int)h) : null) };

        var result = await Tool(gateway).NavigateTo(PageContentToolTests.CloudPageId, cancellationToken: Ct);

        Assert.True(result.IsError);
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal(code.ToString(), error.GetProperty("code").GetString());
        Assert.Equal(retryable, error.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task ObjectDoesNotExist_GetsTheRepeatNavigateUserAction_AndKeepsCodeAndMessage()
    {
        // OneMore row 14: a navigate repeated within about 2 s can fail with 0x80042014 (inferred for NavigateTo).
        var gateway = new FakeGateway { Failure = new OneNoteException(ErrorCode.ObjectNotFound, "NavigateTo failed: hrObjectDoesNotExist (0x80042014).", unchecked((int)0x80042014)) };

        var result = await Tool(gateway).NavigateTo(PageContentToolTests.CloudPageId, cancellationToken: Ct);

        Assert.True(result.IsError);
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal("ObjectNotFound", error.GetProperty("code").GetString());
        Assert.Equal("0x80042014", error.GetProperty("hresult").GetString());
        Assert.Equal("NavigateTo failed: hrObjectDoesNotExist (0x80042014).", error.GetProperty("message").GetString());
        Assert.Equal(NavigateToUseCase.ObjectNotFoundUserAction, error.GetProperty("userAction").GetString());
        Assert.Contains("wait two seconds and try once more", NavigateToUseCase.ObjectNotFoundUserAction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherObjectNotFound_KeepsTheDefaultUserAction()
    {
        var gateway = new FakeGateway { Failure = new OneNoteException(ErrorCode.ObjectNotFound, "m", unchecked((int)0x80042005)) };

        var result = await Tool(gateway).NavigateTo(PageContentToolTests.CloudPageId, cancellationToken: Ct);

        Assert.Equal(
            Core.Results.ToolErrors.UserAction(ErrorCode.ObjectNotFound),
            result.StructuredContent!.Value.GetProperty("error").GetProperty("userAction").GetString());
    }

    [Fact]
    public void UseCase_IsAppState()
    {
        Assert.Equal(OperationClass.AppState, new NavigateToUseCase(new FakeGateway()).Class);
    }

    [Fact]
    public void Tool_SaysItIsUserVisible_AndIsTheOnlyNonWriteToolThatIsNotReadOnly()
    {
        // The write tools (story 2.4 on) aren't read-only either; every other tool is.
        using var services = BuildServer();
        var tools = services.GetServices<McpServerTool>().Select(t => t.ProtocolTool).ToList();

        var navigate = Assert.Single(tools, t => t.Name == NavigateToTool.Name);
        Assert.StartsWith(
            "User-visible: takes keyboard focus and changes what OneNote shows. Call only when the user explicitly asks.",
            navigate.Description,
            StringComparison.Ordinal);
        Assert.False(navigate.Annotations!.ReadOnlyHint);
        Assert.False(navigate.Annotations.DestructiveHint);
        Assert.True(navigate.Annotations.IdempotentHint);
        Assert.False(navigate.Annotations.OpenWorldHint);

        Assert.All(tools.Where(t => t.Name != NavigateToTool.Name && !WriteTools.Contains(t.Name)), t => Assert.True(t.Annotations!.ReadOnlyHint, t.Name));
        Assert.All(WriteTools, name => Assert.False(Assert.Single(tools, t => t.Name == name).Annotations!.ReadOnlyHint, name));
    }

    /// <summary>The write tools: not read-only, and not user-visible either (they don't take focus).</summary>
    internal static readonly string[] WriteTools = [CreatePageTool.Name, UpdatePageContentTool.Name, DeletePageContentTool.Name, RestoreFromJournalTool.Name, CreateSectionTool.Name, CreateSectionGroupTool.Name, CreateNotebookTool.Name, RenameHierarchyObjectTool.Name, ReorderHierarchyChildrenTool.Name, DeleteHierarchyObjectTool.Name];

    [Fact]
    public void WriteTools_DontClaimToBeUserVisible()
    {
        using var services = BuildServer();
        foreach (var tool in services.GetServices<McpServerTool>().Select(t => t.ProtocolTool).Where(t => WriteTools.Contains(t.Name)))
        {
            Assert.DoesNotContain("User-visible", tool.Description, StringComparison.Ordinal);
        }
    }

    private static NavigateToTool Tool(IOneNoteGateway gateway) => new(new ToolRunner(NullLogger<ToolRunner>.Instance), new NavigateToUseCase(gateway));

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
