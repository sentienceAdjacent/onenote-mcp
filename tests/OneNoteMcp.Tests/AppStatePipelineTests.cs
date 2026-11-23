using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 4.3: AD-7's <c>AppState</c> pipeline, through both tools on it (<c>sync_hierarchy</c> and <c>navigate_to</c>) and
/// <see cref="FakeGateway"/>, one test per row of the plan's I/O matrix, plus the pipeline's own rows with a scripted
/// operation (a deadline before step 7, a call that changes nothing). Nothing here reaches real OneNote.
/// </summary>
public sealed class AppStatePipelineTests
{
    public static readonly TheoryData<string> Tools = new() { SyncHierarchyTool.Name, NavigateToTool.Name };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task EmptyId_IsInvalidArgument_WithNoComCall(string tool)
    {
        var gateway = new FakeGateway();

        var error = Error(await CallAsync(tool, gateway, string.Empty), "InvalidArgument");

        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(0, ComCalls(tool, gateway));
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task AbandonedWriteOutstanding_IsComBusy_Retryable_WithNoComCall(string tool)
    {
        var gateway = new FakeGateway { AbandonedWrites = new TaskCompletionSource() };

        var error = Error(await CallAsync(tool, gateway, "{s}"), "ComBusy");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Equal(0, ComCalls(tool, gateway));

        // Checked at the start: an empty ID with a write outstanding is ComBusy too, and nothing is sent.
        Error(await CallAsync(tool, gateway, string.Empty), "ComBusy");

        // Once it settles, the call goes through.
        gateway.AbandonedWrites.SetResult();
        Ok(await CallAsync(tool, gateway, "{s}"));
        Assert.Equal(1, ComCalls(tool, gateway));
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task UnknownOrStaleId_IsObjectNotFound_WithTheReReadUserAction(string tool)
    {
        var gateway = Failing(tool, new OneNoteException(ErrorCode.ObjectNotFound, "m", unchecked((int)0x80042014)));

        var error = Error(await CallAsync(tool, gateway, "{gone}"), "ObjectNotFound");

        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Equal("0x80042014", error.GetProperty("hresult").GetString());
        Assert.StartsWith("Re-read the hierarchy", error.GetProperty("userAction").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, ComCalls(tool, gateway));
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task DialogOpen_IsModalDialogOpen_Retryable_NotOutcomeUnknown(string tool)
    {
        var gateway = Failing(tool, new OneNoteException(ErrorCode.ModalDialogOpen, "m", unchecked((int)0x80042030)));

        var error = Error(await CallAsync(tool, gateway, "{s}"), "ModalDialogOpen");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Equal(ToolErrors.UserAction(ErrorCode.ModalDialogOpen), error.GetProperty("userAction").GetString());
    }

    public static readonly TheoryData<string, ErrorCode, int?> AmbiguousFailures = Ambiguous();

    [Theory]
    [MemberData(nameof(AmbiguousFailures))]
    public async Task AmbiguousStep7Failure_KeepsItsCode_IsOutcomeUnknown_AndNotRetryable(string tool, ErrorCode code, int? hresult)
    {
        var gateway = Failing(tool, new OneNoteException(code, "m", hresult));

        var error = Error(await CallAsync(tool, gateway, "{s}"), code.ToString());

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(ToolErrors.OutcomeUnknownUserAction, error.GetProperty("userAction").GetString());
        Assert.Equal(1, ComCalls(tool, gateway)); // never retried
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task DeadlineDuringStep7_IsTimeout_OutcomeUnknown(string tool)
    {
        var gateway = new FakeGateway();
        SetHook(tool, gateway, ct => Task.Delay(Timeout.Infinite, ct));

        var error = Error(await CallAsync(tool, gateway, "{s}", deadline: TimeSpan.FromMilliseconds(200)), "Timeout");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Contains("while OneNote was handling it", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public async Task ClientCancelDuringStep7_IsCancelled_OutcomeUnknown(string tool)
    {
        var gateway = new FakeGateway();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        SetHook(tool, gateway, async ct =>
        {
            await cancel.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
        });

        var error = Error(await CallAsync(tool, gateway, "{s}", ct: cancel.Token), "Cancelled");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task SyncOk_CarriesSyncRequested_AndPassesTheIdUnchanged()
    {
        var gateway = new FakeGateway();

        var root = Ok(await CallAsync(SyncHierarchyTool.Name, gateway, " {n} "));

        Assert.Equal([" {n} "], gateway.SyncCalls);
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.SyncRequested, warning.GetProperty("code").GetString());
    }

    // --- The pipeline alone, with a scripted operation -----------------------------------------------------------

    [Fact]
    public async Task DeadlineBeforeStep7_IsTimeout_Retryable_AndNothingIsSent()
    {
        var pipeline = new AppStatePipeline(new FakeGateway(), deadline: TimeSpan.FromMilliseconds(200));
        var operation = new Scripted { Read = ct => Task.Delay(Timeout.Infinite, ct) };

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => pipeline.RunAsync(operation, Ct));

        Assert.Equal(ErrorCode.Timeout, ex.Code);
        Assert.False(ex.OutcomeUnknown);
        Assert.True(ex.Retryable);
        Assert.Contains("nothing was sent", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, operation.Calls);
    }

    [Fact]
    public async Task ClientCancelBeforeStep7_StaysACancellation_AndNothingIsSent()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var operation = new Scripted
        {
            Read = async ct =>
            {
                await cancel.CancelAsync();
                ct.ThrowIfCancellationRequested();
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AppStatePipeline(new FakeGateway()).RunAsync(operation, cancel.Token));
        Assert.Equal(0, operation.Calls);
    }

    [Fact]
    public async Task StepsRunInOrder_ComBusyFirst_ThenValidate_ThenRead_ThenCall()
    {
        var order = new List<string>();
        var operation = new Scripted { OnValidate = () => order.Add("validate"), Read = _ => { order.Add("read"); return Task.CompletedTask; } };
        operation.Call = _ => { order.Add("call"); return Task.FromResult(true); };

        Assert.True(await new AppStatePipeline(new FakeGateway()).RunAsync(operation, Ct));
        Assert.Equal(["validate", "read", "call"], order);

        order.Clear();
        var busy = await Assert.ThrowsAsync<OneNoteException>(() => new AppStatePipeline(new FakeGateway { AbandonedWrites = new TaskCompletionSource() }).RunAsync(operation, Ct));
        Assert.Equal(ErrorCode.ComBusy, busy.Code);
        Assert.Empty(order);
    }

    [Fact]
    public async Task ACallThatChangesNothing_IsNeverOutcomeUnknown()
    {
        // get_windows' reads (spike 4.12 W2): a step-7 timeout is a plain timeout, retryable as a read's.
        var pipeline = new AppStatePipeline(new FakeGateway(), deadline: TimeSpan.FromMilliseconds(200));
        var timeout = new Scripted { ChangesStateValue = false, Call = _ => throw new OneNoteException(ErrorCode.Timeout, "m") };
        var hung = new Scripted { ChangesStateValue = false, Call = async ct => { await Task.Delay(Timeout.Infinite, ct); return true; } };

        Assert.False((await Assert.ThrowsAsync<OneNoteException>(() => pipeline.RunAsync(timeout, Ct))).OutcomeUnknown);
        var deadline = await Assert.ThrowsAsync<OneNoteException>(() => pipeline.RunAsync(hung, Ct));
        Assert.Equal(ErrorCode.Timeout, deadline.Code);
        Assert.False(deadline.OutcomeUnknown);
        Assert.True(deadline.Retryable);
    }

    [Fact]
    public void Deadline_Is120Seconds_ByDefault()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), new AppStatePipeline(new FakeGateway()).Deadline);
    }

    // --- Helpers ----------------------------------------------------------------------------------------------------

    private static TheoryData<string, ErrorCode, int?> Ambiguous()
    {
        var data = new TheoryData<string, ErrorCode, int?>();
        foreach (var tool in new[] { SyncHierarchyTool.Name, NavigateToTool.Name })
        {
            data.Add(tool, ErrorCode.Timeout, null);
            data.Add(tool, ErrorCode.ComUnavailable, unchecked((int)0x800706BA));
            data.Add(tool, ErrorCode.Cancelled, null);
            data.Add(tool, ErrorCode.Internal, unchecked((int)0x800706BE)); // RPC_S_CALL_FAILED: OneNote may have crashed
        }

        return data;
    }

    private static Task<CallToolResult> CallAsync(string tool, FakeGateway gateway, string id, TimeSpan? deadline = null, CancellationToken? ct = null)
    {
        var runner = new ToolRunner(NullLogger<ToolRunner>.Instance);
        var pipeline = new AppStatePipeline(gateway, deadline: deadline);
        return tool == SyncHierarchyTool.Name
            ? new SyncHierarchyTool(runner, new SyncHierarchyUseCase(gateway, pipeline)).SyncHierarchy(id, ct ?? Ct)
            : new NavigateToTool(runner, new NavigateToUseCase(gateway, pipeline)).NavigateTo(id, cancellationToken: ct ?? Ct);
    }

    private static FakeGateway Failing(string tool, OneNoteException failure)
    {
        var gateway = new FakeGateway();
        if (tool == SyncHierarchyTool.Name)
        {
            gateway.SyncFailure = failure;
        }
        else
        {
            gateway.NavigateFailure = failure;
        }

        return gateway;
    }

    private static void SetHook(string tool, FakeGateway gateway, Func<CancellationToken, Task> hook)
    {
        if (tool == SyncHierarchyTool.Name)
        {
            gateway.OnSyncCall = hook;
        }
        else
        {
            gateway.OnNavigateCall = hook;
        }
    }

    private static int ComCalls(string tool, FakeGateway gateway) =>
        tool == SyncHierarchyTool.Name ? gateway.SyncCalls.Count : gateway.NavigateCalls.Count;

    private static JsonElement Ok(CallToolResult result)
    {
        Assert.NotNull(result.StructuredContent);
        var root = result.StructuredContent.Value;
        Assert.False(result.IsError, root.GetRawText());
        Assert.True(root.GetProperty("ok").GetBoolean());
        TextBlockAssert.Matches(result);
        return root;
    }

    private static JsonElement Error(CallToolResult result, string code)
    {
        Assert.True(result.IsError);
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        return error;
    }

    private sealed class Scripted : AppStateOperation<bool>
    {
        public Action? OnValidate { get; init; }

        public Func<CancellationToken, Task>? Read { get; init; }

        public Func<CancellationToken, Task<bool>>? Call { get; set; }

        public bool ChangesStateValue { get; init; } = true;

        public int Calls { get; private set; }

        public override bool ChangesState => ChangesStateValue;

        public override void Validate() => OnValidate?.Invoke();

        public override Task ReadAsync(CancellationToken ct) => Read?.Invoke(ct) ?? Task.CompletedTask;

        public override Task<bool> CallAsync(CancellationToken ct)
        {
            Calls++;
            return Call?.Invoke(ct) ?? Task.FromResult(true);
        }
    }
}
