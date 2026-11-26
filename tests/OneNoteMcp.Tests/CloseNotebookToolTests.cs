using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OneNoteMcp.Core;
using OneNoteMcp.Core.AppState;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 4.4's <c>close_notebook</c>, one test per row of the plan's I/O matrix, through tool, use case, the
/// <c>AppState</c> pipeline with its write lock (a test mutex of its own) and <see cref="FakeGateway"/>. The settle is a
/// test's, which logs itself into <see cref="FakeGateway.AppStateLog"/> beside the sync and the close. Nothing here reaches
/// real OneNote.
/// </summary>
public sealed class CloseNotebookToolTests
{
    private const string LocalId = "{4B0B0000-0000-4000-8000-0000000044A1}{1}{B0}";
    private const string CloudId = "{4B0B0000-0000-4000-8000-0000000044B2}{1}{B0}";
    private const string SectionId = "{5EC70000-0000-4000-8000-0000000044C3}{1}{B0}";
    private const string GroupId = "{56000000-0000-4000-8000-0000000044D4}{1}{B0}";
    private const string PageId = "{5EC70000-0000-4000-8000-0000000044C3}{11}{E1000000000000000000000000000000000000004405}";
    private const string LocalPath = @"C:\Notebooks\Notebook A";
    private const string CloudPath = "https://d.docs.live.net/0000000000000000/Documents/Notebook B";
    private const string Ns = OneNoteXml.Namespace2013;

    private static readonly string NotebooksXml =
        "<?xml version=\"1.0\"?>\n<one:Notebooks xmlns:one=\"" + Ns + "\">"
        + "<one:Notebook name=\"Notebook A\" nickname=\"Notebook A\" ID=\"" + LocalId + "\" path=\"" + LocalPath + "\" lastModifiedTime=\"2026-11-22T10:00:00.000Z\" color=\"#ADE792\" />"
        + "<one:Notebook name=\"Notebook B\" nickname=\"Notebook B\" ID=\"" + CloudId + "\" path=\"" + CloudPath + "\" lastModifiedTime=\"2026-11-22T10:00:00.000Z\" />"
        + "</one:Notebooks>";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CloseOk_Local_SyncsSettlesThenCloses_AndReturnsTheNotebookElement_MetaAndNotebookClosed()
    {
        var gateway = Gateway();
        var writeLock = AppStatePipelineTests.TestLock();

        var root = Ok(await Tool(gateway, writeLock).CloseNotebook(LocalId, Ct));

        Assert.Equal(["sync " + LocalId, "settle 10", "close " + LocalId], gateway.AppStateLog);
        Assert.Equal((string.Empty, HierarchyScope.Notebooks), Assert.Single(gateway.Calls));
        var xml = root.GetProperty("xml").GetString()!;
        Assert.Equal(HierarchyNode.NotebookElement(NotebooksXml, LocalId), xml);
        var element = System.Xml.Linq.XElement.Parse(xml);
        Assert.Equal(OneNoteXml.One + "Notebook", element.Name);
        Assert.Equal("Notebook A", (string?)element.Attribute("name"));
        Assert.Equal(LocalPath, (string?)element.Attribute("path"));
        Assert.Equal(LocalId, (string?)element.Attribute("ID"));
        Assert.Empty(element.Elements());
        var meta = root.GetProperty("meta");
        Assert.Equal(["correlationId", "objectId", "path"], meta.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(LocalId, meta.GetProperty("objectId").GetString());
        Assert.Equal(LocalPath, meta.GetProperty("path").GetString());
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.NotebookClosed, warning.GetProperty("code").GetString());
        Assert.Equal(CloseNotebookUseCase.ClosedMessage, warning.GetProperty("message").GetString());
        Assert.Equal(CloseNotebookUseCase.ReopenLocalUserAction, warning.GetProperty("userAction").GetString());
        Assert.Contains("waited about 10 s", CloseNotebookUseCase.ClosedMessage, StringComparison.Ordinal);
        Assert.Contains("ID in the notebook changes", CloseNotebookUseCase.ClosedMessage, StringComparison.Ordinal);
        Assert.Contains("view may now be blank", CloseNotebookUseCase.ClosedMessage, StringComparison.Ordinal);
        Assert.Contains("grants are kept", CloseNotebookUseCase.ClosedMessage, StringComparison.Ordinal);
        Assert.Contains("open_notebook", CloseNotebookUseCase.ReopenLocalUserAction, StringComparison.Ordinal);

        // Step 8 released the lock.
        Assert.True(await AppStatePipelineTests.CanTakeAsync(writeLock));
    }

    [Fact]
    public async Task CloseOk_Cloud_GivesTheReopenInOneNoteRoute()
    {
        var gateway = Gateway();

        var root = Ok(await Tool(gateway).CloseNotebook(CloudId, Ct));

        Assert.Equal(["sync " + CloudId, "settle 10", "close " + CloudId], gateway.AppStateLog);
        Assert.Equal(CloudPath, root.GetProperty("meta").GetProperty("path").GetString());
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(CloseNotebookUseCase.ReopenCloudUserAction, warning.GetProperty("userAction").GetString());
    }

    [Theory]
    [InlineData("HTTP://d.docs.live.net/0000000000000000/Documents/Notebook B")]
    [InlineData("http://d.docs.live.net/0000000000000000/Documents/Notebook B")]
    public async Task CloseOk_TheCloudRoute_FollowsThePathsScheme_InAnyCase(string path)
    {
        var gateway = new FakeGateway();
        gateway.Hierarchies[(string.Empty, HierarchyScope.Notebooks)] = NotebooksXml.Replace(CloudPath, path, StringComparison.Ordinal);

        var root = Ok(await Tool(gateway).CloseNotebook(CloudId, Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(CloseNotebookUseCase.ReopenCloudUserAction, warning.GetProperty("userAction").GetString());
    }

    [Fact]
    public async Task TheLockIsHeld_FromTheReadThroughTheClose()
    {
        var gateway = Gateway();
        var writeLock = AppStatePipelineTests.TestLock();
        var heldAtSync = false;
        var heldAtClose = false;
        gateway.OnSyncCall = async _ => heldAtSync = !await AppStatePipelineTests.CanTakeAsync(writeLock);
        gateway.OnCloseCall = async _ => heldAtClose = !await AppStatePipelineTests.CanTakeAsync(writeLock);

        Ok(await Tool(gateway, writeLock).CloseNotebook(LocalId, Ct));

        Assert.True(heldAtSync);
        Assert.True(heldAtClose);
    }

    public static readonly TheoryData<string, string> NonNotebooks = new()
    {
        { SectionId, "<one:Section xmlns:one=\"" + Ns + "\" name=\"S\" ID=\"" + SectionId + "\" path=\"C:\\Notebooks\\Notebook A\\S.one\" />" },
        { GroupId, "<one:SectionGroup xmlns:one=\"" + Ns + "\" name=\"G\" ID=\"" + GroupId + "\" path=\"C:\\Notebooks\\Notebook A\\G\" />" },
        { PageId, "<one:Page xmlns:one=\"" + Ns + "\" name=\"P\" ID=\"" + PageId + "\" />" },
    };

    [Theory]
    [MemberData(nameof(NonNotebooks))]
    public async Task ASectionGroupOrPageId_IsInvalidArgument_NoSyncNoClose(string id, string selfXml)
    {
        var gateway = Gateway();
        gateway.Hierarchies[(id, HierarchyScope.Self)] = selfXml;

        var error = Error(await Tool(gateway).CloseNotebook(id, Ct), "InvalidArgument");

        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Equal(CloseNotebookUseCase.ReReadUserAction, error.GetProperty("userAction").GetString());
        Assert.Contains("not a notebook", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.AppStateLog);
    }

    [Fact]
    public async Task AnUnknownOrOldId_IsObjectNotFound_NoSyncNoClose()
    {
        var gateway = Gateway(); // no fixture for the ID at self: ObjectNotFound, as OneNote answers an unknown ID

        var error = Error(await Tool(gateway).CloseNotebook("{0LD00000-0000-4000-8000-000000004400}{1}{B0}", Ct), "ObjectNotFound");

        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Equal(CloseNotebookUseCase.ReReadUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.AppStateLog);
    }

    [Fact]
    public async Task ANotebookElementThatIsntOpen_IsObjectNotFound()
    {
        // GetHierarchy at self answers with a notebook that the open list doesn't have: not an open notebook.
        var gateway = Gateway();
        const string id = "{4B0B0000-0000-4000-8000-0000000044FF}{1}{B0}";
        gateway.Hierarchies[(id, HierarchyScope.Self)] = "<one:Notebook xmlns:one=\"" + Ns + "\" name=\"N\" ID=\"" + id + "\" path=\"C:\\N\" />";

        Error(await Tool(gateway).CloseNotebook(id, Ct), "ObjectNotFound");
        Assert.Empty(gateway.AppStateLog);
    }

    [Fact]
    public async Task AnEmptyId_IsInvalidArgument_AtStep1_NothingRead()
    {
        var gateway = Gateway();

        var error = Error(await Tool(gateway).CloseNotebook(string.Empty, Ct), "InvalidArgument");

        Assert.StartsWith("notebookId is empty", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(CloseNotebookUseCase.ReReadUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.Calls);
        Assert.Empty(gateway.AppStateLog);
    }

    [Fact]
    public async Task AnAbandonedWriteOutstanding_IsComBusy_Retryable_NothingReadUnderTheLock()
    {
        var gateway = Gateway();
        gateway.AbandonedWrites = new TaskCompletionSource();
        var writeLock = AppStatePipelineTests.TestLock();

        var error = Error(await Tool(gateway, writeLock).CloseNotebook(LocalId, Ct), "ComBusy");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Empty(gateway.Calls);
        Assert.Empty(gateway.AppStateLog);
        Assert.True(await AppStatePipelineTests.CanTakeAsync(writeLock));
    }

    public static readonly TheoryData<ErrorCode, int?> SyncFailures = new()
    {
        { ErrorCode.Timeout, null },
        { ErrorCode.ComUnavailable, unchecked((int)0x800706BA) },
        { ErrorCode.ModalDialogOpen, unchecked((int)0x80042030) },
        { ErrorCode.ObjectNotFound, unchecked((int)0x80042014) },
    };

    [Theory]
    [MemberData(nameof(SyncFailures))]
    public async Task TheSyncFails_ItsCode_NotOutcomeUnknown_NoClose_LockReleasedAtOnce(ErrorCode code, int? hresult)
    {
        var gateway = Gateway();
        gateway.SyncFailure = new OneNoteException(code, "SyncHierarchy failed.", hresult);
        var writeLock = AppStatePipelineTests.TestLock();

        var error = Error(await Tool(gateway, writeLock).CloseNotebook(LocalId, Ct), code.ToString());

        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Contains("wasn't closed", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        if (code is ErrorCode.Timeout or ErrorCode.ComUnavailable)
        {
            Assert.True(error.GetProperty("retryable").GetBoolean()); // nothing was closed
        }

        Assert.Equal(["sync " + LocalId], gateway.AppStateLog);
        Assert.Empty(gateway.CloseCalls);
        Assert.True(await AppStatePipelineTests.CanTakeAsync(writeLock));
    }

    [Fact]
    public async Task ACancelInTheSettle_IsCancelled_NotOutcomeUnknown_NoClose()
    {
        var gateway = Gateway();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var writeLock = AppStatePipelineTests.TestLock();

        var error = Error(
            await Tool(gateway, writeLock, settle: async (_, ct) =>
            {
                await cancel.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
            }).CloseNotebook(LocalId, cancel.Token),
            "Cancelled");

        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Empty(gateway.CloseCalls);
        Assert.True(await AppStatePipelineTests.CanTakeAsync(writeLock));
    }

    [Fact]
    public async Task TheDeadlineInTheSettle_IsTimeout_Retryable_NotOutcomeUnknown_NoClose()
    {
        var gateway = Gateway();

        var error = Error(
            await Tool(gateway, deadline: TimeSpan.FromMilliseconds(300), settle: (_, ct) => Task.Delay(Timeout.Infinite, ct)).CloseNotebook(LocalId, Ct),
            "Timeout");

        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Contains("before anything that changes OneNote's state was sent", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.CloseCalls);
    }

    public static readonly TheoryData<ErrorCode, int?> AmbiguousCloseFailures = new()
    {
        { ErrorCode.Timeout, null },
        { ErrorCode.ComUnavailable, unchecked((int)0x800706BA) },
        { ErrorCode.Cancelled, null },
        { ErrorCode.Internal, unchecked((int)0x800706BE) },
    };

    [Theory]
    [MemberData(nameof(AmbiguousCloseFailures))]
    public async Task AnAmbiguousCloseFailure_IsOutcomeUnknown_NeverRetried(ErrorCode code, int? hresult)
    {
        var gateway = Gateway();
        gateway.CloseFailure = new OneNoteException(code, "CloseNotebook failed.", hresult);

        var error = Error(await Tool(gateway).CloseNotebook(LocalId, Ct), code.ToString());

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(ToolErrors.OutcomeUnknownUserAction, error.GetProperty("userAction").GetString());
        Assert.Contains("close it in OneNote", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Single(gateway.CloseCalls);
    }

    [Fact]
    public async Task ATimedOutClose_KeepsTheLockUntilItSettles_AndAWriteInsideTheDeadlineWaitsThenRuns()
    {
        // The plan's first acceptance criterion: what ComGateway does for a CloseNotebook that times out is to track it as
        // an abandoned write; the pipeline then keeps the lock, and a write waits on it until the close settles.
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.Combine("settings.json"), temp.Combine("journal"));
        var gateway = Gateway();
        var writeLock = AppStatePipelineTests.TestLock();
        var abandoned = new TaskCompletionSource();
        gateway.OnCloseCall = _ =>
        {
            gateway.AbandonedWrites = abandoned;
            return Task.CompletedTask;
        };
        gateway.CloseFailure = new OneNoteException(ErrorCode.Timeout, "CloseNotebook did not return within 30 s; the connection to OneNote was reset.");

        var error = Error(await Tool(gateway, writeLock).CloseNotebook(LocalId, Ct), "Timeout");
        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());

        var write = new RecordingWrite();
        var running = new WritePipeline(writeLock, gateway, null, store.Read, TimeProvider.System).RunAsync(new ToolCallContext("w"), write, Ct);
        await Task.Delay(300, Ct);
        Assert.False(running.IsCompleted);
        Assert.False(write.Read); // waiting at step 2

        abandoned.SetResult();
        Assert.True(await running.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.True(write.Read);
    }

    [Fact]
    public async Task ARefusedClose_KeepsItsCode_WithTheCloseUserAction_NotOutcomeUnknown()
    {
        var gateway = Gateway();
        gateway.CloseFailure = new OneNoteException(ErrorCode.ComBusy, "CloseNotebook failed: hrNotYetSynchronized (0x8004201D).", unchecked((int)0x8004201D));
        var writeLock = AppStatePipelineTests.TestLock();

        var error = Error(await Tool(gateway, writeLock).CloseNotebook(LocalId, Ct), "ComBusy");

        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Equal("0x8004201D", error.GetProperty("hresult").GetString());
        Assert.Equal(CloseNotebookUseCase.CloseUserAction, error.GetProperty("userAction").GetString());
        Assert.Contains("still open", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.True(await AppStatePipelineTests.CanTakeAsync(writeLock));
    }

    public static readonly TheoryData<ErrorCode, bool> RefusedCloseWording = new()
    {
        { ErrorCode.ComBusy, true },
        { ErrorCode.ModalDialogOpen, true },
        { ErrorCode.ObjectNotFound, false },
    };

    [Theory]
    [MemberData(nameof(RefusedCloseWording))]
    public async Task ARefusedClose_SaysStillOpen_OnlyWhenOneNoteRefusedWhileHoldingIt(ErrorCode code, bool stillOpen)
    {
        // Retrospective F8: only ComBusy and ModalDialogOpen mean OneNote refused while holding the notebook; any other
        // code (a notebook closed by hand during the sync or settle, say) can't promise that it is still open.
        var gateway = Gateway();
        gateway.CloseFailure = new OneNoteException(code, "CloseNotebook failed.");

        var error = Error(await Tool(gateway).CloseNotebook(LocalId, Ct), code.ToString());

        var message = error.GetProperty("message").GetString()!;
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Equal(CloseNotebookUseCase.CloseUserAction, error.GetProperty("userAction").GetString());
        if (stillOpen)
        {
            Assert.EndsWith("The notebook is still open.", message, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("The notebook is still open.", message, StringComparison.Ordinal);
            Assert.EndsWith("Re-read the open notebooks to see whether it is still open.", message, StringComparison.Ordinal);
        }

        Assert.Single(gateway.CloseCalls);
    }

    [Fact]
    public async Task MalformedNotebooksXml_IsInvalidXml_NothingSyncedOrClosed()
    {
        // Retrospective F7: AD-9 maps XML the server can't read to InvalidXml, not Internal.
        var gateway = new FakeGateway();
        gateway.Hierarchies[(string.Empty, HierarchyScope.Notebooks)] = "<one:Notebooks";

        var error = Error(await Tool(gateway).CloseNotebook(LocalId, Ct), "InvalidXml");

        Assert.StartsWith("GetHierarchy returned XML the server can't read", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.AppStateLog);
        Assert.Empty(gateway.CloseCalls);
    }

    [Fact]
    public async Task MalformedSelfXml_ForAnUnknownId_IsInvalidXml_NothingSyncedOrClosed()
    {
        var gateway = Gateway();
        const string id = "{4B0B0000-0000-4000-8000-0000000044EE}{1}{B0}";
        gateway.Hierarchies[(id, HierarchyScope.Self)] = "<one:Section xmlns:one=\"" + Ns + "\" name=\"S\"";

        var error = Error(await Tool(gateway).CloseNotebook(id, Ct), "InvalidXml");

        Assert.StartsWith("GetHierarchy returned XML the server can't read", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.AppStateLog);
        Assert.Empty(gateway.CloseCalls);
    }

    [Fact]
    public void UseCase_IsAppState()
    {
        var gateway = new FakeGateway();
        Assert.Equal(OperationClass.AppState, new CloseNotebookUseCase(gateway, new AppStatePipeline(gateway)).Class);
    }

    [Fact]
    public void Description_IsUserVisible_PromisesNoFocus_AndAnnotationsAreNotReadOnlyNotDestructiveNotIdempotent()
    {
        using var services = BuildServer();
        var close = Assert.Single(services.GetServices<McpServerTool>().Select(t => t.ProtocolTool), t => t.Name == CloseNotebookTool.Name);

        Assert.StartsWith("User-visible", close.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("focus", close.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("120 s deadline", close.Description, StringComparison.Ordinal);
        Assert.Contains("outcomeUnknown", close.Description, StringComparison.Ordinal);
        Assert.Contains("notebookClosed", close.Description, StringComparison.Ordinal);
        Assert.False(close.Annotations!.ReadOnlyHint);
        Assert.False(close.Annotations.DestructiveHint);
        Assert.False(close.Annotations.IdempotentHint);
        Assert.False(close.Annotations.OpenWorldHint);
        Assert.Equal(["notebookId"], close.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void TheServersPipeline_GivesCloseNotebookTheWriteLock()
    {
        // ServerSetup wires the lock into the AppState pipeline: a server-built close on a fake gateway runs through.
        using var services = BuildServer();
        Assert.NotNull(services.GetRequiredService<CloseNotebookUseCase>());
        Assert.NotNull(services.GetRequiredService<AppStatePipeline>());
    }

    [Fact]
    public void NotebookElement_IsTheMatchingNotebook_AttributesOnly_WithTheNamespace_OrNull()
    {
        var withChildren = NotebooksXml.Replace(
            "color=\"#ADE792\" />",
            "color=\"#ADE792\"><one:Section name=\"S\" ID=\"" + SectionId + "\" /></one:Notebook>",
            StringComparison.Ordinal);

        var xml = HierarchyNode.NotebookElement(withChildren, LocalId)!;

        Assert.StartsWith("<one:Notebook xmlns:one=\"" + Ns + "\"", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("Section", xml, StringComparison.Ordinal);
        Assert.Contains("lastModifiedTime=\"2026-11-22T10:00:00.000Z\"", xml, StringComparison.Ordinal);
        Assert.Null(HierarchyNode.NotebookElement(NotebooksXml, SectionId));
        Assert.Null(HierarchyNode.NotebookElement(NotebooksXml, LocalId.ToLowerInvariant()));
    }

    private static FakeGateway Gateway()
    {
        var gateway = new FakeGateway();
        gateway.Hierarchies[(string.Empty, HierarchyScope.Notebooks)] = NotebooksXml;
        return gateway;
    }

    private static CloseNotebookTool Tool(
        FakeGateway gateway,
        WriteLock? writeLock = null,
        TimeSpan? deadline = null,
        Func<TimeSpan, CancellationToken, Task>? settle = null)
    {
        var pipeline = new AppStatePipeline(gateway, deadline: deadline, writeLock: writeLock ?? AppStatePipelineTests.TestLock());
        return new CloseNotebookTool(
            new ToolRunner(NullLogger<ToolRunner>.Instance),
            new CloseNotebookUseCase(gateway, pipeline, settle ?? ((time, _) =>
            {
                gateway.AppStateLog.Add("settle " + time.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return Task.CompletedTask;
            }), oneDrive: null, links: new NoLinks()));
    }

    /// <summary>No links and no network drives, so a close's reopen route never depends on the machine running the tests.</summary>
    private sealed class NoLinks : FilePathGate.ILinkReader
    {
        public string? ImmediateTarget(string path) => null;

        public bool IsNetworkDrive(string root) => false;
    }

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

    private static JsonElement Error(CallToolResult result, string code)
    {
        Assert.True(result.IsError, result.StructuredContent?.GetRawText());
        TextBlockAssert.Matches(result);
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        return error;
    }

    /// <summary>A write that records reaching step 3 and changes nothing.</summary>
    private sealed class RecordingWrite : WriteOperation<bool>
    {
        public bool Read { get; private set; }

        public override OperationClass Class => OperationClass.PageWrite;

        public override Task AuthorizeAsync(CancellationToken ct) => Task.CompletedTask;

        public override Task ReadAsync(CancellationToken ct)
        {
            Read = true;
            return Task.CompletedTask;
        }

        public override void Validate()
        {
        }

        public override Task<bool> CallAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
