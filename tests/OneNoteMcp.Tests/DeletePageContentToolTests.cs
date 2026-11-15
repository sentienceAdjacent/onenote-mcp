using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;
using static OneNoteMcp.Tests.PageWriteCheckTests;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 2.10: <c>delete_page_content</c> through the AD-7 write pipeline, one test per row of the plan's I/O matrix,
/// against <see cref="FakeGateway"/> scripted as in <see cref="UpdatePageContentToolTests"/>: notebook <c>W</c> (an open
/// section, a password-protected one, a locked one, the recycle bin), the synthetic page
/// <c>synthetic/update-page-Basic.xml</c> (Outlines 20 and 40), a temporary <c>settings.json</c> and journal folder. Each
/// test has a write lock of its own and checks it is free again after every outcome; refusals in steps 3–5 leave no COM
/// delete and no journal entry.
/// </summary>
public sealed class DeletePageContentToolTests : IDisposable
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";
    private const string NotebookId = "{NBW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string NotebookPath = @"C:\notebooks\W\";
    private const string SectionId = "{SW000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string ProtectedId = "{SP000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string LockedId = "{SL000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string RecycleBinId = "{RBW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string DeletedPagesId = "{SDW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string VaultPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000002}";
    private const string LockedPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000003}";
    private const string BinnedPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000004}";

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly FakeGateway gateway = new();
    private readonly ConcurrentQueue<WriteLockHeld> holds = new();
    private readonly WriteLock writeLock;
    private readonly PageTextCache cache = PageTextCache.WithDefaults();
    private readonly ReadAccess readAccess;

    public DeletePageContentToolTests()
    {
        writeLock = new WriteLock($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", holds.Enqueue);
        readAccess = new ReadAccess(gateway, Store.Read, new SettingsWriter(Store, clock), clock);

        gateway.Parents[NotebookId] = string.Empty;
        gateway.Parents[SectionId] = NotebookId;
        gateway.Parents[ProtectedId] = NotebookId;
        gateway.Parents[LockedId] = NotebookId;
        gateway.Parents[RecycleBinId] = NotebookId;
        gateway.Parents[DeletedPagesId] = RecycleBinId;
        gateway.Parents[PageId] = SectionId;
        gateway.Parents[VaultPageId] = ProtectedId;
        gateway.Parents[LockedPageId] = LockedId;
        gateway.Parents[BinnedPageId] = DeletedPagesId;
        Self(NotebookId, $"<one:Notebook {Ns} name=\"W\" nickname=\"W\" ID=\"{NotebookId}\" path=\"{NotebookPath}\" />");
        Self(SectionId, Section(SectionId, "Open", string.Empty));
        Self(ProtectedId, Section(ProtectedId, "Vault", "encrypted=\"true\""));
        Self(LockedId, Section(LockedId, "Locked vault", "encrypted=\"true\" locked=\"true\""));
        Self(RecycleBinId, $"<one:SectionGroup {Ns} name=\"OneNote_RecycleBin\" ID=\"{RecycleBinId}\" path=\"C:\\notebooks\\W\\OneNote_RecycleBin\" isRecycleBin=\"true\" />");
        Self(DeletedPagesId, Section(DeletedPagesId, "Deleted Pages", "isInRecycleBin=\"true\" isDeletedPages=\"true\""));
        Self(PageId, $"<one:Page {Ns} ID=\"{PageId}\" name=\"Synthetic Update\" />");
        Self(VaultPageId, $"<one:Page {Ns} ID=\"{VaultPageId}\" name=\"Synthetic Update\" />");
        Self(LockedPageId, $"<one:Page {Ns} ID=\"{LockedPageId}\" name=\"Synthetic Update\" />");
        Self(BinnedPageId, $"<one:Page {Ns} ID=\"{BinnedPageId}\" name=\"Synthetic Update\" isInRecycleBin=\"true\" />");
        gateway.Pages[PageId] = Read;
        gateway.Pages[VaultPageId] = Read.Replace(PageId, VaultPageId, StringComparison.Ordinal);
        Grant();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Folder => temp.Combine("journal");

    private SettingsStore Store => new(temp.Combine("settings.json"), Folder);

    public void Dispose() => temp.Dispose();

    // --- The matrix ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FreshDelete_IsOk_WithOneJournalEntry_TheCallersDateGoesToCom_AndMetaBasisComesFromTheReadBack()
    {
        cache.Set(PageId, DateTimeOffset.UnixEpoch, "cached text", long.MaxValue);
        var written = Deletes(PageId, "2026-11-12T00:05:00.000Z");

        // The same instant as the read's date, written differently: compared as UTC instants, passed to COM unchanged.
        var root = Ok(await Tool().DeletePageContent(PageId, G(40), BasisArgument(date: "2026-11-12T00:00:00Z"), Ct));

        Assert.Equal((PageId, G(40), "2026-11-12T00:00:00Z"), Assert.Single(gateway.DeleteCalls));
        Assert.Empty(gateway.UpdateCalls);
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        var basis = root.GetProperty("meta").GetProperty("basis");
        Assert.Equal(PageBasis.From(written).PageHash, basis.GetProperty("pageHash").GetString());
        Assert.Equal("2026-11-12T00:05:00.000Z", basis.GetProperty("lastModifiedTime").GetString());
        Assert.False(basis.GetProperty("elementHashes").TryGetProperty(G(40), out _));
        var entry = JournalStore.Read(Assert.Single(Directory.GetFiles(Folder)));
        Assert.Equal("delete_page_content", entry.Operation);
        Assert.Equal(nameof(OperationClass.PageWrite), entry.OperationClass);
        Assert.Equal(root.GetProperty("meta").GetProperty("correlationId").GetString(), entry.CorrelationId);
        Assert.Equal(entry.EntryId.ToString("D"), root.GetProperty("meta").GetProperty("journalEntryId").GetString()); // 2.11 Q1
        Assert.False(cache.TryGet(PageId, DateTimeOffset.UnixEpoch, out _));
        Assert.Equal(root.GetProperty("meta").GetProperty("correlationId").GetString(), Assert.Single(holds).CorrelationId);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AFailedComDelete_NamesTheJournalEntry_InItsMessage()
    {
        // 2.11 Q1: a failure after the snapshot names the entry, so the caller can restore it; error meta stays {correlationId}.
        gateway.DeleteFailure = new OneNoteException(ErrorCode.ModalDialogOpen, "DeletePageContent failed: hrModalDialogOpen.", unchecked((int)0x80042030));

        var result = await Tool().DeletePageContent(PageId, G(40), BasisArgument(), Ct);

        var error = Error(result, "ModalDialogOpen");
        var entry = JournalStore.Read(Assert.Single(Directory.GetFiles(Folder)));
        Assert.Contains(entry.EntryId.ToString("D"), error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.False(result.StructuredContent!.Value.GetProperty("meta").TryGetProperty("journalEntryId", out _));
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task StaleHash_IsStaleWrite_WithNoSnapshot_AndNoComDelete()
    {
        var error = Error(await Tool().DeletePageContent(PageId, G(40), BasisArgument(hash: (G(40), new string('0', 64))), Ct), "StaleWrite");

        Assert.Contains(G(40), error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task OnlyTheDeletedOutlinesHash_IsCompared_PageHashAndOtherHashesAreIgnored()
    {
        Deletes(PageId);
        var basis = BasisArgument(hash: (G(20), new string('0', 64))) with { PageHash = new string('1', 64) };

        var root = Ok(await Tool().DeletePageContent(PageId, G(40), basis, Ct));

        Assert.Empty(Codes(root));
        Assert.Single(gateway.DeleteCalls);
    }

    [Fact]
    public async Task StaleDate_IsStaleWrite_WithNoComDelete()
    {
        Error(await Tool().DeletePageContent(PageId, G(40), BasisArgument(date: "2026-11-11T23:00:00.000Z"), Ct), "StaleWrite");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task ABasisDateThatIsntADate_IsInvalidArgument()
    {
        var error = Error(await Tool().DeletePageContent(PageId, G(40), BasisArgument(date: "yesterday"), Ct), "InvalidArgument");

        Assert.Equal(PageWrite.NoBasisUserAction, error.GetProperty("userAction").GetString());
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task NoBasis_RunsWithStep3sDate_AndWarnsNoBasis()
    {
        Deletes(PageId);

        var root = Ok(await Tool().DeletePageContent(PageId, G(40), cancellationToken: Ct));

        Assert.Equal(["noBasis"], Codes(root));
        Assert.Equal(PageBasis.From(Read).LastModifiedTime, Assert.Single(gateway.DeleteCalls).ExpectedLastModified);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task PartialBasis_TheOutlinesHashMissing_SkipsThatCheck_AndWarnsPartialBasis()
    {
        Deletes(PageId);
        var basis = BasisArgument();
        var hashes = new Dictionary<string, string>(basis.ElementHashes!, StringComparer.Ordinal);
        hashes.Remove(G(40));

        var root = Ok(await Tool().DeletePageContent(PageId, G(40), basis with { ElementHashes = hashes }, Ct));

        Assert.Equal(["partialBasis"], Codes(root));
        Assert.Contains(G(40), Warning(root, "partialBasis"), StringComparison.Ordinal);
        Assert.Single(gateway.DeleteCalls);
    }

    [Fact]
    public async Task PartialBasis_AMissingDate_TakesStep3sDate()
    {
        Deletes(PageId);

        var root = Ok(await Tool().DeletePageContent(PageId, G(40), BasisArgument() with { LastModifiedTime = null }, Ct));

        Assert.Equal(["partialBasis"], Codes(root));
        Assert.Equal(PageBasis.From(Read).LastModifiedTime, Assert.Single(gateway.DeleteCalls).ExpectedLastModified);
    }

    [Theory]
    [InlineData(41)] // an OE
    [InlineData(10)] // the title's OE
    [InlineData(24)] // a table
    public async Task NotAnOutline_IsInvalidArgument_WithItsOwnUserAction(int n)
    {
        var error = Error(await Tool().DeletePageContent(PageId, G(n), BasisArgument(), Ct), "InvalidArgument");

        Assert.Equal(DeletePageContentWrite.NotAnOutlineUserAction, error.GetProperty("userAction").GetString());
        Assert.Contains("removeObjectIds", error.GetProperty("userAction").GetString(), StringComparison.Ordinal);
        Assert.Contains(G(n), error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task AnImage_IsInvalidArgument()
    {
        gateway.Pages[PageId] = After(p => p.Descendants(One + "Image").Single().SetAttributeValue("objectID", G(90)));

        var error = Error(await Tool().DeletePageContent(PageId, G(90), cancellationToken: Ct), "InvalidArgument");

        Assert.Equal(DeletePageContentWrite.NotAnOutlineUserAction, error.GetProperty("userAction").GetString());
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task UnknownObjectId_IsUnknownObjectId()
    {
        var unknown = "{5E900000-0000-4000-8000-000000000001}{99}{B0}";

        var error = Error(await Tool().DeletePageContent(PageId, unknown, BasisArgument(), Ct), "UnknownObjectId");

        Assert.Contains(unknown, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task AnOutlineAlreadyDeleted_IsUnknownObjectId()
    {
        gateway.Pages[PageId] = After(p => Drop(p, 40));

        Error(await Tool().DeletePageContent(PageId, G(40), cancellationToken: Ct), "UnknownObjectId");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task AnEmptyObjectId_IsInvalidArgument_NamingTheArgument_BeforeAnythingIsRead()
    {
        gateway.Calls.Clear();

        var error = Error(await Tool().DeletePageContent(PageId, string.Empty, BasisArgument(), Ct), "InvalidArgument");

        Assert.Contains("objectId", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.PageCalls);
        Assert.Empty(gateway.Calls);
        Assert.Empty(holds);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task StillThereAfter_IsOk_WithReadBackDiffersNamingIt()
    {
        // No OnDelete: OneNote "kept" the Outline.
        var root = Ok(await Tool().DeletePageContent(PageId, G(40), BasisArgument(), Ct));

        Assert.Equal(["readBackDiffers"], Codes(root));
        Assert.Contains(G(40), Warning(root, "readBackDiffers"), StringComparison.Ordinal);
        Assert.True(root.GetProperty("meta").TryGetProperty("basis", out _));
        Assert.Single(gateway.DeleteCalls);
        await AssertLockFreeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReadBackThatThrows_IsOk_WithReadBackFailed_AndNoBasis(bool cancelled)
    {
        Exception failure = cancelled ? new OperationCanceledException() : new OneNoteException(ErrorCode.ComBusy, "busy");
        gateway.OnDelete = (_, _) => gateway.PageFailures[PageId] = failure;

        var root = Ok(await Tool().DeletePageContent(PageId, G(40), BasisArgument(), Ct));

        Assert.Equal(["readBackFailed"], Codes(root));
        Assert.False(root.GetProperty("meta").TryGetProperty("basis", out _));
        Assert.Single(gateway.DeleteCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ReadBack_RunsUnderTheLock_AfterTheCall()
    {
        // Step 3's read, the snapshot's piBinaryData read, then the read-back, with the write lock still held (AD-18).
        Deletes(PageId);
        var lockHeldAtReadBack = false;
        var reads = 0;
        var probe = new ProbingGateway(gateway, async () =>
        {
            if (++reads == 3)
            {
                var busy = await Assert.ThrowsAsync<OneNoteException>(() => writeLock.AcquireAsync("held?", TimeSpan.FromMilliseconds(100), Ct));
                lockHeldAtReadBack = busy.Code == ErrorCode.ComBusy;
            }
        });

        var root = Ok(await Tool(probe).DeletePageContent(PageId, G(40), BasisArgument(), Ct));

        Assert.Empty(Codes(root));
        Assert.True(lockHeldAtReadBack);
        Assert.Equal([(PageId, PageInfo.Basic), (PageId, PageInfo.BinaryData), (PageId, PageInfo.Basic)], gateway.PageCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AnAmbiguousStep7Failure_IsOutcomeUnknown_WithNoReadBack()
    {
        gateway.DeleteFailure = new OneNoteException(ErrorCode.Timeout, "DeletePageContent did not return within 30 s; the connection to OneNote was reset.");

        var error = Error(await Tool().DeletePageContent(PageId, G(40), BasisArgument(), Ct), "Timeout");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Equal([(PageId, PageInfo.Basic), (PageId, PageInfo.BinaryData)], gateway.PageCalls);
        Assert.Single(Directory.GetFiles(Folder));
        Assert.False(cache.TryGet(PageId, DateTimeOffset.UnixEpoch, out _));
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ASnapshotThatFails_IsJournalFailed_WithNoComDelete()
    {
        File.WriteAllText(Folder, "a file where the journal folder should be");

        Error(await Tool().DeletePageContent(PageId, G(40), BasisArgument(), Ct), "JournalFailed");

        Assert.Empty(gateway.DeleteCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AnAbandonedWriteOutstanding_IsComBusy_NothingReadOrDeleted()
    {
        gateway.AbandonedWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Calls.Clear();

        var error = Error(await Tool().DeletePageContent(PageId, G(40), BasisArgument(), Ct), "ComBusy");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Empty(gateway.PageCalls);
        Assert.DoesNotContain((PageId, HierarchyScope.Self), gateway.Calls);
        await AssertRefusedAsync();
        gateway.AbandonedWrites.SetResult();
    }

    // --- Protected sections (Q1) -----------------------------------------------------------------------------------------

    [Fact]
    public async Task ProtectedWithoutAReadGrant_IsReadNotGranted_NeverAsks_NothingReadOrDeleted()
    {
        var prompt = new FakePrompt(FakePrompt.Accept("Vault", ReadGrantLifetime.Once));

        var ex = await Assert.ThrowsAsync<OneNoteException>(() =>
            UseCase().ExecuteAsync(new ToolCallContext("c", Prompt: prompt), VaultPageId, G(40), null, Ct));

        Assert.Equal(ErrorCode.ReadNotGranted, ex.Code);
        Assert.Equal(DeletePageContentWrite.ReadFirstUserAction, ex.UserAction);
        Assert.Contains("get_page_content", ex.UserAction, StringComparison.Ordinal);
        Assert.Contains(ProtectedId, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(G(40), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Vault", ex.Message, StringComparison.Ordinal);
        Assert.Empty(prompt.Questions);
        Assert.Empty(gateway.PageCalls);
        Assert.Empty(readAccess.OnceGrants());
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task ProtectedWithoutAReadGrant_ThroughTheTool_IsReadNotGranted_AndItsUserAction()
    {
        var error = Error(await Tool().DeletePageContent(VaultPageId, G(40), cancellationToken: Ct), "ReadNotGranted");

        Assert.Equal(DeletePageContentWrite.ReadFirstUserAction, error.GetProperty("userAction").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task ProtectedWithAStoredReadGrant_Deletes_AndReturnsMetaBasis()
    {
        new SettingsWriter(Store, clock).SaveReadGrant(new ProtectedSectionGrant(
            GrantKeys.SectionKey(@"C:\notebooks\W\Vault.one"), "Vault", GrantKeys.NotebookKey(NotebookPath), ProtectedId, clock.GetUtcNow(), GrantedVia.Cli));
        Deletes(VaultPageId);

        var success = await UseCase().ExecuteAsync(new ToolCallContext("c"), VaultPageId, G(40), null, Ct);

        Assert.NotNull(success.Meta.Basis);
        Assert.Equal(["noBasis"], success.Warnings.Select(w => w.Code));
        Assert.Equal(VaultPageId, Assert.Single(gateway.DeleteCalls).PageId);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ProtectedWithAOnceReadGrant_Deletes_AndReturnsMetaBasis()
    {
        // The read that asked (get_page_content) opened the once window; the delete runs inside it without asking.
        var asking = new FakePrompt(FakePrompt.Accept("Vault", ReadGrantLifetime.Once));
        await readAccess.CheckObjectAsync(new ToolCallContext("read", Prompt: asking), VaultPageId, Ct);
        Assert.Single(readAccess.OnceGrants());
        Deletes(VaultPageId);
        var prompt = new FakePrompt();

        var success = await UseCase().ExecuteAsync(new ToolCallContext("c", Prompt: prompt), VaultPageId, G(40), null, Ct);

        Assert.NotNull(success.Meta.Basis);
        Assert.Empty(prompt.Questions);
        Assert.Single(gateway.DeleteCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ProtectedWithoutAReadGrant_AndNoWriteGrant_IsReadNotGranted_BeforeTheWriteGrantDialog()
    {
        // AD-7 step 1 as amended by 5.2: the no-prompt read-grant check comes before the write grant, so the user is never
        // asked for a write grant for a delete that would fail anyway.
        File.Delete(Store.Path);
        var prompt = new FakePrompt(FakePrompt.Accept("W", ReadGrantLifetime.Once));

        var ex = await Assert.ThrowsAsync<OneNoteException>(() =>
            UseCase().ExecuteAsync(new ToolCallContext("c", Prompt: prompt), VaultPageId, G(40), null, Ct));

        Assert.Equal(ErrorCode.ReadNotGranted, ex.Code);
        Assert.Empty(prompt.Questions);
        Assert.Empty(gateway.PageCalls);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task ProtectedAndLocked_WithoutAReadGrant_IsReadNotGranted()
    {
        Error(await Tool().DeletePageContent(LockedPageId, G(40), cancellationToken: Ct), "ReadNotGranted");

        Assert.Empty(gateway.PageCalls);
        await AssertRefusedAsync();
    }

    // --- Step 1 and step 3 -------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASectionId_IsObjectNotFound()
    {
        var error = Error(await Tool().DeletePageContent(SectionId, G(40), cancellationToken: Ct), "ObjectNotFound");

        Assert.Equal(PageWrite.NotAPageUserAction, error.GetProperty("userAction").GetString());
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task APageInTheRecycleBin_IsObjectNotFound()
    {
        Error(await Tool().DeletePageContent(BinnedPageId, G(40), cancellationToken: Ct), "ObjectNotFound");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task APageInALockedSection_WithAReadGrant_IsSectionLocked_BeforeThePageIsRead()
    {
        new SettingsWriter(Store, clock).SaveReadGrant(new ProtectedSectionGrant(
            GrantKeys.SectionKey(@"C:\notebooks\W\Locked vault.one"), "Locked vault", GrantKeys.NotebookKey(NotebookPath), LockedId, clock.GetUtcNow(), GrantedVia.Cli));

        Error(await Tool().DeletePageContent(LockedPageId, G(40), cancellationToken: Ct), "SectionLocked");

        Assert.Empty(gateway.PageCalls);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task NoWriteGrant_IsWriteNotGranted()
    {
        File.Delete(Store.Path);

        Error(await Tool().DeletePageContent(PageId, G(40), cancellationToken: Ct), "WriteNotGranted");

        await AssertRefusedAsync();
    }

    // --- Helpers ------------------------------------------------------------------------------------------------------

    private void Self(string id, string xml) => gateway.Hierarchies[(id, HierarchyScope.Self)] = "<?xml version=\"1.0\"?>\n" + xml;

    private static string Section(string id, string name, string extra) =>
        $"<one:Section {Ns} name=\"{name}\" ID=\"{id}\" path=\"C:\\notebooks\\W\\{name}.one\" {extra} />";

    private void Grant() =>
        new SettingsWriter(Store, clock).SaveWriteGrant(
            new NotebookGrant(GrantKeys.NotebookKey(NotebookPath), "W", NotebookId, Write: true, PermanentDelete: false, clock.GetUtcNow(), GrantedVia.Cli));

    /// <summary>After a successful delete, <paramref name="pageId"/> reads back without Outline 40; returns that XML.</summary>
    private string Deletes(string pageId, string? lastModified = null)
    {
        var after = After(p =>
        {
            Drop(p, 40);
            if (lastModified is not null)
            {
                p.SetAttributeValue("lastModifiedTime", lastModified);
            }
        }).Replace(PageId, pageId, StringComparison.Ordinal);
        gateway.OnDelete = (page, _) => gateway.Pages[page] = after;
        return after;
    }

    /// <summary>The read's basis, with <paramref name="date"/> or a hash replaced.</summary>
    private static PageBasisArgument BasisArgument(string? date = null, (string Id, string Hash)? hash = null)
    {
        var basis = PageBasis.From(Read);
        var hashes = new Dictionary<string, string>(basis.ElementHashes, StringComparer.Ordinal);
        if (hash is { } h)
        {
            hashes[h.Id] = h.Hash;
        }

        return new PageBasisArgument(date ?? basis.LastModifiedTime, basis.PageHash, hashes);
    }

    private PageWriteServices Services(IOneNoteGateway g) => new(
        g,
        new WriteAccess(g, Store.Read),
        ReferenceEquals(g, gateway) ? readAccess : new ReadAccess(g, Store.Read, new SettingsWriter(Store, clock), clock),
        new PageSnapshot(g, new JournalStore(Store.Read), clock));

    private DeletePageContentUseCase UseCase(IOneNoteGateway? over = null)
    {
        var g = over ?? gateway;
        return new DeletePageContentUseCase(Services(g), new WritePipeline(writeLock, g, cache, Store.Read, TimeProvider.System));
    }

    private DeletePageContentTool Tool(IOneNoteGateway? over = null) => new(new ToolRunner(NullLogger<ToolRunner>.Instance), UseCase(over));

    private async Task AssertRefusedAsync()
    {
        Assert.Empty(gateway.DeleteCalls);
        Assert.Empty(gateway.UpdateCalls);
        Assert.False(Directory.Exists(Folder) && Directory.EnumerateFileSystemEntries(Folder).Any());
        await AssertLockFreeAsync();
    }

    private async Task AssertLockFreeAsync()
    {
        using var probe = await writeLock.AcquireAsync("free?", TimeSpan.FromMilliseconds(500), Ct);
    }

    private static List<string> Codes(JsonElement root) =>
        [.. root.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()!)];

    private static string Warning(JsonElement root, string code) =>
        root.GetProperty("warnings").EnumerateArray().First(w => w.GetProperty("code").GetString() == code).GetProperty("message").GetString()!;

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

    /// <summary>Wraps the fake: runs <paramref name="onPageRead"/> before each <c>GetPageContent</c>.</summary>
    private sealed class ProbingGateway(FakeGateway inner, Func<Task> onPageRead) : IOneNoteGateway
    {
        public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct) => inner.GetHierarchyAsync(startNodeId, scope, ct);

        public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) => inner.GetHierarchyParentAsync(objectId, ct);

        public async Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct)
        {
            await onPageRead();
            return await inner.GetPageContentAsync(pageId, pageInfo, ct);
        }

        public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) => inner.GetBinaryPageContentAsync(pageId, callbackId, ct);

        public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) => inner.FindPagesAsync(startNodeId, query, includeUnindexed, ct);

        public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) => inner.FindMetaAsync(startNodeId, name, includeUnindexed, ct);

        public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => inner.GetHyperlinkToObjectAsync(hierarchyId, pageContentObjectId, ct);

        public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => inner.GetWebHyperlinkToObjectAsync(hierarchyId, pageContentObjectId, ct);

        public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => inner.GetSpecialLocationAsync(location, ct);

        public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct) => inner.NavigateToAsync(hierarchyId, objectId, ct);

        public Task<string> CreateNewPageAsync(string sectionId, NewPageStyle style, CancellationToken ct) => inner.CreateNewPageAsync(sectionId, style, ct);

        public Task<string> CreateSectionAsync(string parentId, string sectionFileName, CancellationToken ct) => inner.CreateSectionAsync(parentId, sectionFileName, ct);

        public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct) => inner.UpdatePageContentAsync(pageXml, expectedLastModified, ct);

        public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct) => inner.DeletePageContentAsync(pageId, objectId, expectedLastModified, ct);

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => inner.WaitForAbandonedWritesAsync(ct);
    }
}
