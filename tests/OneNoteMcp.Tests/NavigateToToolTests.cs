using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core;
using OneNoteMcp.Core.AppState;
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

        Assert.Equal([(PageContentToolTests.CloudPageId, string.Empty, (int?)null)], gateway.NavigateCalls);
        Assert.False(root.TryGetProperty("xml", out _));
        Assert.False(root.TryGetProperty("index", out _));
        Assert.Equal(["correlationId"], root.GetProperty("meta").EnumerateObject().Select(p => p.Name));
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task ObjectOnAPage_PassesBothIdsUnchanged()
    {
        var gateway = new FakeGateway();

        Ok(await Tool(gateway).NavigateTo(" {p} ", " {oe} ", cancellationToken: Ct));

        Assert.Equal([(" {p} ", " {oe} ", (int?)null)], gateway.NavigateCalls);
    }

    [Fact]
    public async Task WithAWindow_PassesItsHandle_SoOnlyThatWindowMoves()
    {
        // Story 4.6 (Decision Q1): window is the integer handle get_windows returns.
        var gateway = new FakeGateway();
        gateway.Windows.Add(new OneNoteWindow(0x10, false, false, "{n}", string.Empty, "{s}", "{p}", false, -1, false));
        gateway.Windows.Add(new OneNoteWindow(0x20, true, true, "{n}", string.Empty, "{s}", "{p2}", false, -1, false));

        var root = Ok(await Tool(gateway).NavigateTo(PageContentToolTests.CloudPageId, window: 0x20, cancellationToken: Ct));

        Assert.Equal([(PageContentToolTests.CloudPageId, string.Empty, (int?)0x20)], gateway.NavigateCalls);
        Assert.Equal(["correlationId"], root.GetProperty("meta").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task WithAHandleNoWindowHas_IsInvalidArgument_AndNothingMoves()
    {
        var gateway = new FakeGateway();
        gateway.Windows.Add(new OneNoteWindow(0x10, true, true, "{n}", string.Empty, "{s}", "{p}", false, -1, false));

        var result = await Tool(gateway).NavigateTo(PageContentToolTests.CloudPageId, window: 0x99, cancellationToken: Ct);

        Assert.True(result.IsError);
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal("InvalidArgument", error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Equal(OneNoteWindow.UnknownHandleUserAction, error.GetProperty("userAction").GetString());
        Assert.Equal(OneNoteWindow.UnknownHandleMessage(0x99), error.GetProperty("message").GetString());
        Assert.Empty(gateway.NavigateCalls);
    }

    [Fact]
    public async Task AHandleWithBit31Set_MakesTheRoundTrip_AsANegativeNumber()
    {
        // Decision Q1: window handles fit in 32 bits; get_windows reports them signed, so the 32-bit argument check takes any.
        var gateway = new FakeGateway();
        gateway.Windows.Add(new OneNoteWindow(0xFFFF_FFFF_8000_0010, true, true, "{n}", string.Empty, "{s}", "{p}", false, -1, false));
        var handle = OneNoteWindow.HandleOf(0xFFFF_FFFF_8000_0010);
        Assert.True(handle < 0);

        Ok(await Tool(gateway).NavigateTo(PageContentToolTests.CloudPageId, window: handle, cancellationToken: Ct));

        Assert.Equal([(PageContentToolTests.CloudPageId, string.Empty, (int?)handle)], gateway.NavigateCalls);
    }

    [Fact]
    public async Task WithAWindow_WhileAnAbandonedWriteIsOutstanding_IsComBusy_AndNothingMoves()
    {
        var gateway = new FakeGateway { AbandonedWrites = new TaskCompletionSource() };
        gateway.Windows.Add(new OneNoteWindow(0x10, true, true, "{n}", string.Empty, "{s}", "{p}", false, -1, false));

        var result = await Tool(gateway).NavigateTo(PageContentToolTests.CloudPageId, window: 0x10, cancellationToken: Ct);

        Assert.Equal("ComBusy", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(gateway.NavigateCalls);
    }

    [Fact]
    public void Tool_TakesAnOptionalIntegerWindow_AndDescribesIt()
    {
        using var services = BuildServer();
        var navigate = Assert.Single(services.GetServices<McpServerTool>().Select(t => t.ProtocolTool), t => t.Name == NavigateToTool.Name);

        var window = navigate.InputSchema.GetProperty("properties").GetProperty("window");
        Assert.Equal(["integer", "null"], window.GetProperty("type").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(["hierarchyId"], navigate.InputSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
        Assert.Contains("Without window it uses OneNote's first open window.", navigate.Description, StringComparison.Ordinal);
        Assert.Contains("a handle no open window has fails with InvalidArgument", navigate.Description, StringComparison.Ordinal);
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
    public async Task ObjectDoesNotExist_GetsTheReReadUserAction_AndKeepsCodeAndMessage()
    {
        // Story 4.3 (spike 4.12 R1): no navigate repeated within 2 s failed, so the "wait two seconds" advice is gone (AD-9).
        var gateway = new FakeGateway { Failure = new OneNoteException(ErrorCode.ObjectNotFound, "NavigateTo failed: hrObjectDoesNotExist (0x80042014).", unchecked((int)0x80042014)) };

        var result = await Tool(gateway).NavigateTo(PageContentToolTests.CloudPageId, cancellationToken: Ct);

        Assert.True(result.IsError);
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal("ObjectNotFound", error.GetProperty("code").GetString());
        Assert.Equal("0x80042014", error.GetProperty("hresult").GetString());
        Assert.Equal("NavigateTo failed: hrObjectDoesNotExist (0x80042014).", error.GetProperty("message").GetString());
        Assert.Equal(NavigateToUseCase.ObjectNotFoundUserAction, error.GetProperty("userAction").GetString());
        Assert.Equal("Re-read the hierarchy and use a current ID.", NavigateToUseCase.ObjectNotFoundUserAction);
        Assert.DoesNotContain("wait two seconds", NavigateToUseCase.ObjectNotFoundUserAction, StringComparison.OrdinalIgnoreCase);
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
        var gateway = new FakeGateway();
        Assert.Equal(OperationClass.AppState, new NavigateToUseCase(gateway, new AppStatePipeline(gateway)).Class);
    }

    [Fact]
    public void Tool_SaysItIsUserVisible_AndIsTheOnlyUserVisibleToolThatIsNotReadOnly()
    {
        // The write tools (story 2.4 on) and sync_hierarchy (story 4.3) aren't read-only either; every other tool is.
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

        Assert.All(tools.Where(t => t.Name != NavigateToTool.Name && !WriteTools.Contains(t.Name) && !AppStateTools.Contains(t.Name)), t => Assert.True(t.Annotations!.ReadOnlyHint, t.Name));
        Assert.All(WriteTools.Concat(AppStateTools), name => Assert.False(Assert.Single(tools, t => t.Name == name).Annotations!.ReadOnlyHint, name));
    }

    /// <summary>The AppState tools other than navigate_to (story 4.3 on): not read-only, and not user-visible (spike 4.1 S1).</summary>
    internal static readonly string[] AppStateTools = [SyncHierarchyTool.Name];

    [Fact]
    public void SyncHierarchy_IsNotUserVisible_ButOnlyOnTheUsersRequest_AndIdempotent()
    {
        using var services = BuildServer();
        var sync = Assert.Single(services.GetServices<McpServerTool>().Select(t => t.ProtocolTool), t => t.Name == SyncHierarchyTool.Name);

        Assert.DoesNotContain("User-visible", sync.Description, StringComparison.Ordinal);
        Assert.Contains("Call only when the user asks for a sync; never on your own, after a write or otherwise.", sync.Description, StringComparison.Ordinal);
        Assert.False(sync.Annotations!.ReadOnlyHint);
        Assert.False(sync.Annotations.DestructiveHint);
        Assert.True(sync.Annotations.IdempotentHint);
    }

    [Fact]
    public void Tool_DescribesTheDeadline_AndOutcomeUnknown()
    {
        using var services = BuildServer();
        var navigate = Assert.Single(services.GetServices<McpServerTool>().Select(t => t.ProtocolTool), t => t.Name == NavigateToTool.Name);

        Assert.Contains("120 s deadline", navigate.Description, StringComparison.Ordinal);
        Assert.Contains("outcomeUnknown", navigate.Description, StringComparison.Ordinal);
    }

    /// <summary>The write tools: not read-only, and not user-visible either (they don't take focus).</summary>
    internal static readonly string[] WriteTools = [CreatePageTool.Name, UpdatePageContentTool.Name, DeletePageContentTool.Name, RestoreFromJournalTool.Name, CreateSectionTool.Name, CreateSectionGroupTool.Name, CreateNotebookTool.Name, RenameHierarchyObjectTool.Name, ReorderHierarchyChildrenTool.Name, MoveHierarchyObjectTool.Name, RestoreFromRecycleBinTool.Name, SetPageLevelTool.Name, MergeSectionsTool.Name, DeleteHierarchyObjectTool.Name, PermanentlyDeleteHierarchyObjectTool.Name, PublishTool.Name, OpenPackageTool.Name];

    [Fact]
    public void WriteTools_DontClaimToBeUserVisible()
    {
        using var services = BuildServer();
        foreach (var tool in services.GetServices<McpServerTool>().Select(t => t.ProtocolTool).Where(t => WriteTools.Contains(t.Name)))
        {
            Assert.DoesNotContain("User-visible", tool.Description, StringComparison.Ordinal);
        }
    }

    private static NavigateToTool Tool(IOneNoteGateway gateway) => new(new ToolRunner(NullLogger<ToolRunner>.Instance), new NavigateToUseCase(gateway, new AppStatePipeline(gateway)));

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
