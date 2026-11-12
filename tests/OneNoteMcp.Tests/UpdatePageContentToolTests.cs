using System.Collections.Concurrent;
using System.Globalization;
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
/// Story 2.9: <c>update_page_content</c> through the AD-7 write pipeline, one test per row of the plan's I/O matrix,
/// against <see cref="FakeGateway"/> scripted with notebook <c>W</c> (an open section, a password-protected one, a locked
/// one, the recycle bin), the synthetic page <c>synthetic/update-page-Basic.xml</c>, a temporary <c>settings.json</c> and
/// journal folder. Each test has a write lock of its own and checks it is free again after every outcome; refusals in
/// steps 3–5 leave no COM write and no journal entry.
/// </summary>
public sealed class UpdatePageContentToolTests : IDisposable
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
    private const string ReadDate = "2026-11-12T00:00:00.000Z";

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly FakeGateway gateway = new();
    private readonly ConcurrentQueue<WriteLockHeld> holds = new();
    private readonly WriteLock writeLock;
    private readonly PageTextCache cache = PageTextCache.WithDefaults();

    public UpdatePageContentToolTests()
    {
        writeLock = new WriteLock($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", holds.Enqueue);

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
    public async Task FreshEdit_IsOk_WithOneJournalEntry_TheCallersDateGoesToCom_AndMetaBasisComesFromTheReadBack()
    {
        cache.Set(PageId, DateTimeOffset.UnixEpoch, "cached text", long.MaxValue);
        var sent = Sent(EditedOutlineB());
        var written = AfterWrite(p => SetText(p, 41, "changed"), "2026-11-12T00:05:00.000Z");

        // The same instant as the read's date, written differently: compared as UTC instants, passed to COM unchanged.
        var root = Ok(await Tool().UpdatePageContent(PageId, sent, BasisArgument(date: "2026-11-12T00:00:00Z"), cancellationToken: Ct));

        var (xml, date) = Assert.Single(gateway.UpdateCalls);
        Assert.Equal(sent, xml);
        Assert.Equal("2026-11-12T00:00:00Z", date);
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        var basis = root.GetProperty("meta").GetProperty("basis");
        Assert.Equal(PageBasis.From(written).PageHash, basis.GetProperty("pageHash").GetString());
        Assert.Equal("2026-11-12T00:05:00.000Z", basis.GetProperty("lastModifiedTime").GetString());
        var entry = JournalStore.Read(Assert.Single(Directory.GetFiles(Folder)));
        Assert.Equal("update_page_content", entry.Operation);
        Assert.Equal(nameof(OperationClass.PageWrite), entry.OperationClass);
        Assert.Equal(root.GetProperty("meta").GetProperty("correlationId").GetString(), entry.CorrelationId);
        Assert.False(cache.TryGet(PageId, DateTimeOffset.UnixEpoch, out _));
        Assert.Equal(root.GetProperty("meta").GetProperty("correlationId").GetString(), Assert.Single(holds).CorrelationId);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task StaleHash_IsStaleWrite_WithNoSnapshot_AndNoComWrite()
    {
        var error = Error(await Tool().UpdatePageContent(PageId, Sent(EditedOutlineB()), BasisArgument(hash: (G(40), new string('0', 64))), cancellationToken: Ct), "StaleWrite");

        Assert.Contains(G(40), error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task StaleDate_IsStaleWrite_WithNoComWrite()
    {
        Error(await Tool().UpdatePageContent(PageId, Sent(EditedOutlineB()), BasisArgument(date: "2026-11-11T23:00:00.000Z"), cancellationToken: Ct), "StaleWrite");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task ABasisDateThatIsntADate_IsInvalidArgument()
    {
        Error(await Tool().UpdatePageContent(PageId, Sent(EditedOutlineB()), BasisArgument(date: "yesterday"), cancellationToken: Ct), "InvalidArgument");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task NoBasis_RunsWithStep3sDate_AndWarnsNoBasis()
    {
        var root = Ok(await Tool().UpdatePageContent(PageId, Sent(EditedOutlineB()), cancellationToken: Ct));

        Assert.Equal(ReadDate, Assert.Single(gateway.UpdateCalls).ExpectedLastModified);
        Assert.Equal(["noBasis"], Codes(root));
        Assert.Single(Directory.GetFiles(Folder));
    }

    [Fact]
    public async Task PartialBasis_AMissingHash_SkipsThatCheck_AndWarnsPartialBasis()
    {
        var basis = new PageBasisArgument(ReadDate, null, new Dictionary<string, string>());

        var root = Ok(await Tool().UpdatePageContent(PageId, Sent(EditedOutlineB()), basis, cancellationToken: Ct));

        Assert.Equal(["partialBasis"], Codes(root));
        Assert.Contains(G(40), Warning(root, "partialBasis"), StringComparison.Ordinal);
        Assert.Single(gateway.UpdateCalls);
    }

    [Fact]
    public async Task PartialBasis_AMissingDate_TakesStep3sDate()
    {
        var current = PageBasis.From(Read);
        var basis = new PageBasisArgument(null, null, current.ElementHashes);

        var root = Ok(await Tool().UpdatePageContent(PageId, Sent(EditedOutlineB()), basis, cancellationToken: Ct));

        Assert.Equal(["partialBasis"], Codes(root));
        Assert.Equal(ReadDate, Assert.Single(gateway.UpdateCalls).ExpectedLastModified);
    }

    [Fact]
    public async Task HashesForUnsentElements_AreIgnored()
    {
        // Outline A changed since the read (its hash in the basis is stale), but only Outline B is sent.
        var root = Ok(await Tool().UpdatePageContent(PageId, Sent(EditedOutlineB()), BasisArgument(hash: (G(20), new string('0', 64))), cancellationToken: Ct));

        Assert.Empty(Codes(root));
    }

    [Fact]
    public async Task OeOmitted_IsInvalidArgument_WithItsOwnUserAction()
    {
        var error = Error(await Tool().UpdatePageContent(PageId, Sent(OutlineAsRead(40, o => Drop(o, 42))), BasisArgument(), cancellationToken: Ct), "InvalidArgument");

        Assert.Equal(UpdatePageContentWrite.OmittedUserAction, error.GetProperty("userAction").GetString());
        Assert.Contains(G(42), error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task OeRemoved_ByName_IsOk()
    {
        var sent = Sent(OutlineAsRead(40, o => Drop(o, 42)));
        AfterWrite(p => Drop(p, 42));

        var root = Ok(await Tool().UpdatePageContent(PageId, sent, BasisArgument(), [G(42)], Ct));

        Assert.Empty(Codes(root));
        Assert.Equal(sent, Assert.Single(gateway.UpdateCalls).PageXml);
    }

    [Fact]
    public async Task UnknownObjectId_IsUnknownObjectId()
    {
        var sent = Sent(OutlineAsRead(40, o => o.Descendants(One + "OE").First().SetAttributeValue("objectID", G(77))));

        Error(await Tool().UpdatePageContent(PageId, sent, BasisArgument(), cancellationToken: Ct), "UnknownObjectId");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task RootIdThatIsntThePage_IsInvalidArgument()
    {
        var error = Error(await Tool().UpdatePageContent(PageId, Sent(EditedOutlineB(), VaultPageId), BasisArgument(), cancellationToken: Ct), "InvalidArgument");

        Assert.Equal(UpdatePageContentWrite.PageIdUserAction, error.GetProperty("userAction").GetString());
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task BadSchema_IsInvalidXml_WithLineAndPositionOnly()
    {
        var sent = Sent("<one:Outline><one:OEChildren><one:OE objectID=\"secret-value\"><one:T>secret text</one:T></one:OE></one:OEChildren></one:Outline>");

        var error = Error(await Tool().UpdatePageContent(PageId, sent, cancellationToken: Ct), "InvalidXml");

        var message = error.GetProperty("message").GetString()!;
        Assert.Matches(@"line \d+, position \d+ \(one:OE\)", message);
        Assert.DoesNotContain("secret", message, StringComparison.Ordinal);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task AnIgnoredQuickStyleIndex_IsOk_WithReadBackDiffers()
    {
        // OneNote drops a changed quickStyleIndex on an existing OE (spike 2.2 constraint 7): the page reads back as before.
        var sent = Sent(OutlineAsRead(40, o => o.Descendants(One + "OE").First().SetAttributeValue("quickStyleIndex", "0")));

        var root = Ok(await Tool().UpdatePageContent(PageId, sent, BasisArgument(), cancellationToken: Ct));

        var message = Warning(root, "readBackDiffers");
        Assert.Contains(G(41), message, StringComparison.Ordinal);
        Assert.Contains("quickStyleIndex", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnImageGoneAfterTheWrite_IsOk_WithImageLost()
    {
        AfterWrite(p => p.Descendants(One + "Image").Single().ReplaceWith(new XElement(One + "T", "gone")));

        var root = Ok(await Tool().UpdatePageContent(PageId, Sent(OutlineAsRead(20)), BasisArgument(), cancellationToken: Ct));

        Assert.Equal(["imageLost"], Codes(root));
        Assert.Contains(G(20), Warning(root, "imageLost"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReadBackThatThrows_IsOk_WithReadBackFailed_AndNoBasis(bool cancelled)
    {
        Exception failure = cancelled ? new OperationCanceledException() : new OneNoteException(ErrorCode.ComBusy, "busy");
        gateway.OnUpdate = _ => gateway.PageFailures[PageId] = failure;

        var root = Ok(await Tool().UpdatePageContent(PageId, Sent(EditedOutlineB()), BasisArgument(), cancellationToken: Ct));

        Assert.Equal(["readBackFailed"], Codes(root));
        Assert.False(root.GetProperty("meta").TryGetProperty("basis", out _));
        Assert.Single(gateway.UpdateCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ReadBack_RunsUnderTheLock_AfterTheCall()
    {
        // Step 3's read, the snapshot's piBinaryData read, then the read-back, with the write lock still held (AD-18).
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

        var root = Ok(await Tool(probe).UpdatePageContent(PageId, Sent(EditedOutlineB()), BasisArgument(), cancellationToken: Ct));

        Assert.Empty(Codes(root));
        Assert.True(lockHeldAtReadBack);
        Assert.Equal([(PageId, PageInfo.Basic), (PageId, PageInfo.BinaryData), (PageId, PageInfo.Basic)], gateway.PageCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ProtectedWithoutAReadGrant_IsOk_NeverAsks_AndNamesNothingInsideThePage()
    {
        var prompt = new FakePrompt();
        var sent = Sent(OutlineAsRead(40, o => o.Descendants(One + "OE").First().SetAttributeValue("quickStyleIndex", "0")), VaultPageId);

        var success = await UseCase().ExecuteAsync(new ToolCallContext("c", Prompt: prompt), VaultPageId, sent, null, null, Ct);

        Assert.Empty(prompt.Questions);
        Assert.Null(success.Meta.Basis);
        Assert.Equal(["noBasis", "readBackDiffers"], success.Warnings.Select(w => w.Code));
        Assert.All(success.Warnings, w => Assert.DoesNotContain("{B0}", w.Message, StringComparison.Ordinal));
        Assert.Contains("1 attribute(s)", success.Warnings[1].Message, StringComparison.Ordinal);
        Assert.Single(gateway.UpdateCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ProtectedWithoutAReadGrant_ARefusal_CarriesCountsOnly()
    {
        var sent = Sent(OutlineAsRead(40, o => Drop(o, 42)), VaultPageId);

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => UseCase().ExecuteAsync(new ToolCallContext("c"), VaultPageId, sent, null, null, Ct));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
        Assert.DoesNotContain(G(42), ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 OE(s)", ex.Message, StringComparison.Ordinal);
        Assert.Empty(gateway.UpdateCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ProtectedWithAStoredReadGrant_NamesIds_AndReturnsMetaBasis()
    {
        new SettingsWriter(Store, clock).SaveReadGrant(new ProtectedSectionGrant(
            GrantKeys.SectionKey(@"C:\notebooks\W\Vault.one"), "Vault", GrantKeys.NotebookKey(NotebookPath), ProtectedId, clock.GetUtcNow(), GrantedVia.Cli));
        var sent = Sent(OutlineAsRead(40, o => o.Descendants(One + "OE").First().SetAttributeValue("quickStyleIndex", "0")), VaultPageId);

        var success = await UseCase().ExecuteAsync(new ToolCallContext("c"), VaultPageId, sent, null, null, Ct);

        Assert.NotNull(success.Meta.Basis);
        Assert.Contains(G(41), success.Warnings.Single(w => w.Code == "readBackDiffers").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAbandonedWriteOutstanding_IsComBusy_NothingReadOrWritten()
    {
        gateway.AbandonedWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.Calls.Clear();

        var error = Error(await Tool().UpdatePageContent(PageId, Sent(EditedOutlineB()), BasisArgument(), cancellationToken: Ct), "ComBusy");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Empty(gateway.PageCalls);
        Assert.DoesNotContain((PageId, HierarchyScope.Self), gateway.Calls);
        await AssertRefusedAsync();
        gateway.AbandonedWrites.SetResult();
    }

    [Fact]
    public async Task AnAmbiguousStep7Failure_IsOutcomeUnknown_WithNoReadBack()
    {
        gateway.UpdateFailure = new OneNoteException(ErrorCode.Timeout, "UpdatePageContent did not return within 30 s; the connection to OneNote was reset.");

        var error = Error(await Tool().UpdatePageContent(PageId, Sent(EditedOutlineB()), BasisArgument(), cancellationToken: Ct), "Timeout");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Equal([(PageId, PageInfo.Basic), (PageId, PageInfo.BinaryData)], gateway.PageCalls);
        Assert.Single(Directory.GetFiles(Folder));
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ASnapshotThatFails_IsJournalFailed_WithNoComWrite()
    {
        File.WriteAllText(Folder, "a file where the journal folder should be");

        Error(await Tool().UpdatePageContent(PageId, Sent(EditedOutlineB()), BasisArgument(), cancellationToken: Ct), "JournalFailed");

        Assert.Empty(gateway.UpdateCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task OeMoved_IsInvalidArgument_WithTheRecreateAndRemoveUserAction()
    {
        var sent = Sent(OutlineAsRead(20, o => o.Element(One + "OEChildren")!.Add(Element(41))) + OutlineAsRead(40, o => Drop(o, 41)));

        var error = Error(await Tool().UpdatePageContent(PageId, sent, BasisArgument(), [G(41)], Ct), "InvalidArgument");

        Assert.Equal(UpdatePageContentWrite.MovedUserAction, error.GetProperty("userAction").GetString());
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task StaleTitle_IsStaleWrite()
    {
        var title = new XElement(One + "Title", Element(10));
        SetText(title, 10, "New title");

        Error(await Tool().UpdatePageContent(PageId, Sent(Text(title)), BasisArgument(hash: (G(10), new string('0', 64))), cancellationToken: Ct), "StaleWrite");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task FreshTitle_IsOk()
    {
        var title = new XElement(One + "Title", Element(10));
        SetText(title, 10, "New title");

        Ok(await Tool().UpdatePageContent(PageId, Sent(Text(title)), BasisArgument(), cancellationToken: Ct));

        Assert.Single(gateway.UpdateCalls);
    }

    // --- Step 3 ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASectionId_IsObjectNotFound()
    {
        var error = Error(await Tool().UpdatePageContent(SectionId, Sent(EditedOutlineB(), SectionId), cancellationToken: Ct), "ObjectNotFound");

        Assert.Equal(PageWrite.NotAPageUserAction, error.GetProperty("userAction").GetString());
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task APageInTheRecycleBin_IsObjectNotFound()
    {
        Error(await Tool().UpdatePageContent(BinnedPageId, Sent(EditedOutlineB(), BinnedPageId), cancellationToken: Ct), "ObjectNotFound");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task APageInALockedSection_IsSectionLocked_BeforeThePageIsRead()
    {
        Error(await Tool().UpdatePageContent(LockedPageId, Sent(EditedOutlineB(), LockedPageId), cancellationToken: Ct), "SectionLocked");

        Assert.Empty(gateway.PageCalls);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task NoWriteGrant_IsWriteNotGranted()
    {
        File.Delete(Store.Path);

        Error(await Tool().UpdatePageContent(PageId, Sent(EditedOutlineB()), cancellationToken: Ct), "WriteNotGranted");

        await AssertRefusedAsync();
    }

    // --- Warnings --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadBackWarnings_AreCappedAt20PerCode_ThenOneSummary()
    {
        // 25 new OEs whose alignment OneNote "ignores".
        var sent = Sent(OutlineAsRead(40, o => o.Element(One + "OEChildren")!.Add(
            Enumerable.Range(0, 25).Select(_ => new XElement(One + "OE", new XAttribute("alignment", "right"), new XElement(One + "T", "n"))))));
        AfterWrite(p => p.Descendants().Single(e => (string?)e.Attribute("objectID") == G(40)).Element(One + "OEChildren")!.Add(
            Enumerable.Range(0, 25).Select(i => new XElement(One + "OE", new XAttribute("objectID", G(100 + i)), new XAttribute("alignment", "left"), new XElement(One + "T", "n")))));

        var root = Ok(await Tool().UpdatePageContent(PageId, sent, BasisArgument(), cancellationToken: Ct));

        var warnings = root.GetProperty("warnings").EnumerateArray().ToList();
        Assert.Equal(21, warnings.Count);
        Assert.All(warnings, w => Assert.Equal("readBackDiffers", w.GetProperty("code").GetString()));
        Assert.StartsWith("5 more readBackDiffers", warnings[^1].GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // --- Helpers ------------------------------------------------------------------------------------------------------

    private void Self(string id, string xml) => gateway.Hierarchies[(id, HierarchyScope.Self)] = "<?xml version=\"1.0\"?>\n" + xml;

    private static string Section(string id, string name, string extra) =>
        $"<one:Section {Ns} name=\"{name}\" ID=\"{id}\" path=\"C:\\notebooks\\W\\{name}.one\" {extra} />";

    private void Grant() =>
        new SettingsWriter(Store, clock).SaveWriteGrant(
            new NotebookGrant(GrantKeys.NotebookKey(NotebookPath), "W", NotebookId, Write: true, PermanentDelete: false, clock.GetUtcNow(), GrantedVia.Cli));

    private static void SetText(XElement root, int n, string text) =>
        root.DescendantsAndSelf().Single(e => (string?)e.Attribute("objectID") == G(n)).Element(One + "T")!.Value = text;

    /// <summary>Outline B as read, with its first OE's text changed.</summary>
    private static string EditedOutlineB() => OutlineAsRead(40, o => SetText(o, 41, "changed"));

    /// <summary>After a successful write, the page reads back as the read page changed by <paramref name="edit"/>; returns that XML.</summary>
    private string AfterWrite(Action<XElement> edit, string? lastModified = null)
    {
        var after = After(p =>
        {
            edit(p);
            if (lastModified is not null)
            {
                p.SetAttributeValue("lastModifiedTime", lastModified);
            }
        });
        gateway.OnUpdate = _ => gateway.Pages[PageId] = after;
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
        new ReadAccess(g, Store.Read, new SettingsWriter(Store, clock), clock),
        new PageSnapshot(g, new JournalStore(Store.Read), clock));

    private UpdatePageContentUseCase UseCase(IOneNoteGateway? over = null)
    {
        var g = over ?? gateway;
        return new UpdatePageContentUseCase(Services(g), new WritePipeline(writeLock, g, cache, Store.Read, TimeProvider.System));
    }

    private UpdatePageContentTool Tool(IOneNoteGateway? over = null) => new(new ToolRunner(NullLogger<ToolRunner>.Instance), UseCase(over));

    private async Task AssertRefusedAsync()
    {
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

        public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct) => inner.UpdatePageContentAsync(pageXml, expectedLastModified, ct);

        public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct) => inner.DeletePageContentAsync(pageId, objectId, expectedLastModified, ct);

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => inner.WaitForAbandonedWritesAsync(ct);
    }
}
