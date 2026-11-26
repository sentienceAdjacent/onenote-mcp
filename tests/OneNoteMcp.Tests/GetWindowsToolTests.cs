using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using Xunit;
using V = OneNoteMcp.Tests.VaultGateway;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 4.6's <c>get_windows</c> rows through tool, use case, the <c>AppState</c> pipeline and a fake gateway: the window
/// list, empty IDs left out (spike 4.12 W5), no window (W6), AD-21's withheld page, and <c>ComBusy</c> while an abandoned
/// write is outstanding. Nothing here reaches real OneNote.
/// </summary>
public sealed class GetWindowsToolTests : IDisposable
{
    private const string NotebookId = "{4B0B0000-0000-4000-8000-000000000001}{1}{B0}";
    private const string PageId = "{C10D0000-0000-4000-8000-000000000001}{11}{E1000000000000000000000000000000000000000001}";

    private readonly TempDirectory temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task OneWindow_IsCurrent_WithItsIds_AndOneSectionRead()
    {
        var gateway = new FakeGateway();
        gateway.Windows.Add(new OneNoteWindow(0x5A01F2, true, true, NotebookId, string.Empty, FakeGateway.SyntheticSectionId, PageId, false, -1, false));

        var root = Ok(await Tool(gateway).GetWindows(Ct));

        var window = Assert.Single(root.GetProperty("meta").GetProperty("windows").EnumerateArray());
        Assert.Equal(0x5A01F2, window.GetProperty("handle").GetInt32());
        Assert.True(window.GetProperty("current").GetBoolean());
        Assert.True(window.GetProperty("active").GetBoolean());
        Assert.False(window.GetProperty("fullPageView").GetBoolean());
        Assert.Equal("default", window.GetProperty("dockedLocation").GetString());
        Assert.False(window.GetProperty("sideNote").GetBoolean());
        Assert.Equal(NotebookId, window.GetProperty("notebookId").GetString());
        Assert.False(window.TryGetProperty("sectionGroupId", out _)); // "" is absent, never an ID (W5)
        Assert.Equal(FakeGateway.SyntheticSectionId, window.GetProperty("sectionId").GetString());
        Assert.Equal(PageId, window.GetProperty("pageId").GetString());
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        Assert.Equal([(FakeGateway.SyntheticSectionId, HierarchyScope.Self)], gateway.Calls);
        Assert.Equal(["correlationId", "windows"], root.GetProperty("meta").EnumerateObject().Select(p => p.Name));
        Assert.False(root.TryGetProperty("xml", out _));
    }

    [Fact]
    public async Task TwoWindows_InItemOrder_OneCurrent_EachSectionReadOnce()
    {
        var gateway = new FakeGateway();
        gateway.Windows.Add(new OneNoteWindow(0x10, false, false, NotebookId, string.Empty, FakeGateway.SyntheticSectionId, PageId, false, 0, false));
        gateway.Windows.Add(new OneNoteWindow(0x20, true, true, NotebookId, "{G}{1}{B0}", FakeGateway.SyntheticSectionId, PageId, true, 2, true));

        var root = Ok(await Tool(gateway).GetWindows(Ct));

        var windows = root.GetProperty("meta").GetProperty("windows").EnumerateArray().ToList();
        Assert.Equal([0x10L, 0x20L], windows.Select(w => w.GetProperty("handle").GetInt64()));
        Assert.Equal([false, true], windows.Select(w => w.GetProperty("current").GetBoolean()));
        Assert.Equal(["none", "right"], windows.Select(w => w.GetProperty("dockedLocation").GetString()));
        Assert.True(windows[1].GetProperty("sideNote").GetBoolean());
        Assert.True(windows[1].GetProperty("fullPageView").GetBoolean());
        Assert.Equal("{G}{1}{B0}", windows[1].GetProperty("sectionGroupId").GetString());
        Assert.Single(gateway.Calls); // the shared section is read once
    }

    [Fact]
    public async Task NoWindow_IsAnEmptyList_AndOk()
    {
        var gateway = new FakeGateway();

        var root = Ok(await Tool(gateway).GetWindows(Ct));

        Assert.Equal(0, root.GetProperty("meta").GetProperty("windows").GetArrayLength());
        Assert.Equal(1, gateway.WindowsCalls);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task LockedSection_ReportsTheSection_WithNoPageId_AndNoSectionRead()
    {
        // Spike 4.12 W5: CurrentPageId is "" in a locked section.
        var gateway = new FakeGateway();
        gateway.Windows.Add(new OneNoteWindow(0x10, true, true, NotebookId, string.Empty, FakeGateway.SyntheticSectionId, string.Empty, false, -1, false));

        var root = Ok(await Tool(gateway).GetWindows(Ct));

        var window = Assert.Single(root.GetProperty("meta").GetProperty("windows").EnumerateArray());
        Assert.Equal(FakeGateway.SyntheticSectionId, window.GetProperty("sectionId").GetString());
        Assert.False(window.TryGetProperty("pageId", out _));
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        Assert.Empty(gateway.Calls);
    }

    [Theory]
    [InlineData(false)] // password-protected
    [InlineData(true)] // protected by its path (entry 15)
    public async Task ProtectedPage_NoGrant_ReportsTheSection_WithoutThePageId_AndOneReadNotGrantedWithTheCount(bool byPath)
    {
        using var h = new VaultHarness(byPath: byPath);
        h.Gateway.Windows.Add(new OneNoteWindow(0x10, false, false, V.NotebookId, string.Empty, V.VaultId, V.VaultPageId, false, -1, false));
        h.Gateway.Windows.Add(new OneNoteWindow(0x20, true, true, V.NotebookId, V.GroupId, V.GroupVaultId, V.GroupVaultPageId, false, -1, false));
        h.Gateway.Windows.Add(new OneNoteWindow(0x30, false, false, V.NotebookId, string.Empty, V.OpenSectionId, V.OpenPageId, false, -1, false));
        var prompt = new FakePrompt(FakePrompt.Accept(V.VaultName, ReadGrantLifetime.Once));

        var success = await UseCase(h).ExecuteAsync(new ToolCallContext("c", Prompt: prompt), Ct);

        var windows = success.Meta.Windows!;
        Assert.Equal([V.VaultId, V.GroupVaultId, V.OpenSectionId], windows.Select(w => w.SectionId));
        Assert.Equal([null, null, V.OpenPageId], windows.Select(w => w.PageId));
        var warning = Assert.Single(success.Warnings);
        Assert.Equal(ToolWarnings.ReadNotGranted, warning.Code);
        Assert.Equal(WindowPages.WithheldMessage(2), warning.Message);
        Assert.StartsWith("2 window(s)", warning.Message, StringComparison.Ordinal);
        Assert.Equal(ReadAccess.WithheldUserAction, warning.UserAction);
        Assert.DoesNotContain(V.VaultPageId, warning.Message, StringComparison.Ordinal);
        Assert.Empty(prompt.Questions); // never prompted (AD-21)
        Assert.Empty(h.Gateway.ContentCalls);
    }

    [Fact]
    public async Task ProtectedPage_StoredGrantHeldAtTheStart_ReportsThePageId()
    {
        using var h = new VaultHarness();
        h.Writer.SaveReadGrant(new ProtectedSectionGrant(
            GrantKeys.SectionKey(V.VaultPath), V.VaultName, GrantKeys.NotebookKey(V.NotebookPath), V.VaultId, h.Clock.GetUtcNow(), GrantedVia.Cli, null));
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Gateway.Windows.Add(new OneNoteWindow(0x10, true, true, V.NotebookId, string.Empty, V.VaultId, V.VaultPageId, false, -1, false));

        var success = await UseCase(h).ExecuteAsync(new ToolCallContext("c"), Ct);

        Assert.Equal(V.VaultPageId, Assert.Single(success.Meta.Windows!).PageId);
        Assert.Empty(success.Warnings);
    }

    [Fact]
    public async Task ProtectedPage_LiveOnceWindow_ReportsThePageId()
    {
        using var h = new VaultHarness();
        var vault = Xml.HierarchyNode.Root(await h.Gateway.GetHierarchyAsync(V.VaultId, HierarchyScope.Self, Ct));
        var prompt = new FakePrompt(FakePrompt.Accept(V.VaultName, ReadGrantLifetime.Once));
        Assert.NotNull((await h.Access.CheckSectionAsync(new ToolCallContext("read", Prompt: prompt), vault, Ct)).Grant);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Gateway.Windows.Add(new OneNoteWindow(0x10, true, true, V.NotebookId, string.Empty, V.VaultId, V.VaultPageId, false, -1, false));

        var success = await UseCase(h).ExecuteAsync(new ToolCallContext("c"), Ct);

        Assert.Equal(V.VaultPageId, Assert.Single(success.Meta.Windows!).PageId);
        Assert.Empty(success.Warnings);
    }

    [Fact]
    public async Task APageWhoseSectionIsGone_IsLeftOut_FailingClosed()
    {
        var gateway = new FakeGateway();
        gateway.Windows.Add(new OneNoteWindow(0x10, true, true, NotebookId, string.Empty, "{GONE}{1}{B0}", PageId, false, -1, false));
        gateway.Windows.Add(new OneNoteWindow(0x20, false, false, NotebookId, string.Empty, string.Empty, PageId, false, -1, false));

        var root = Ok(await Tool(gateway).GetWindows(Ct));

        Assert.All(root.GetProperty("meta").GetProperty("windows").EnumerateArray(), w => Assert.False(w.TryGetProperty("pageId", out _)));
        Assert.Equal(WindowPages.WithheldMessage(2), Assert.Single(root.GetProperty("warnings").EnumerateArray()).GetProperty("message").GetString());
    }

    [Fact]
    public async Task AbandonedWriteOutstanding_IsComBusy_AndNoWindowIsRead()
    {
        // Story 4.6 acceptance (AD-3, AD-7 AppState): ComBusy at the pipeline's start.
        var gateway = new FakeGateway { AbandonedWrites = new TaskCompletionSource() };
        gateway.Windows.Add(new OneNoteWindow(0x10, true, true, NotebookId, string.Empty, FakeGateway.SyntheticSectionId, PageId, false, -1, false));

        var result = await Tool(gateway).GetWindows(Ct);

        Assert.True(result.IsError);
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal("ComBusy", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(0, gateway.WindowsCalls);
        Assert.Empty(gateway.Calls);
    }

    [Theory]
    [InlineData(ErrorCode.Timeout, null)]
    [InlineData(ErrorCode.ComUnavailable, 0x800706BAu)]
    [InlineData(ErrorCode.Cancelled, null)]
    public async Task Failures_AreRetryableAsARead_AndNeverOutcomeUnknown(ErrorCode code, uint? hresult)
    {
        // Spine AD-7 AppState and AD-9: reading the windows changes nothing (spike 4.12 W2).
        var gateway = new FakeGateway { WindowsFailure = new OneNoteException(code, "m", hresult is { } hr ? unchecked((int)hr) : null) };

        var result = await Tool(gateway).GetWindows(Ct);

        Assert.True(result.IsError);
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal(code.ToString(), error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
    }

    [Fact]
    public async Task ACancelWhileReading_IsCancelled_Retryable_AndNotOutcomeUnknown()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var gateway = new FakeGateway
        {
            OnWindowsCall = async ct =>
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
            },
        };

        var result = await Tool(gateway).GetWindows(cts.Token);

        Assert.True(result.IsError);
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal("Cancelled", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
    }

    [Fact]
    public void UseCase_IsAppState()
    {
        var gateway = new FakeGateway();
        Assert.Equal(OperationClass.AppState, UseCase(gateway).Class);
    }

    [Fact]
    public void Tool_IsReadOnly_Idempotent_NotUserVisible_AndOnlyOnTheUsersRequest_WithNoArguments()
    {
        using var services = BuildServer();
        var tool = Assert.Single(services.GetServices<McpServerTool>().Select(t => t.ProtocolTool), t => t.Name == GetWindowsTool.Name);

        Assert.True(tool.Annotations!.ReadOnlyHint);
        Assert.True(tool.Annotations.IdempotentHint);
        Assert.False(tool.Annotations.DestructiveHint);
        Assert.False(tool.Annotations.OpenWorldHint);
        Assert.DoesNotContain("User-visible", tool.Description, StringComparison.Ordinal);
        Assert.Contains("Call it only when the user asks, or refers to what they're looking at in OneNote", tool.Description, StringComparison.Ordinal);
        Assert.Contains("readNotGranted", tool.Description, StringComparison.Ordinal);
        Assert.Contains("navigate_to's window", tool.Description, StringComparison.Ordinal);
        Assert.Empty(tool.InputSchema.GetProperty("properties").EnumerateObject());
    }

    [Theory]
    [InlineData(-1, "default")]
    [InlineData(0, "none")]
    [InlineData(1, "left")]
    [InlineData(2, "right")]
    [InlineData(3, "top")]
    [InlineData(4, "bottom")]
    [InlineData(7, "7")]
    public void DockedLocation_IsNamed(int value, string name) =>
        Assert.Equal(name, WindowPages.DockedLocationName(value));

    private GetWindowsTool Tool(FakeGateway gateway) =>
        new(new ToolRunner(NullLogger<ToolRunner>.Instance), UseCase(gateway));

    private GetWindowsUseCase UseCase(FakeGateway gateway)
    {
        var store = new SettingsStore(temp.Combine("settings.json"), temp.Combine("journal"));
        var clock = new FakeClock();
        return new GetWindowsUseCase(gateway, new AppStatePipeline(gateway), new ReadAccess(gateway, store.Read, new SettingsWriter(store, clock), clock));
    }

    private static GetWindowsUseCase UseCase(VaultHarness h) => new(h.Gateway, new AppStatePipeline(h.Gateway), h.Access);

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
