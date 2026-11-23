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
/// Story 4.9's <c>quick_filing</c> I/O matrix through tool, use case, the <c>AppState</c> pipeline and <see cref="FakeGateway"/>,
/// whose pickers a test answers for the user. The gateway's own rows (the STA call, the hold, the callback) are in
/// <see cref="ComGatewayLifecycleTests"/>. Nothing here reaches real OneNote.
/// </summary>
public sealed class QuickFilingToolTests
{
    private const uint NoButton = QuickFilingAnswer.NoButton;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pick_IsOk_WithTheSectionsIdAndItsOwnElement_FromOneSelfRead()
    {
        var gateway = new FakeGateway { QuickFilingAnswer = new QuickFilingAnswer(FakeGateway.SyntheticSectionId, 0) };
        var logs = new List<QuickFilingLogEntry>();

        var root = Ok(await Tool(gateway, logs: logs).QuickFiling(null, null, Ct));

        Assert.Equal(FakeGateway.SyntheticSectionId, root.GetProperty("meta").GetProperty("objectId").GetString());
        Assert.Equal(gateway.Hierarchies[(FakeGateway.SyntheticSectionId, HierarchyScope.Self)], root.GetProperty("xml").GetString());
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        Assert.Equal([(FakeGateway.SyntheticSectionId, HierarchyScope.Self)], gateway.Calls);
        Assert.Equal([(QuickFilingUseCase.DefaultTitle, string.Empty)], gateway.QuickFilingCalls);
        Assert.Equal(1, gateway.PickerReleases);
        var log = Assert.Single(logs);
        Assert.Equal("picked", log.Outcome);
        Assert.Equal(FakeGateway.SyntheticSectionId, log.ChosenId);
        Assert.Equal("0x00000000", log.PressedButton);
        Assert.NotNull(log.RunMs);
        Assert.NotNull(log.WaitMs);
    }

    [Fact]
    public async Task Pick_WhoseReadFails_IsOk_WithTheIdOnly()
    {
        var gateway = new FakeGateway { QuickFilingAnswer = new QuickFilingAnswer("{gone}{1}{B0}", 0) };

        var root = Ok(await Tool(gateway).QuickFiling("Where should the notes go?", "Pick the section for meeting notes.", Ct));

        Assert.Equal("{gone}{1}{B0}", root.GetProperty("meta").GetProperty("objectId").GetString());
        Assert.False(root.TryGetProperty("xml", out _));
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        Assert.Equal([("Where should the notes go?", "Pick the section for meeting notes.")], gateway.QuickFilingCalls);
    }

    [Fact]
    public async Task CancelOrEsc_IsOk_WithPickerCancelled_AndNothingChosen()
    {
        // Spike 4.13 F5: Cancel and Esc both give no item and PressedButton 0xFFFFFFFF.
        var gateway = new FakeGateway { QuickFilingAnswer = new QuickFilingAnswer(string.Empty, NoButton) };
        var logs = new List<QuickFilingLogEntry>();

        var root = Ok(await Tool(gateway, logs: logs).QuickFiling(null, null, Ct));

        Assert.Equal(["correlationId"], root.GetProperty("meta").EnumerateObject().Select(p => p.Name));
        Assert.False(root.TryGetProperty("xml", out _));
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal("pickerCancelled", warning.GetProperty("code").GetString());
        Assert.Equal(QuickFilingUseCase.CancelledMessage, warning.GetProperty("message").GetString());
        Assert.Empty(gateway.Calls); // nothing read
        Assert.Equal("cancelled", Assert.Single(logs).Outcome);
        Assert.Equal("0xFFFFFFFF", logs[0].PressedButton);
    }

    [Fact]
    public async Task WaitEnds_IsOk_WithPickerLeftOpen_TheHoldAndSlotAreFreed_AndALateAnswerIsDroppedAndLogged()
    {
        var gateway = new FakeGateway();
        var logs = new List<QuickFilingLogEntry>();
        var tool = Tool(gateway, TimeSpan.FromMilliseconds(100), logs);

        var root = Ok(await tool.QuickFiling(null, null, Ct));

        Assert.Equal(["correlationId"], root.GetProperty("meta").EnumerateObject().Select(p => p.Name));
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal("pickerLeftOpen", warning.GetProperty("code").GetString());
        Assert.Contains("may still be on screen", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("ignored", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(QuickFilingUseCase.LeftOpenUserAction, warning.GetProperty("userAction").GetString());
        Assert.Equal(1, gateway.PickerReleases);
        Assert.True(gateway.Pickers[0].Released);
        Assert.Equal("leftOpen", Assert.Single(Snapshot(logs)).Outcome);

        // The slot is free: a new picker is allowed with the old one still on screen.
        gateway.QuickFilingAnswer = new QuickFilingAnswer(string.Empty, NoButton);
        Ok(await tool.QuickFiling(null, null, Ct));
        Assert.Equal(2, gateway.QuickFilingCalls.Count);

        // The old picker's late answer: dropped (nothing read, no error), logged.
        gateway.PendingAnswers[0].SetResult(new QuickFilingAnswer(FakeGateway.SyntheticSectionId, 0));
        await WaitUntil(() => Snapshot(logs).Exists(l => l.Outcome == "lateAnswer"));
        Assert.Empty(gateway.Calls);
        Assert.Equal(FakeGateway.SyntheticSectionId, Snapshot(logs).Single(l => l.Outcome == "lateAnswer").ChosenId);
    }

    [Fact]
    public async Task ClientCancelDuringTheWait_IsCancelled_Retryable_NotOutcomeUnknown_AndFreesTheSlotAndHold()
    {
        var gateway = new FakeGateway();
        var tool = Tool(gateway);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var call = tool.QuickFiling(null, null, cancel.Token);
        await WaitUntil(() => gateway.Pickers.Count == 1);
        Assert.False(call.IsCompleted);
        await cancel.CancelAsync();
        var result = await call;

        var error = Error(result);
        Assert.Equal("Cancelled", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Contains("may still be on screen", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, gateway.PickerReleases);

        gateway.QuickFilingAnswer = new QuickFilingAnswer(string.Empty, NoButton);
        Ok(await tool.QuickFiling(null, null, Ct));
        Assert.Equal(2, gateway.QuickFilingCalls.Count);
    }

    [Fact]
    public async Task SecondCallWhileOneWaits_IsComBusy_Retryable_BeforeCom()
    {
        var gateway = new FakeGateway();
        var logs = new List<QuickFilingLogEntry>();
        var tool = Tool(gateway, logs: logs);

        var first = tool.QuickFiling(null, null, Ct);
        await WaitUntil(() => gateway.Pickers.Count == 1);
        var second = await tool.QuickFiling("Another", null, Ct);

        var error = Error(second);
        Assert.Equal("ComBusy", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(QuickFilingUseCase.BusyUserAction, error.GetProperty("userAction").GetString());
        Assert.Single(gateway.QuickFilingCalls);
        Assert.Contains(Snapshot(logs), l => l.Outcome == "busy");

        gateway.PendingAnswers[0].SetResult(new QuickFilingAnswer(string.Empty, NoButton));
        Ok(await first);
    }

    [Fact]
    public async Task AbandonedWriteOutstanding_IsComBusy_BeforeCom()
    {
        var gateway = new FakeGateway { AbandonedWrites = new TaskCompletionSource() };

        var error = Error(await Tool(gateway).QuickFiling(null, null, Ct));

        Assert.Equal("ComBusy", error.GetProperty("code").GetString());
        Assert.Empty(gateway.QuickFilingCalls);
    }

    [Theory]
    [InlineData(ErrorCode.Timeout, null)]
    [InlineData(ErrorCode.ComUnavailable, null)]
    [InlineData(ErrorCode.Internal, unchecked((int)0x800706BE))]
    public async Task RunTimesOutOrDisconnects_IsOutcomeUnknown_AndFreesTheSlot(ErrorCode code, int? hresult)
    {
        var gateway = new FakeGateway { QuickFilingFailure = new OneNoteException(code, "QuickFiling failed.", hresult) };
        var tool = Tool(gateway);

        var error = Error(await tool.QuickFiling(null, null, Ct));

        Assert.Equal(code.ToString(), error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(ToolErrors.OutcomeUnknownUserAction, error.GetProperty("userAction").GetString());

        gateway.QuickFilingFailure = null;
        gateway.QuickFilingAnswer = new QuickFilingAnswer(string.Empty, NoButton);
        Ok(await tool.QuickFiling(null, null, Ct));
    }

    [Fact]
    public async Task ACancelWhileRunIsBeingSent_IsOutcomeUnknown()
    {
        var gateway = new FakeGateway();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        gateway.OnQuickFilingCall = async ct =>
        {
            await cancel.CancelAsync();
            ct.ThrowIfCancellationRequested();
        };

        var error = Error(await Tool(gateway).QuickFiling(null, null, cancel.Token));

        Assert.Equal("Cancelled", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
    }

    [Fact]
    public async Task ModalDialog_PassesThrough_NotOutcomeUnknown()
    {
        var gateway = new FakeGateway { QuickFilingFailure = new OneNoteException(ErrorCode.ModalDialogOpen, "QuickFiling failed: a dialog is open (0x80042030).", unchecked((int)0x80042030)) };

        var error = Error(await Tool(gateway).QuickFiling(null, null, Ct));

        Assert.Equal("ModalDialogOpen", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
    }

    [Fact]
    public async Task AGetterThatFailsInTheCallback_IsItsMappedError_NotOutcomeUnknown()
    {
        var gateway = new FakeGateway();
        var tool = Tool(gateway);

        var call = tool.QuickFiling(null, null, Ct);
        await WaitUntil(() => gateway.Pickers.Count == 1);
        gateway.PendingAnswers[0].SetException(new OneNoteException(ErrorCode.ModalDialogOpen, "QuickFilingDialog.OnDialogClosed failed.", unchecked((int)0x80042030)));
        var error = Error(await call);

        Assert.Equal("ModalDialogOpen", error.GetProperty("code").GetString());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Equal(1, gateway.PickerReleases);
    }

    [Theory]
    [InlineData(256, 0, "title is 256 characters long")]
    [InlineData(0, 1025, "description is 1025 characters long")]
    public async Task TooLongTitleOrDescription_IsInvalidArgument_BeforeCom(int titleLength, int descriptionLength, string expected)
    {
        var gateway = new FakeGateway();

        var error = Error(await Tool(gateway).QuickFiling(
            titleLength == 0 ? null : new string('t', titleLength),
            descriptionLength == 0 ? null : new string('d', descriptionLength),
            Ct));

        Assert.Equal("InvalidArgument", error.GetProperty("code").GetString());
        Assert.StartsWith(expected, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.QuickFilingCalls);
    }

    [Theory]
    [InlineData("two\nlines", null)]
    [InlineData(null, "a\ttab")]
    [InlineData("bell\a", null)]
    public async Task ControlCharacters_AreInvalidArgument_BeforeCom(string? title, string? description)
    {
        var gateway = new FakeGateway();

        var error = Error(await Tool(gateway).QuickFiling(title, description, Ct));

        Assert.Equal("InvalidArgument", error.GetProperty("code").GetString());
        Assert.Contains("control character", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.QuickFilingCalls);
    }

    [Fact]
    public async Task TheLongestTitleAndDescription_AreAccepted()
    {
        var gateway = new FakeGateway { QuickFilingAnswer = new QuickFilingAnswer(string.Empty, NoButton) };

        Ok(await Tool(gateway).QuickFiling(new string('t', 255), new string('d', 1024), Ct));

        Assert.Single(gateway.QuickFilingCalls);
    }

    [Fact]
    public async Task OtherToolsRunNormally_WhileAPickerWaits()
    {
        var gateway = new FakeGateway();
        var tool = Tool(gateway);

        var waiting = tool.QuickFiling(null, null, Ct);
        await WaitUntil(() => gateway.Pickers.Count == 1);
        var read = await new GetHierarchyUseCase(gateway).ExecuteAsync(new ToolCallContext("c"), FakeGateway.SyntheticSectionId, HierarchyScope.Self, Ct);

        Assert.NotNull(read.Xml);
        Assert.False(waiting.IsCompleted);
        gateway.PendingAnswers[0].SetResult(new QuickFilingAnswer(string.Empty, NoButton));
        Ok(await waiting);
    }

    [Fact]
    public async Task Logs_CarryLengths_NeverTheText()
    {
        var gateway = new FakeGateway { QuickFilingAnswer = new QuickFilingAnswer(string.Empty, NoButton) };
        var logs = new List<QuickFilingLogEntry>();

        Ok(await Tool(gateway, logs: logs).QuickFiling("Secret project", "Budget notes", Ct));

        var log = Assert.Single(logs);
        Assert.Equal("Secret project".Length, log.TitleLength);
        Assert.Equal("Budget notes".Length, log.DescriptionLength);
        Assert.DoesNotContain("Secret", log.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Budget", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UseCase_IsAppState_AndWaitsAFixed300s()
    {
        var gateway = new FakeGateway();
        var useCase = new QuickFilingUseCase(gateway, new AppStatePipeline(gateway));
        Assert.Equal(OperationClass.AppState, useCase.Class);
        Assert.Equal(TimeSpan.FromSeconds(300), useCase.WaitLimit);
        Assert.Equal(TimeSpan.FromSeconds(300), QuickFilingUseCase.DefaultWaitLimit);
    }

    [Fact]
    public void Description_SaysUserVisible_WhenToCall_TheWait_AndTheWarnings_AndPromisesNoModality()
    {
        using var services = BuildServer();
        var tool = Assert.Single(services.GetServices<McpServerTool>().Select(t => t.ProtocolTool), t => t.Name == QuickFilingTool.Name);

        Assert.StartsWith("User-visible", tool.Description, StringComparison.Ordinal);
        Assert.Contains("may bring OneNote forward", tool.Description, StringComparison.Ordinal);
        Assert.Contains("doesn't block OneNote", tool.Description, StringComparison.Ordinal);
        Assert.Contains("Call only when the user asks to choose a place", tool.Description, StringComparison.Ordinal);
        Assert.Contains("300 s", tool.Description, StringComparison.Ordinal);
        Assert.Contains("pickerCancelled", tool.Description, StringComparison.Ordinal);
        Assert.Contains("pickerLeftOpen", tool.Description, StringComparison.Ordinal);
        Assert.Contains("ComBusy", tool.Description, StringComparison.Ordinal);
        Assert.Contains("outcomeUnknown", tool.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("modal", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("focus", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["title", "description"], tool.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.False(tool.InputSchema.TryGetProperty("required", out var required) && required.GetArrayLength() > 0);
    }

    private static QuickFilingTool Tool(FakeGateway gateway, TimeSpan? waitLimit = null, List<QuickFilingLogEntry>? logs = null) =>
        new(
            new ToolRunner(NullLogger<ToolRunner>.Instance),
            new QuickFilingUseCase(gateway, new AppStatePipeline(gateway), waitLimit: waitLimit, log: logs is null ? null : e =>
            {
                lock (logs)
                {
                    logs.Add(e);
                }
            }));

    private static List<QuickFilingLogEntry> Snapshot(List<QuickFilingLogEntry> logs)
    {
        lock (logs)
        {
            return [.. logs];
        }
    }

    private static ServiceProvider BuildServer()
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddOneNoteMcp();
        return collection.BuildServiceProvider();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "Condition not met within 5 s.");
            await Task.Delay(10, Ct);
        }
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

    private static JsonElement Error(CallToolResult result)
    {
        Assert.True(result.IsError, result.StructuredContent?.GetRawText());
        return result.StructuredContent!.Value.GetProperty("error");
    }
}
