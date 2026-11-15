using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
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
/// Story 2.4: <c>create_page</c> through the AD-7 write pipeline, one test per row of the plan's I/O matrix, against
/// <see cref="FakeGateway"/> scripted with a synthetic notebook <c>W</c> and a temporary <c>settings.json</c>. Each test
/// has a write lock of its own (a unique mutex name), and checks it is free again after every outcome.
/// </summary>
public sealed class CreatePageToolTests : IDisposable
{
    private const string NotebookId = "{NBW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string NotebookPath = @"C:\notebooks\W\";
    private const string SectionId = "{SW000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string ProtectedId = "{SP000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string LockedId = "{SL000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string GroupId = "{GW000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string RecycleBinId = "{RBW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string DeletedPagesId = "{SDW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string BinnedId = "{SBW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string PageId = "{SW000000-0000-4000-8000-000000000001}{1}{E1}";
    private const string UnknownId = "{00000000-0000-4000-8000-00000000DEAD}{1}{B0}";

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly FakeGateway gateway = new();
    private readonly ConcurrentQueue<WriteLockHeld> holds = new();
    private readonly WriteLock writeLock;
    private readonly PageTextCache cache = PageTextCache.WithDefaults();

    public CreatePageToolTests()
    {
        writeLock = new WriteLock($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", holds.Enqueue);

        // Notebook W: Open (granted target), a protected unlocked section, a locked one, a group, the recycle bin.
        Parents[NotebookId] = string.Empty;
        Parents[SectionId] = NotebookId;
        Parents[ProtectedId] = NotebookId;
        Parents[LockedId] = NotebookId;
        Parents[GroupId] = NotebookId;
        Parents[RecycleBinId] = NotebookId;
        Parents[DeletedPagesId] = RecycleBinId;
        Parents[BinnedId] = RecycleBinId;
        Parents[PageId] = SectionId;
        Self(NotebookId, $"<one:Notebook {Ns} name=\"W\" nickname=\"W\" ID=\"{NotebookId}\" path=\"{NotebookPath}\" />");
        Self(SectionId, Section(SectionId, "Open", string.Empty));
        Self(ProtectedId, Section(ProtectedId, "Vault", "encrypted=\"true\""));
        Self(LockedId, Section(LockedId, "Locked vault", "encrypted=\"true\" locked=\"true\""));
        Self(GroupId, $"<one:SectionGroup {Ns} name=\"G\" ID=\"{GroupId}\" path=\"C:\\notebooks\\W\\G\" />");
        Self(RecycleBinId, $"<one:SectionGroup {Ns} name=\"OneNote_RecycleBin\" ID=\"{RecycleBinId}\" path=\"C:\\notebooks\\W\\OneNote_RecycleBin\" isRecycleBin=\"true\" />");
        Self(DeletedPagesId, Section(DeletedPagesId, "Deleted Pages", "isInRecycleBin=\"true\" isDeletedPages=\"true\""));
        Self(BinnedId, Section(BinnedId, "Deleted section", "isInRecycleBin=\"true\""));
        Self(PageId, $"<one:Page {Ns} ID=\"{PageId}\" name=\"P\" />");
    }

    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Dictionary<string, string> Parents => gateway.Parents;

    private SettingsStore Store => new(temp.Combine("settings.json"), temp.Combine("journal"));

    public void Dispose() => temp.Dispose();

    // --- Granted ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Granted_IsOk_WithTheNewPageIdInMeta_AndOneCreateCall()
    {
        Grant();

        var root = Ok(await Tool().CreatePage(SectionId, cancellationToken: Ct));

        Assert.Equal(FakeGateway.CreatedPageId(1), root.GetProperty("meta").GetProperty("pageId").GetString());
        Assert.Equal(["correlationId", "pageId"], root.GetProperty("meta").EnumerateObject().Select(p => p.Name));
        Assert.False(root.TryGetProperty("xml", out _));
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        Assert.Equal([(SectionId, NewPageStyle.BlankPageWithTitle)], gateway.CreateCalls);
        Assert.Equal(root.GetProperty("meta").GetProperty("correlationId").GetString(), Assert.Single(holds).CorrelationId);
        await AssertLockFreeAsync();
    }

    [Theory]
    [InlineData(PageStyleArgument.Default, NewPageStyle.Default)]
    [InlineData(PageStyleArgument.BlankPageWithTitle, NewPageStyle.BlankPageWithTitle)]
    [InlineData(PageStyleArgument.BlankPageNoTitle, NewPageStyle.BlankPageNoTitle)]
    public async Task Style_ReachesCreateNewPage(PageStyleArgument style, NewPageStyle expected)
    {
        Grant();

        Ok(await Tool().CreatePage(SectionId, style, Ct));

        Assert.Equal([(SectionId, expected)], gateway.CreateCalls);
    }

    [Fact]
    public async Task TheGrantIsKeyedByTheNotebooksLivePath_TrailingSeparatorTrimmed_CaseIgnored()
    {
        Grant(key: @"c:\NOTEBOOKS\w");

        Ok(await Tool().CreatePage(SectionId, cancellationToken: Ct));
    }

    [Fact]
    public async Task ProtectedSection_UnlockedWithNoReadGrant_IsOk_AndNeverAsksForARead()
    {
        Grant();
        var prompt = new FakePrompt();
        var context = new ToolCallContext("c", Prompt: prompt);

        var success = await UseCase().ExecuteAsync(context, ProtectedId, NewPageStyle.BlankPageWithTitle, Ct);

        Assert.Equal(FakeGateway.CreatedPageId(1), success.Meta.PageId);
        Assert.Empty(prompt.Questions);
        Assert.Equal([(ProtectedId, NewPageStyle.BlankPageWithTitle)], gateway.CreateCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task TheWrittenPage_IsDroppedFromTheSearchCache()
    {
        Grant();
        var created = FakeGateway.CreatedPageId(1);
        cache.Set(created, DateTimeOffset.UnixEpoch, "stale text", long.MaxValue);
        cache.Set(PageId, DateTimeOffset.UnixEpoch, "other page", long.MaxValue);

        Ok(await Tool().CreatePage(SectionId, cancellationToken: Ct));

        Assert.False(cache.TryGet(created, DateTimeOffset.UnixEpoch, out _));
        Assert.True(cache.TryGet(PageId, DateTimeOffset.UnixEpoch, out _));
    }

    // --- Refused before COM ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ungranted_NoEntry_IsWriteNotGranted_Retryable_WithTheCliUserAction_AndNoWrite()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");

        var error = Error(await Tool().CreatePage(SectionId, cancellationToken: Ct), "WriteNotGranted");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        var userAction = error.GetProperty("userAction").GetString()!;
        Assert.Contains("grant --write \"<notebook name>\"", userAction, StringComparison.Ordinal);
        Assert.Contains("Never run the grant command yourself", userAction, StringComparison.Ordinal);
        Assert.Contains(NotebookId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"W\"", error.GetRawText(), StringComparison.Ordinal);
        AssertNoWrite();
        Assert.Empty(holds); // refused at step 1, before the lock
    }

    [Fact]
    public async Task Ungranted_WriteFalse_IsWriteNotGranted()
    {
        Grant(write: false);

        Error(await Tool().CreatePage(SectionId, cancellationToken: Ct), "WriteNotGranted");
        AssertNoWrite();
    }

    [Fact]
    public async Task AGrantForAnotherNotebook_DoesNotCount()
    {
        Grant(key: @"C:\notebooks\Other");

        Error(await Tool().CreatePage(SectionId, cancellationToken: Ct), "WriteNotGranted");
        AssertNoWrite();
    }

    [Fact]
    public async Task NoSettingsFile_IsWriteNotGranted()
    {
        Error(await Tool().CreatePage(SectionId, cancellationToken: Ct), "WriteNotGranted");
        AssertNoWrite();
    }

    [Fact]
    public async Task SettingsInvalid_IsSettingsInvalid_AndNoWrite()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1, "journal": {"retentionDays": 0}}""");

        var error = Error(await Tool().CreatePage(SectionId, cancellationToken: Ct), "SettingsInvalid");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Contains("journal.retentionDays", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        AssertNoWrite();
        Assert.Empty(gateway.Calls); // settings are checked before the notebook is resolved
    }

    [Fact]
    public async Task SettingsFromANewerServer_IsSettingsInvalid_NotRetryable_WithUpdateAdvice()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 2}""");

        var error = Error(await Tool().CreatePage(SectionId, cancellationToken: Ct), "SettingsInvalid");

        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(SettingsSnapshot.NewerSchemaUserAction, error.GetProperty("userAction").GetString());
        Assert.Contains("update OneNoteMcp", SettingsSnapshot.NewerSchemaUserAction, StringComparison.Ordinal);
        Assert.Contains("Don't edit settings.json", SettingsSnapshot.NewerSchemaUserAction, StringComparison.Ordinal);
        AssertNoWrite();
    }

    [Fact]
    public async Task LockedSection_IsSectionLocked_NoWrite_LockReleased()
    {
        Grant();

        var error = Error(await Tool().CreatePage(LockedId, cancellationToken: Ct), "SectionLocked");

        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        AssertNoWrite();
        Assert.Single(holds); // refused at step 4, under the lock
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task UnknownId_IsObjectNotFound_NoWrite()
    {
        Grant();

        var error = Error(await Tool().CreatePage(UnknownId, cancellationToken: Ct), "ObjectNotFound");

        Assert.Equal("0x80042014", error.GetProperty("hresult").GetString());
        AssertNoWrite();
    }

    [Theory]
    [InlineData(PageId)]
    [InlineData(GroupId)]
    [InlineData(DeletedPagesId)]
    [InlineData(BinnedId)]
    [InlineData(NotebookId)]
    public async Task NotALiveSection_IsObjectNotFound_AskingForASectionId_NoWrite(string id)
    {
        Grant();

        var error = Error(await Tool().CreatePage(id, cancellationToken: Ct), "ObjectNotFound");

        Assert.Equal(CreatePageUseCase.NotASectionUserAction, error.GetProperty("userAction").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        AssertNoWrite();
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task LockHeldElsewhere_PastTheWait_IsComBusy_Retryable_NoWrite()
    {
        Grant(callSeconds: 1);
        using (await writeLock.AcquireAsync("elsewhere", TimeSpan.FromSeconds(1), Ct))
        {
            var error = Error(await Tool().CreatePage(SectionId, cancellationToken: Ct), "ComBusy");

            Assert.True(error.GetProperty("retryable").GetBoolean());
            Assert.False(error.TryGetProperty("outcomeUnknown", out _));
            AssertNoWrite();
        }

        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task DeadlineExpiry_BeforeTheCall_IsTimeout_WithoutOutcomeUnknown()
    {
        // The lock wait (30 s by default) is capped by what's left of a 300 ms deadline.
        Grant();
        using (await writeLock.AcquireAsync("elsewhere", TimeSpan.FromSeconds(1), Ct))
        {
            var error = Error(await Tool(deadline: TimeSpan.FromMilliseconds(300)).CreatePage(SectionId, cancellationToken: Ct), "Timeout");

            Assert.False(error.TryGetProperty("outcomeUnknown", out _));
            Assert.True(error.GetProperty("retryable").GetBoolean());
            Assert.Contains("nothing was written", error.GetProperty("message").GetString(), StringComparison.Ordinal);
            AssertNoWrite();
        }
    }

    [Fact]
    public async Task AFailureBeforeTheCall_NeverSetsOutcomeUnknown_EvenATimeout()
    {
        Grant();
        var failing = new FailingSelfReadGateway(gateway, SectionId, new OneNoteException(ErrorCode.Timeout, "GetHierarchy timed out"));

        var error = Error(await Tool(failing).CreatePage(SectionId, cancellationToken: Ct), "Timeout");

        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.NotEqual(ToolErrors.OutcomeUnknownUserAction, error.GetProperty("userAction").GetString());
        AssertNoWrite();
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ClientCancel_BeforeTheCall_IsCancelled_WithoutOutcomeUnknown()
    {
        Grant();
        using var cts = new CancellationTokenSource();
        var failing = new FailingSelfReadGateway(gateway, SectionId, null, onRead: cts.Cancel);

        var error = Error(await Tool(failing).CreatePage(SectionId, cancellationToken: cts.Token), "Cancelled");

        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        AssertNoWrite();
        await AssertLockFreeAsync();
    }

    // --- The COM call fails -------------------------------------------------------------------------------------------

    public static TheoryData<ErrorCode, uint?> Ambiguous => new()
    {
        { ErrorCode.Timeout, null },
        { ErrorCode.ComUnavailable, 0x800706BAu },
        { ErrorCode.Cancelled, null },
        { ErrorCode.Internal, 0x800706BEu },
    };

    [Theory]
    [MemberData(nameof(Ambiguous))]
    public async Task AnAmbiguousFailureAtTheCall_IsOutcomeUnknown_NotRetryable_WithTheReReadUserAction(ErrorCode code, uint? hresult)
    {
        Grant();
        gateway.CreateFailure = new OneNoteException(code, "CreateNewPage failed.", hresult is { } h ? unchecked((int)h) : null);

        var error = Error(await Tool().CreatePage(SectionId, cancellationToken: Ct), code.ToString());

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(ToolErrors.OutcomeUnknownUserAction, error.GetProperty("userAction").GetString());
        if (hresult is { } expected)
        {
            Assert.Equal(ToolErrors.FormatHResult(unchecked((int)expected)), error.GetProperty("hresult").GetString());
        }

        Assert.Single(gateway.CreateCalls);
        await AssertLockFreeAsync();
    }

    [Theory]
    [InlineData(ErrorCode.SectionLocked, 0x8004201Bu)]
    [InlineData(ErrorCode.ModalDialogOpen, 0x80042030u)]
    [InlineData(ErrorCode.ObjectNotFound, 0x80042005u)]
    [InlineData(ErrorCode.ComBusy, 0x80042024u)]
    public async Task AClearFailureAtTheCall_KeepsItsCode_WithoutOutcomeUnknown(ErrorCode code, uint hresult)
    {
        Grant();
        gateway.CreateFailure = new OneNoteException(code, "CreateNewPage failed.", unchecked((int)hresult));

        var error = Error(await Tool().CreatePage(SectionId, cancellationToken: Ct), code.ToString());

        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ClientCancel_DuringTheCall_IsCancelled_WithOutcomeUnknown()
    {
        Grant();
        using var cts = new CancellationTokenSource();
        gateway.OnCreate = async ct =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        };

        var error = Error(await Tool().CreatePage(SectionId, cancellationToken: cts.Token), "Cancelled");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task DeadlineExpiry_DuringTheCall_IsTimeout_WithOutcomeUnknown()
    {
        Grant();
        gateway.OnCreate = ct => Task.Delay(Timeout.Infinite, ct);

        var error = Error(await Tool(deadline: TimeSpan.FromMilliseconds(300)).CreatePage(SectionId, cancellationToken: Ct), "Timeout");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(ToolErrors.OutcomeUnknownUserAction, error.GetProperty("userAction").GetString());
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task TheLock_IsHeldDuringTheCall()
    {
        Grant();
        var heldDuringCall = true;
        gateway.OnCreate = async ct =>
        {
            try
            {
                using var probe = await writeLock.AcquireAsync("probe", TimeSpan.FromMilliseconds(100), ct);
                heldDuringCall = false;
            }
            catch (OneNoteException ex) when (ex.Code == ErrorCode.ComBusy)
            {
            }
        };

        Ok(await Tool().CreatePage(SectionId, cancellationToken: Ct));

        Assert.True(heldDuringCall);
    }

    [Fact]
    public async Task AfterAStep7Timeout_WithAnAbandonedWriteOutstanding_TheLockIsKeptUntilItSettles_ButTheResultIsNotHeldBack()
    {
        Grant();
        gateway.CreateFailure = new OneNoteException(ErrorCode.Timeout, "CreateNewPage did not return within 30 s; the connection to OneNote was reset.");
        var abandoned = AbandonOnCreate();

        // The result comes back while the abandoned call is still outstanding.
        var error = Error(await Tool().CreatePage(SectionId, cancellationToken: Ct), "Timeout");
        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        await AssertLockHeldAsync();

        abandoned.SetResult();
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AfterAStep7Cancel_WithAnAbandonedWriteOutstanding_TheLockIsKeptUntilItSettles()
    {
        Grant();
        using var cts = new CancellationTokenSource();
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.OnCreate = async ct =>
        {
            // The call is abandoned while it runs, after step 2's check (AD-3).
            gateway.AbandonedWrites = abandoned;
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        };

        Error(await Tool().CreatePage(SectionId, cancellationToken: cts.Token), "Cancelled");
        await AssertLockHeldAsync();

        abandoned.SetResult();
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AfterAStep7Timeout_AnAbandonedWriteThatNeverSettles_KeepsTheLockOnlyUntilTheDeadline()
    {
        Grant();
        gateway.CreateFailure = new OneNoteException(ErrorCode.Timeout, "CreateNewPage did not return within 30 s; the connection to OneNote was reset.");
        AbandonOnCreate();

        Error(await Tool(deadline: TimeSpan.FromMilliseconds(1500)).CreatePage(SectionId, cancellationToken: Ct), "Timeout");
        await AssertLockHeldAsync();

        // Never settles: the 1.5 s deadline releases it.
        using var acquired = await writeLock.AcquireAsync("after the deadline", TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task AnAbandonedWriteOutstanding_AfterStep2_IsComBusy_NothingIsRead_AndTheLockIsReleasedAtOnce()
    {
        // AD-3 (story 2.9): while an abandoned write is outstanding, a new write is ComBusy right after it takes the lock,
        // before step 3 reads anything. Only a step-7 failure can leave a write running, so this refusal never waits.
        Grant();
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.AbandonedWrites = abandoned;
        gateway.Calls.Clear();

        var error = Error(await Tool().CreatePage(LockedId, cancellationToken: Ct), "ComBusy");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.DoesNotContain((LockedId, HierarchyScope.Self), gateway.Calls);
        await AssertLockFreeAsync();
        AssertNoWrite();

        // Once it settles, writes run again.
        abandoned.SetResult();
        Error(await Tool().CreatePage(LockedId, cancellationToken: Ct), "SectionLocked");
    }

    [Fact]
    public async Task AfterAStep7Timeout_TheWrittenPages_AreDroppedFromTheCacheAgain_WhenTheAbandonedWriteSettles()
    {
        // create_page names no page when its call fails, so a page write that names one drives the pipeline directly.
        Grant();
        const string OtherId = "{SW000000-0000-4000-8000-000000000001}{1}{E2}";
        cache.Set(PageId, DateTimeOffset.UnixEpoch, "before the write", long.MaxValue);
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new WritePipeline(writeLock, gateway, cache, Store.Read, TimeProvider.System);

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => pipeline.RunAsync(new ToolCallContext("c"), new TimedOutPageWrite(PageId, () => gateway.AbandonedWrites = abandoned), Ct));
        Assert.Equal(ErrorCode.Timeout, ex.Code);
        Assert.True(ex.OutcomeUnknown);

        // The first removal, before the result.
        Assert.False(cache.TryGet(PageId, DateTimeOffset.UnixEpoch, out _));

        // A search caches the page again while the abandoned call may still be changing it.
        cache.Set(PageId, DateTimeOffset.UnixEpoch, "read mid-write", long.MaxValue);
        cache.Set(OtherId, DateTimeOffset.UnixEpoch, "another page", long.MaxValue);
        await AssertLockHeldAsync();
        Assert.True(cache.TryGet(PageId, DateTimeOffset.UnixEpoch, out _));

        // It settles: the page is dropped again before the lock goes; other pages stay.
        abandoned.SetResult();
        await AssertLockFreeAsync();
        Assert.False(cache.TryGet(PageId, DateTimeOffset.UnixEpoch, out _));
        Assert.True(cache.TryGet(OtherId, DateTimeOffset.UnixEpoch, out _));
    }

    // --- The step after 7: the read-back hook (story 2.9, AD-7) ------------------------------------------------------

    [Fact]
    public async Task AfterCall_RunsUnderTheLock_AfterASuccessfulCall_AndItsResultIsReturned()
    {
        var operation = new ReadBackWrite(async (result, ct) =>
        {
            await AssertLockHeldAsync();
            return result + " checked";
        });

        var result = await Pipeline().RunAsync(new ToolCallContext("c"), operation, Ct);

        Assert.Equal("written checked", result);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AfterCall_ThatThrows_NeverFailsTheWrite_ItsFailureHookDecidesTheResult()
    {
        var operation = new ReadBackWrite((_, _) => throw new OneNoteException(ErrorCode.ComBusy, "busy"));

        var result = await Pipeline().RunAsync(new ToolCallContext("c"), operation, Ct);

        Assert.Equal("written, read-back failed: OneNoteException", result);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AfterCall_PastTheDeadline_NeverFailsTheWrite()
    {
        var operation = new ReadBackWrite(async (result, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return result;
        });

        var result = await Pipeline(TimeSpan.FromMilliseconds(500)).RunAsync(new ToolCallContext("c"), operation, Ct);

        Assert.StartsWith("written, read-back failed: ", result, StringComparison.Ordinal);
        Assert.Contains("Canceled", result, StringComparison.Ordinal);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AfterCall_DoesNotRun_WhenTheCallFails()
    {
        var ran = false;
        var operation = new ReadBackWrite(
            (result, _) =>
            {
                ran = true;
                return Task.FromResult(result);
            },
            new OneNoteException(ErrorCode.StaleWrite, "stale"));

        await Assert.ThrowsAsync<OneNoteException>(() => Pipeline().RunAsync(new ToolCallContext("c"), operation, Ct));

        Assert.False(ran);
        await AssertLockFreeAsync();
    }

    private WritePipeline Pipeline(TimeSpan? deadline = null) => new(writeLock, gateway, cache, Store.Read, TimeProvider.System, deadline);

    /// <summary>A write whose step-7 call returns <c>"written"</c> (or throws <paramref name="callFailure"/>), with a scripted read-back.</summary>
    private sealed class ReadBackWrite(Func<string, CancellationToken, Task<string>> afterCall, Exception? callFailure = null) : WriteOperation<string>
    {
        public override OperationClass Class => OperationClass.PageWrite;

        public override Task AuthorizeAsync(CancellationToken ct) => Task.CompletedTask;

        public override Task ReadAsync(CancellationToken ct) => Task.CompletedTask;

        public override void Validate()
        {
        }

        public override Task<string> CallAsync(CancellationToken ct) =>
            callFailure is null ? Task.FromResult("written") : Task.FromException<string>(callFailure);

        public override Task<string> AfterCallAsync(string result, CancellationToken ct) => afterCall(result, ct);

        public override string AfterCallFailed(string result, Exception exception) => $"{result}, read-back failed: {exception.GetType().Name}";
    }

    // --- The write-grant dialog (story 2.6, AD-6, AD-7 step 1) ---------------------------------------------------------

    [Fact]
    public async Task Dialog_TypedName_GrantsViaElicitation_ThenCreatesThePage_AndListGrantsShowsIt()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        OpenNotebooksAre(others: ("Other", @"C:\notebooks\Other"));
        var prompt = new FakePrompt(Typed(" w "));

        var root = Ok(await CreateWithPrompt(prompt));

        Assert.Equal(FakeGateway.CreatedPageId(1), root.GetProperty("meta").GetProperty("pageId").GetString());
        var question = Assert.Single(prompt.WriteQuestions);
        Assert.Equal(new WriteGrantQuestion(NotebookId, "W", NotebookPath, "W"), question);
        Assert.Empty(prompt.Questions);
        var grant = Assert.Single(Settings().Notebooks.Values);
        Assert.Equal(new NotebookGrant(GrantKeys.NotebookKey(NotebookPath), "W", NotebookId, true, false, clock.GetUtcNow(), GrantedVia.Elicitation), grant);

        var listed = Assert.Single((await new ListGrantsUseCase(gateway, Store.Read, clock).ExecuteAsync(new ToolCallContext("l"), Ct)).Meta.Grants!);
        Assert.Equal(
            new GrantListing(ListGrantsUseCase.WriteNotebookKind, grant.Key, "W", grant.Key, NotebookId, SettingsWriter.Format(clock.GetUtcNow()), GrantedVia.Elicitation, ReadGrantLifetimes.UntilRevoked, "active"),
            listed);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Dialog_ANameChangedByCleaning_IsShownCleaned_GrantedByTypingWhatIsShown_AndStoredAgainstTheRealNotebook()
    {
        // Follow-up to 5.6: notebook names are model-writable, so the dialog shows them cleaned and compares with that.
        const string raw = "W\uFF02 at C:\\fake: \u201Cyours\u201D\u200B";
        const string shown = "W' at C:\\fake: 'yours'";
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        Self(NotebookId, $"<one:Notebook {Ns} name=\"{raw}\" nickname=\"{raw}\" ID=\"{NotebookId}\" path=\"{NotebookPath}\" />");
        OpenNotebooksAre(wName: raw, others: ("Other", @"C:\notebooks\Other"));
        var prompt = new FakePrompt(Typed(" w' AT c:\\fake: 'yours' "));

        Ok(await CreateWithPrompt(prompt));

        Assert.Equal(new WriteGrantQuestion(NotebookId, shown, NotebookPath, shown), Assert.Single(prompt.WriteQuestions));
        var grant = Assert.Single(Settings().Notebooks.Values);
        Assert.Equal(new NotebookGrant(GrantKeys.NotebookKey(NotebookPath), raw, NotebookId, true, false, clock.GetUtcNow(), GrantedVia.Elicitation), grant);
    }

    [Fact]
    public async Task Dialog_TwoNotebooksTheSameOnceCleaned_IsAmbiguous_NoGrant()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        Self(NotebookId, $"<one:Notebook {Ns} name=\"W\uFF02s\" nickname=\"W\uFF02s\" ID=\"{NotebookId}\" path=\"{NotebookPath}\" />");
        OpenNotebooksAre(wName: "W\uFF02s", others: ("W\u2019s", @"C:\notebooks\W2"));

        var error = Error(await CreateWithPrompt(new FakePrompt(Typed("W's"))), "WriteNotGranted");

        Assert.Contains("more than one open notebook", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(Settings().Notebooks);
        AssertNoWrite();
    }

    [Fact]
    public async Task Dialog_GrantedOnce_IsNotAskedAgain_ButAfterARevokeItIs()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        OpenNotebooksAre();
        var prompt = new FakePrompt(Typed("W"), Typed("W"));

        Ok(await CreateWithPrompt(prompt));
        Ok(await CreateWithPrompt(prompt));
        Assert.Single(prompt.WriteQuestions);

        Assert.Equal(1, new SettingsWriter(Store, clock).RevokeWriteGrant(GrantKeys.NotebookKey(NotebookPath)));
        Ok(await CreateWithPrompt(prompt));
        Assert.Equal(2, prompt.WriteQuestions.Count);
        Assert.Equal(3, gateway.CreateCalls.Count);
    }

    [Fact]
    public async Task Dialog_NeverRevivesAPermanentDeleteFlagHeldWithoutAWriteGrant()
    {
        // Story 5.6 plan Q2 b: a write grant, the dialog's included, never silently brings permanent deletes back.
        File.WriteAllText(
            Store.Path,
            """{"schemaVersion": 1, "notebooks": {"C:\\notebooks\\W": {"name": "W", "idAtGrant": "x", "write": false, "permanentDelete": true, "grantedAt": "2026-11-01T00:00:00.000Z", "grantedVia": "cli"}}}""");
        OpenNotebooksAre();

        Ok(await CreateWithPrompt(new FakePrompt(Typed("W"))));

        var grant = Assert.Single(Settings().Notebooks.Values);
        Assert.True(grant.Write);
        Assert.False(grant.PermanentDelete);
        Assert.Equal(GrantedVia.Elicitation, grant.GrantedVia);
    }

    public static TheoryData<GrantPromptResult, string> Refusals => new()
    {
        { new GrantPromptResult(GrantPromptStatus.Declined), "declined" },
        { new GrantPromptResult(GrantPromptStatus.Cancelled), "cancelled" },
        { GrantPromptResult.Unsupported, "can't show the grant dialog" },
        { Typed("Nope"), "matched no open notebook" },
        { Typed(string.Empty), "matched no open notebook" },
        { new GrantPromptResult(GrantPromptStatus.Accepted), "matched no open notebook" },
        { Typed("Other"), "didn't match this notebook" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Dialog_NotAnsweredWithThisNotebooksName_IsWriteNotGranted_Retryable_NoGrant_NoLock_NoWrite(GrantPromptResult answer, string why)
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        OpenNotebooksAre(others: ("Other", @"C:\notebooks\Other"));

        var error = Error(await CreateWithPrompt(new FakePrompt(answer)), "WriteNotGranted");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(WriteAccess.NotGrantedUserAction, error.GetProperty("userAction").GetString());
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains(why, message, StringComparison.Ordinal);
        Assert.Contains(NotebookId, message, StringComparison.Ordinal);
        Assert.DoesNotContain("\"W\"", error.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(@"notebooks\\W", error.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(Settings().Notebooks);
        Assert.Empty(holds);
        AssertNoWrite();
    }

    [Fact]
    public async Task Dialog_AmbiguousName_IsWriteNotGranted_NoGrant()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        OpenNotebooksAre(others: ("W", @"C:\notebooks\W2"));

        var error = Error(await CreateWithPrompt(new FakePrompt(Typed("W"))), "WriteNotGranted");

        Assert.Contains("more than one open notebook", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(Settings().Notebooks);
        AssertNoWrite();
    }

    [Fact]
    public async Task Dialog_SettingsInvalid_IsSettingsInvalid_WithNoDialog()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1, "journal": {"retentionDays": 0}}""");
        var prompt = new FakePrompt(Typed("W"));

        Error(await CreateWithPrompt(prompt), "SettingsInvalid");

        Assert.Empty(prompt.WriteQuestions);
        AssertNoWrite();
    }

    [Fact]
    public async Task Dialog_NotebookWithNoPath_IsWriteNotGranted_WithNoDialog()
    {
        Self(NotebookId, $"<one:Notebook {Ns} name=\"W\" nickname=\"W\" ID=\"{NotebookId}\" path=\"\" />");
        var prompt = new FakePrompt(Typed("W"));

        Error(await CreateWithPrompt(prompt), "WriteNotGranted");

        Assert.Empty(prompt.WriteQuestions);
        AssertNoWrite();
    }

    [Fact]
    public async Task Dialog_AlreadyGranted_IsNeverRaised()
    {
        Grant();
        var prompt = new FakePrompt(Typed("W"));

        Ok(await CreateWithPrompt(prompt));

        Assert.Empty(prompt.WriteQuestions);
    }

    [Fact]
    public async Task Dialog_ASlowAnswer_PastTheDeadline_StillWrites()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        OpenNotebooksAre();
        var prompt = new ScriptedPrompt(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700), ct);
            return Typed("W");
        });

        Ok(await CreateWithPrompt(prompt, deadline: TimeSpan.FromMilliseconds(300)));

        Assert.Single(gateway.CreateCalls);
    }

    [Fact]
    public async Task Dialog_TheWriteLock_IsFreeWhileTheUserDecides()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        OpenNotebooksAre();
        var prompt = new ScriptedPrompt(async (_, ct) =>
        {
            // Another writer (another process, as far as the named mutex knows) can take the lock meanwhile.
            Assert.Empty(holds);
            using (await writeLock.AcquireAsync("another process", TimeSpan.FromMilliseconds(500), ct))
            {
            }

            return Typed("W");
        });

        Ok(await CreateWithPrompt(prompt));

        Assert.Equal(1, prompt.Asked);
        Assert.Equal(2, holds.Count); // the other writer's hold, then this write's
    }

    [Fact]
    public async Task Dialog_ClientCancelWhileTheUserDecides_IsCancelled_NoOutcomeUnknown_NoGrant()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        OpenNotebooksAre();
        using var cts = new CancellationTokenSource();
        var prompt = new ScriptedPrompt(async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return Typed("W");
        });

        var error = Error(await CreateWithPrompt(prompt, ct: cts.Token), "Cancelled");

        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Empty(Settings().Notebooks);
        AssertNoWrite();
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Dialog_AFailedSave_KeepsItsOwnError_NoOnceFallback()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        OpenNotebooksAre();
        var prompt = new ScriptedPrompt((_, _) =>
        {
            // The file goes bad while the dialog is open: the save refuses it.
            File.WriteAllText(Store.Path, """{"schemaVersion": 1, "journal": {"retentionDays": 0}}""");
            return Task.FromResult(Typed("W"));
        });

        Error(await CreateWithPrompt(prompt), "SettingsInvalid");

        AssertNoWrite();
        Assert.Empty(holds);
    }

    [Fact]
    public async Task Dialog_StepOneRunsOnceMore_WithNoSecondDialog()
    {
        // The check reads a settings file the dialog's save doesn't reach, so the re-check still finds no grant.
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        var elsewhere = new SettingsStore(temp.Combine("elsewhere.json"), temp.Combine("journal-elsewhere"));
        File.WriteAllText(elsewhere.Path, """{"schemaVersion": 1}""");
        OpenNotebooksAre();
        var prompt = new FakePrompt(Typed("W"), Typed("W"));

        Error(await CreateWithPrompt(prompt, accessSettings: elsewhere.Read), "WriteNotGranted");

        Assert.Single(prompt.WriteQuestions);
        Assert.Single(Settings().Notebooks);
        AssertNoWrite();
    }

    [Fact]
    public async Task Dialog_TheNotebooksIdChanged_SamePath_StillGranted()
    {
        // Granted under one ID; the notebook was closed and reopened, so it has another (spike 5.1). The key is its path.
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        OpenNotebooksAre();
        Ok(await CreateWithPrompt(new FakePrompt(Typed("W"))));

        const string NewId = "{NBW00000-0000-4000-8000-000000000002}{1}{B0}";
        Parents[SectionId] = NewId;
        Parents[NewId] = string.Empty;
        Self(NewId, $"<one:Notebook {Ns} name=\"W\" nickname=\"W\" ID=\"{NewId}\" path=\"{NotebookPath}\" />");
        OpenNotebooksAre(wId: NewId);
        var prompt = new FakePrompt();

        Ok(await CreateWithPrompt(prompt));

        Assert.Empty(prompt.WriteQuestions);
        var listed = Assert.Single((await new ListGrantsUseCase(gateway, Store.Read, clock).ExecuteAsync(new ToolCallContext("l"), Ct)).Meta.Grants!);
        Assert.Equal(("active", NotebookId), (listed.Status, listed.IdAtGrant));
    }

    // --- The tool ---------------------------------------------------------------------------------------------------

    [Fact]
    public void UseCase_IsCreate()
    {
        Assert.Equal(OperationClass.Create, UseCase().Class);
    }

    // --- Helpers ----------------------------------------------------------------------------------------------------

    private void Self(string id, string xml) =>
        gateway.Hierarchies[(id, HierarchyScope.Self)] = "<?xml version=\"1.0\"?>\n" + xml;

    private static string Section(string id, string name, string extra) =>
        $"<one:Section {Ns} name=\"{name}\" ID=\"{id}\" path=\"C:\\notebooks\\W\\{name}.one\" {extra} />";

    private void Grant(bool write = true, string? key = null, int? callSeconds = null)
    {
        if (callSeconds is { } seconds)
        {
            File.WriteAllText(Store.Path, "{\"schemaVersion\": 1, \"timeouts\": {\"callSeconds\": " + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}");
        }

        new SettingsWriter(Store, clock).SaveWriteGrant(
            new NotebookGrant(key ?? GrantKeys.NotebookKey(NotebookPath), "W", NotebookId, write, PermanentDelete: false, clock.GetUtcNow(), GrantedVia.Cli));
    }

    private CreatePageUseCase UseCase(IOneNoteGateway? over = null, TimeSpan? deadline = null, Func<SettingsSnapshot>? accessSettings = null)
    {
        var g = over ?? gateway;
        return new CreatePageUseCase(
            g,
            new WriteAccess(g, accessSettings ?? Store.Read),
            new WritePipeline(writeLock, g, cache, Store.Read, TimeProvider.System, deadline, new WriteGrantDialog(g, new SettingsWriter(Store, clock), clock)));
    }

    private CreatePageTool Tool(IOneNoteGateway? over = null, TimeSpan? deadline = null) =>
        new(new ToolRunner(NullLogger<ToolRunner>.Instance), UseCase(over, deadline));

    /// <summary>A <c>create_page</c> call through the runner with <paramref name="prompt"/> as the call's grant dialog.</summary>
    private Task<CallToolResult> CreateWithPrompt(IGrantPrompt prompt, string? sectionId = null, TimeSpan? deadline = null, Func<SettingsSnapshot>? accessSettings = null, CancellationToken? ct = null)
    {
        var useCase = UseCase(deadline: deadline, accessSettings: accessSettings);
        return new ToolRunner(NullLogger<ToolRunner>.Instance).RunAsync(
            CreatePageTool.Name,
            useCase,
            (context, token) => useCase.ExecuteAsync(context, sectionId ?? SectionId, NewPageStyle.BlankPageWithTitle, token),
            ct ?? Ct,
            prompt);
    }

    /// <summary>The open notebooks as <c>GetHierarchy("", sections)</c> returns them: W, plus <paramref name="others"/> (name, path).</summary>
    private void OpenNotebooksAre(string wId = NotebookId, string wName = "W", params (string Name, string Path)[] others)
    {
        var xml = new System.Text.StringBuilder("<?xml version=\"1.0\"?>\n<one:Notebooks " + Ns + ">");
        xml.Append($"<one:Notebook name=\"{wName}\" nickname=\"{wName}\" ID=\"{wId}\" path=\"{NotebookPath}\">{Section(SectionId, "Open", string.Empty)}</one:Notebook>");
        var n = 0;
        foreach (var (name, path) in others)
        {
            xml.Append($"<one:Notebook name=\"{name}\" nickname=\"{name}\" ID=\"{{NBX0000{++n}-0000-4000-8000-000000000001}}{{1}}{{B0}}\" path=\"{path}\" />");
        }

        gateway.Hierarchies[(string.Empty, HierarchyScope.Sections)] = xml.Append("</one:Notebooks>").ToString();
    }

    private static GrantPromptResult Typed(string name) => new(GrantPromptStatus.Accepted, name);

    private SettingsSnapshot Settings() => Store.Read();

    /// <summary>A write-grant dialog driven by a delegate; the read dialog is never expected.</summary>
    private sealed class ScriptedPrompt(Func<WriteGrantQuestion, CancellationToken, Task<GrantPromptResult>> answer) : IGrantPrompt
    {
        public int Asked { get; private set; }

        public Task<GrantPromptResult> AskAsync(GrantQuestion question, CancellationToken ct) =>
            throw new InvalidOperationException("create_page never asks for a read grant.");

        public Task<GrantPromptResult> AskWriteAsync(WriteGrantQuestion question, CancellationToken ct)
        {
            Asked++;
            return answer(question, ct);
        }
    }

    /// <summary>The step-7 call leaves an abandoned write outstanding (AD-3) until the returned source completes.</summary>
    private TaskCompletionSource AbandonOnCreate()
    {
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.OnCreate = _ =>
        {
            gateway.AbandonedWrites = abandoned;
            return Task.CompletedTask;
        };
        return abandoned;
    }

    private void AssertNoWrite() => Assert.Empty(gateway.CreateCalls);

    private async Task AssertLockFreeAsync()
    {
        using var probe = await writeLock.AcquireAsync("free?", TimeSpan.FromMilliseconds(500), Ct);
    }

    private async Task AssertLockHeldAsync()
    {
        var busy = await Assert.ThrowsAsync<OneNoteException>(() => writeLock.AcquireAsync("held?", TimeSpan.FromMilliseconds(100), Ct));
        Assert.Equal(ErrorCode.ComBusy, busy.Code);
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
        Assert.True(result.IsError);
        TextBlockAssert.Matches(result);
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        return error;
    }

    /// <summary>A page write whose step-7 call times out after it may have written <paramref name="pageId"/>.</summary>
    private sealed class TimedOutPageWrite(string pageId, Action? onCall = null) : WriteOperation<string>
    {
        public override OperationClass Class => OperationClass.Create;

        public override IReadOnlyCollection<string> WrittenPageIds => [pageId];

        public override Task AuthorizeAsync(CancellationToken ct) => Task.CompletedTask;

        public override Task ReadAsync(CancellationToken ct) => Task.CompletedTask;

        public override void Validate()
        {
        }

        public override Task<string> CallAsync(CancellationToken ct)
        {
            onCall?.Invoke();
            return Task.FromException<string>(new OneNoteException(ErrorCode.Timeout, "UpdatePageContent did not return within 30 s; the connection to OneNote was reset."));
        }
    }

    /// <summary>Wraps a gateway: step 3's read of <c>sectionId</c> at <c>self</c> throws, or runs a hook first.</summary>
    private sealed class FailingSelfReadGateway(FakeGateway inner, string sectionId, OneNoteException? failure, Action? onRead = null) : IOneNoteGateway
    {
        private int selfReads;

        public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct)
        {
            if (startNodeId == sectionId && scope == HierarchyScope.Self && ++selfReads == 1)
            {
                onRead?.Invoke();
                ct.ThrowIfCancellationRequested();
                if (failure is not null)
                {
                    return Task.FromException<string>(failure);
                }
            }

            return inner.GetHierarchyAsync(startNodeId, scope, ct);
        }

        public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) => inner.GetHierarchyParentAsync(objectId, ct);

        public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct) => inner.GetPageContentAsync(pageId, pageInfo, ct);

        public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) => inner.GetBinaryPageContentAsync(pageId, callbackId, ct);

        public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) => inner.FindPagesAsync(startNodeId, query, includeUnindexed, ct);

        public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) => inner.FindMetaAsync(startNodeId, name, includeUnindexed, ct);

        public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => inner.GetHyperlinkToObjectAsync(hierarchyId, pageContentObjectId, ct);

        public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => inner.GetWebHyperlinkToObjectAsync(hierarchyId, pageContentObjectId, ct);

        public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => inner.GetSpecialLocationAsync(location, ct);

        public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct) => inner.NavigateToAsync(hierarchyId, objectId, ct);

        public Task<string> CreateNewPageAsync(string sectionId, NewPageStyle style, CancellationToken ct) => inner.CreateNewPageAsync(sectionId, style, ct);

        public Task<string> CreateSectionAsync(string parentId, string sectionFileName, CancellationToken ct) => inner.CreateSectionAsync(parentId, sectionFileName, ct);

        public Task<string> CreateSectionGroupAsync(string parentId, string groupName, CancellationToken ct) => inner.CreateSectionGroupAsync(parentId, groupName, ct);

        public Task<string> CreateNotebookAsync(string folderPath, CancellationToken ct) => inner.CreateNotebookAsync(folderPath, ct);

        public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct) => inner.UpdatePageContentAsync(pageXml, expectedLastModified, ct);

        public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct) => inner.DeletePageContentAsync(pageId, objectId, expectedLastModified, ct);


        public Task UpdateHierarchyAsync(string changesXml, CancellationToken ct) => inner.UpdateHierarchyAsync(changesXml, ct);

        public Task DeleteHierarchyAsync(string objectId, string expectedLastModified, bool permanent, CancellationToken ct) => inner.DeleteHierarchyAsync(objectId, expectedLastModified, permanent, ct);

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => inner.WaitForAbandonedWritesAsync(ct);
    }
}
