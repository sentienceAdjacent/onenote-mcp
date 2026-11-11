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

    private SettingsStore Store => new(temp.Combine("settings.json"));

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
    [InlineData(ErrorCode.Internal, 0x80042024u)]
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
        gateway.AbandonedWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The result comes back while the abandoned call is still outstanding.
        var error = Error(await Tool().CreatePage(SectionId, cancellationToken: Ct), "Timeout");
        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        await AssertLockHeldAsync();

        gateway.AbandonedWrites.SetResult();
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AfterAStep7Cancel_WithAnAbandonedWriteOutstanding_TheLockIsKeptUntilItSettles()
    {
        Grant();
        using var cts = new CancellationTokenSource();
        gateway.AbandonedWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.OnCreate = async ct =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        };

        Error(await Tool().CreatePage(SectionId, cancellationToken: cts.Token), "Cancelled");
        await AssertLockHeldAsync();

        gateway.AbandonedWrites.SetResult();
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AfterAStep7Timeout_AnAbandonedWriteThatNeverSettles_KeepsTheLockOnlyUntilTheDeadline()
    {
        Grant();
        gateway.CreateFailure = new OneNoteException(ErrorCode.Timeout, "CreateNewPage did not return within 30 s; the connection to OneNote was reset.");
        gateway.AbandonedWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Error(await Tool(deadline: TimeSpan.FromMilliseconds(1500)).CreatePage(SectionId, cancellationToken: Ct), "Timeout");
        await AssertLockHeldAsync();

        // Never settles: the 1.5 s deadline releases it.
        using var acquired = await writeLock.AcquireAsync("after the deadline", TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task AFailureBeforeStep7_ReleasesTheLockAtOnce_EvenWithAnAbandonedWriteOutstanding()
    {
        // Only a step-7 failure can leave a write running: a refusal before it never waits.
        Grant();
        gateway.AbandonedWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Error(await Tool().CreatePage(LockedId, cancellationToken: Ct), "SectionLocked");

        await AssertLockFreeAsync();
        AssertNoWrite();
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

    private CreatePageUseCase UseCase(IOneNoteGateway? over = null, TimeSpan? deadline = null)
    {
        var g = over ?? gateway;
        return new CreatePageUseCase(g, new WriteAccess(g, Store.Read), new WritePipeline(writeLock, g, cache, Store.Read, TimeProvider.System, deadline));
    }

    private CreatePageTool Tool(IOneNoteGateway? over = null, TimeSpan? deadline = null) =>
        new(new ToolRunner(NullLogger<ToolRunner>.Instance), UseCase(over, deadline));

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

        public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct) => inner.UpdatePageContentAsync(pageXml, expectedLastModified, ct);

        public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct) => inner.DeletePageContentAsync(pageId, objectId, expectedLastModified, ct);

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => inner.WaitForAbandonedWritesAsync(ct);
    }
}
