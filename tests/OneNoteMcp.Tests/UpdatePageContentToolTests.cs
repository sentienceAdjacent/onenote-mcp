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
        Assert.Equal(entry.EntryId.ToString("D"), root.GetProperty("meta").GetProperty("journalEntryId").GetString()); // 2.11 Q1
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
    public async Task ProtectedWithoutAReadGrant_IsOk_NeverAsks_NamesNothingInsideThePage_AndSaysTheReadBackIsWithheld()
    {
        var prompt = new FakePrompt();
        var sent = Sent(NewOutline("alignment=\"right\""), VaultPageId);
        VaultAfterWrite(p => p.Add(new XElement(One + "Outline", new XAttribute("objectID", G(60)), new XElement(One + "OEChildren",
            new XElement(One + "OE", new XAttribute("objectID", G(61)), new XElement(One + "T", "new"))))));

        var success = await UseCase().ExecuteAsync(new ToolCallContext("c", Prompt: prompt), VaultPageId, sent, null, null, Ct);

        Assert.Empty(prompt.Order);
        Assert.Null(success.Meta.Basis);
        Assert.Equal(["noBasis", "readBackDiffers", "readBackWithheld"], success.Warnings.Select(w => w.Code));
        Assert.All(success.Warnings, w => Assert.DoesNotContain("{B0}", w.Message, StringComparison.Ordinal));
        Assert.Contains("1 attribute(s)", success.Warnings[1].Message, StringComparison.Ordinal);
        Assert.DoesNotMatch("[0-9]", success.Warnings[2].Message.Replace(VaultPageId, string.Empty, StringComparison.Ordinal));
        Assert.Single(gateway.UpdateCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ProtectedWithoutAReadGrant_AReadBackWithNoDifference_StillSaysItIsWithheld()
    {
        var sent = Sent(NewOutline(string.Empty), VaultPageId);
        VaultAfterWrite(p => p.Add(new XElement(One + "Outline", new XAttribute("objectID", G(60)), new XElement(One + "OEChildren",
            new XElement(One + "OE", new XAttribute("objectID", G(61)), new XElement(One + "T", "new"))))));

        var success = await UseCase().ExecuteAsync(new ToolCallContext("c"), VaultPageId, sent, new PageWriteBasis(ReadDate), null, Ct);

        Assert.Equal(["readBackWithheld"], success.Warnings.Select(w => w.Code));
        Assert.Null(success.Meta.Basis);
    }

    // --- Date-only basis without a read grant (AD-17 as amended by 5.2; Phill, 2026-11-12, story 2.9 security review) --

    public static TheoryData<string> OraclesWithoutAReadGrant => ["existing objectID", "element hash", "pageHash", "removeObjectIds", "objectID on a new element"];

    [Theory]
    [MemberData(nameof(OraclesWithoutAReadGrant))]
    public async Task ProtectedWithoutAReadGrant_AnyHashObjectIdOrRemoval_IsReadNotGranted_BeforeThePageIsRead(string oracle)
    {
        var prompt = new FakePrompt();
        var (xml, basis, remove) = oracle switch
        {
            "existing objectID" => (Sent(OutlineAsRead(40), VaultPageId), (PageWriteBasis?)null, (IReadOnlyList<string>?)null),
            "element hash" => (Sent(NewOutline(string.Empty), VaultPageId), new PageWriteBasis(ReadDate, ElementHashes: new Dictionary<string, string> { [G(40)] = "00" }), null),
            "pageHash" => (Sent(NewOutline(string.Empty), VaultPageId), new PageWriteBasis(ReadDate, PageHash: "00"), null),
            "removeObjectIds" => (Sent(NewOutline(string.Empty), VaultPageId), null, [G(42)]),
            _ => (Sent($"<one:Outline objectID=\"{G(99)}\"><one:OEChildren><one:OE><one:T>new</one:T></one:OE></one:OEChildren></one:Outline>", VaultPageId), null, null),
        };

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => UseCase().ExecuteAsync(new ToolCallContext("c", Prompt: prompt), VaultPageId, xml, basis, remove, Ct));

        Assert.Equal(ErrorCode.ReadNotGranted, ex.Code);
        Assert.Equal(UpdatePageContentWrite.ReadFirstUserAction, ex.UserAction);
        Assert.Contains(ProtectedId, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{5E9", ex.Message, StringComparison.Ordinal);
        Assert.Empty(gateway.PageCalls);
        Assert.Empty(prompt.Order);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task ProtectedWithAStoredReadGrant_MayCarryHashesAndObjectIds()
    {
        GrantVaultRead();
        var sent = Sent(OutlineAsRead(40), VaultPageId);

        var success = await UseCase().ExecuteAsync(new ToolCallContext("c"), VaultPageId, sent, null, [], Ct);

        Assert.Single(gateway.UpdateCalls);
        Assert.DoesNotContain("readBackWithheld", success.Warnings.Select(w => w.Code));
    }

    // --- DOCTYPE (AD-16 as amended by 5.2) -------------------------------------------------------------------------------

    [Fact]
    public async Task ADoctype_IsInvalidXml_BeforeAnyOtherCheck_AndNoEntityIsExpanded()
    {
        File.Delete(Store.Path); // no write grant either: the DOCTYPE is refused first
        gateway.Calls.Clear();
        var prompt = new FakePrompt();
        var xml = "<?xml version=\"1.0\"?>\n<!DOCTYPE one:Page [<!ENTITY x \"expanded\">]>\n"
            + $"<one:Page {Ns} ID=\"{PageId}\"><one:Outline><one:OEChildren><one:OE><one:T>&x;</one:T></one:OE></one:OEChildren></one:Outline></one:Page>";

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => UseCase().ExecuteAsync(new ToolCallContext("c", Prompt: prompt), PageId, xml, null, null, Ct));

        Assert.Equal(ErrorCode.InvalidXml, ex.Code);
        Assert.Contains("DOCTYPE", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("expanded", ex.Message, StringComparison.Ordinal);
        Assert.Empty(gateway.Calls);
        Assert.Empty(gateway.PageCalls);
        Assert.Empty(prompt.Order);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task BrokenXml_IsInvalidXml_InStep1_WithItsPositionOnly()
    {
        gateway.Calls.Clear();

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => UseCase().ExecuteAsync(new ToolCallContext("c"), PageId, $"<one:Page {Ns} ID=\"{PageId}\"><one:Outline>secret", null, null, Ct));

        Assert.Equal(ErrorCode.InvalidXml, ex.Code);
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
        Assert.Empty(gateway.Calls);
        await AssertRefusedAsync();
    }

    // --- User-gated file paths (AD-7 as amended by 5.2) -------------------------------------------------------------------

    [Fact]
    public async Task ANewFilePath_ConfirmedInTheDialog_IsWritten_AndTheDialogNamesItCanonical()
    {
        var path = temp.Combine("a.txt");
        var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted));

        var success = await UseCase().ExecuteAsync(new ToolCallContext("c", Prompt: prompt), PageId, Sent(FileOutline(path)), null, null, Ct);

        var question = Assert.Single(prompt.FileQuestions);
        Assert.Equal(PageId, question.PageId);
        Assert.Equal([FilePathGate.Canonical(path, out _)!], question.Paths);
        Assert.Single(gateway.UpdateCalls);
        Assert.All(success.Warnings, w => Assert.DoesNotContain(path, w.Message, StringComparison.OrdinalIgnoreCase));
    }

    public static TheoryData<string, bool, string> UnconfirmedPaths => new()
    {
        { "declined", true, FilePathsUnconfirmed.DeclinedUserAction },
        { "cancelled", true, FilePathsUnconfirmed.DeclinedUserAction },
        { "unsupported", false, FilePathsUnconfirmed.NoDialogUserAction },
        { "no prompt", false, FilePathsUnconfirmed.NoDialogUserAction },
    };

    [Theory]
    [MemberData(nameof(UnconfirmedPaths))]
    public async Task ANewFilePath_NotConfirmed_IsWriteNotGranted_AndNothingIsWritten(string answer, bool retryable, string userAction)
    {
        var path = temp.Combine("secret.txt");
        FakePrompt? prompt = answer switch
        {
            "declined" => new FakePrompt(new GrantPromptResult(GrantPromptStatus.Declined)),
            "cancelled" => new FakePrompt(new GrantPromptResult(GrantPromptStatus.Cancelled)),
            "unsupported" => new FakePrompt(GrantPromptResult.Unsupported),
            _ => null,
        };

        var ex = await Assert.ThrowsAsync<FilePathsUnconfirmed>(() => UseCase().ExecuteAsync(new ToolCallContext("c", Prompt: prompt), PageId, Sent(FileOutline(path)), null, null, Ct));

        var error = ToolErrors.From(ex, OperationClass.PageWrite);
        Assert.Equal(ErrorCode.WriteNotGranted, ex.Code);
        Assert.Equal(retryable, error.Retryable);
        Assert.Equal(userAction, error.UserAction);
        Assert.DoesNotContain("secret", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(prompt is null ? 0 : 1, prompt?.FileQuestions.Count ?? 0);
        await AssertRefusedAsync();
    }

    [Theory]
    [InlineData(@"\\server\share\a.txt")]
    [InlineData(@"\\?\C:\a.txt")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"folder\a.txt")]
    [InlineData(@"C:a.txt")]
    [InlineData("https://example.invalid/a.txt")]
    public async Task ANetworkDeviceOrRelativePath_IsInvalidArgument_WithNoDialog(string path)
    {
        var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted));

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => UseCase().ExecuteAsync(new ToolCallContext("c", Prompt: prompt), PageId, Sent(FileOutline(path)), null, null, Ct));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
        Assert.Equal(UpdatePageContentWrite.PathUserAction, ex.UserAction);
        Assert.DoesNotContain(path, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(prompt.Order);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task AnAttachmentResentAsAReadReturnedIt_NeedsNoDialog_ThoughOneNoteRenamedItsTempCopy()
    {
        // Live sitting B3 (2026-11-12): every read names a new temp copy, and OneNote's InsertedFile has no objectID.
        var source = temp.Combine("kept.txt");
        var issued = new IssuedPathCaches();
        gateway.Pages[PageId] = WithAttachment(Read, source, temp.Combine("{1}.bin"));
        var read = await new GetPageContentUseCase(gateway, Services(gateway).ReadAccess, issued).ExecuteAsync(new ToolCallContext("r"), PageId, false, Ct);
        gateway.Pages[PageId] = WithAttachment(Read, source, temp.Combine("{2}.bin"));
        var prompt = new FakePrompt();

        await UseCase(issued: issued).ExecuteAsync(new ToolCallContext("c", Prompt: prompt), PageId, Sent(OutlineOf(read.Xml!, 40)), null, null, Ct);

        Assert.Empty(prompt.Order);
        Assert.Single(gateway.UpdateCalls);
    }

    [Fact]
    public async Task AnAttachmentWithATempNameNoReadReturned_StillAsks()
    {
        var source = temp.Combine("kept.txt");
        gateway.Pages[PageId] = WithAttachment(Read, source, temp.Combine("{2}.bin"));
        var prompt = new FakePrompt();

        await Assert.ThrowsAsync<FilePathsUnconfirmed>(() => UseCase(issued: new IssuedPathCaches()).ExecuteAsync(
            new ToolCallContext("c", Prompt: prompt), PageId, Sent(OutlineOf(WithAttachment(Read, source, temp.Combine("{1}.bin")), 40)), null, null, Ct));

        Assert.Single(prompt.FileQuestions);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task APathUnchangedOnTheSameExistingElement_NeedsNoDialog()
    {
        var path = temp.Combine("kept.txt");
        gateway.Pages[PageId] = WithFile(Read, path);
        var prompt = new FakePrompt();

        await UseCase().ExecuteAsync(new ToolCallContext("c", Prompt: prompt), PageId, Sent(OutlineOf(WithFile(Read, path), 40)), null, null, Ct);

        Assert.Empty(prompt.Order);
        Assert.Single(gateway.UpdateCalls);
    }

    [Fact]
    public async Task AFilePathInAProtectedSectionWithoutAReadGrant_IsAlwaysNew()
    {
        var path = temp.Combine("vault.txt");
        var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted));

        await UseCase().ExecuteAsync(new ToolCallContext("c", Prompt: prompt), VaultPageId, Sent(FileOutline(path), VaultPageId), null, null, Ct);

        Assert.Single(prompt.FileQuestions);
        // Step 1 never reads the page without a grant: the only reads are step 3's piBasic, the snapshot and the read-back.
        Assert.Equal([(VaultPageId, PageInfo.Basic), (VaultPageId, PageInfo.BinaryData), (VaultPageId, PageInfo.Basic)], gateway.PageCalls);
        Assert.Single(gateway.UpdateCalls);
    }

    // --- The HTML stopgap (Phill, 2026-11-12, until the html-local-ref probe runs) ---------------------------------------

    public static TheoryData<string> HtmlBlocks =>
    [
        "<one:HTMLBlock><one:File path=\"C:\\x\\block.html\" /></one:HTMLBlock>",
        "<one:HTMLBlock><one:Data><![CDATA[<p>hello</p>]]></one:Data></one:HTMLBlock>",
    ];

    [Theory]
    [MemberData(nameof(HtmlBlocks))]
    public async Task AnHtmlBlock_IsInvalidArgument_BeforeAnyPathDialog_InAProtectedSectionToo(string block)
    {
        foreach (var page in new[] { PageId, VaultPageId })
        {
            var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted));
            var sent = Sent($"<one:Outline><one:OEChildren>{block}</one:OEChildren></one:Outline>", page);

            var ex = await Assert.ThrowsAsync<OneNoteException>(() => UseCase().ExecuteAsync(new ToolCallContext("c", Prompt: prompt), page, sent, null, null, Ct));

            Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
            Assert.Equal(UpdatePageContentWrite.HtmlBlockUserAction, ex.UserAction);
            Assert.DoesNotContain("hello", ex.Message, StringComparison.Ordinal);
            Assert.Empty(prompt.Order);
            await AssertRefusedAsync();
        }
    }

    public static TheoryData<string> RefusedHtml =>
    [
        "<img src=\"file:///C:/x/a.png\">",
        "<a href=\"C:\\x\\notes.txt\">notes</a>",
        "<a href='\\\\server\\share\\a.txt'>s</a>",
        "<img src=//host/a.png>",
        "<span>see &#102;ile:///C:/x</span>",
        "<a href=\"notes.txt\">notes</a>",
        "<a href=\"jav&#x09ascript:x\">x</a>",
        "<img src=\"https://example.invalid/a.png\">",
        "<span style=\"background:url(C:\\x\\a.png)\">x</span>",
    ];

    [Theory]
    [MemberData(nameof(RefusedHtml))]
    public async Task ATextWithRefusedHtml_IsInvalidArgument_WithThePathDialogRouteOrInlineData(string html)
    {
        var sent = Sent($"<one:Outline><one:OEChildren><one:OE><one:T><![CDATA[{html}]]></one:T></one:OE></one:OEChildren></one:Outline>");

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => UseCase().ExecuteAsync(new ToolCallContext("c"), PageId, sent, null, null, Ct));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
        Assert.Equal(UpdatePageContentWrite.RefusedHtmlUserAction, ex.UserAction);
        Assert.DoesNotContain("C:", ex.Message.Replace(PageId, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task AnExistingTextWithALocalLink_SentBackUnchanged_IsAllowed_SoOtherEditsStillWork()
    {
        // OE 41 already links to a local file; the caller edits OE 42 in the same Outline and resends 41 unchanged.
        var withLink = After(p => p.Descendants().Single(e => (string?)e.Attribute("objectID") == G(41)).Element(One + "T")!
            .ReplaceNodes(new XCData("<a href=\"file:///C:/x/notes.txt\">notes</a>")));
        gateway.Pages[PageId] = withLink;
        var sent = Sent(OutlineOf(withLink, 40));
        var edited = XElement.Parse(sent);
        edited.Descendants().Single(e => (string?)e.Attribute("objectID") == G(42)).Element(One + "T")!.Value = "edited";

        await UseCase().ExecuteAsync(new ToolCallContext("c"), PageId, edited.ToString(), null, null, Ct);

        Assert.Single(gateway.UpdateCalls);

        // The same link, newly added on another OE, is refused.
        var added = XElement.Parse(sent);
        added.Descendants().Single(e => (string?)e.Attribute("objectID") == G(42)).Element(One + "T")!.ReplaceNodes(new XCData("<a href=\"file:///C:/x/notes.txt\">n</a>"));
        var ex = await Assert.ThrowsAsync<OneNoteException>(() => UseCase().ExecuteAsync(new ToolCallContext("c"), PageId, added.ToString(), null, null, Ct));
        Assert.Equal(UpdatePageContentWrite.RefusedHtmlUserAction, ex.UserAction);
    }

    [Fact]
    public async Task APathThatBecameNewWhileTheUserDecided_IsRetryable_NotNoDialog()
    {
        var kept = temp.Combine("kept.txt");
        var reads = 0;
        gateway.Pages[PageId] = WithFile(Read, kept);
        var probe = new ProbingGateway(gateway, () =>
        {
            // The second read is step 1's rerun after the dialog: by then OneNote holds another path on that element.
            if (++reads == 2)
            {
                gateway.Pages[PageId] = WithFile(Read, temp.Combine("other.txt"));
            }

            return Task.CompletedTask;
        });
        var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted));
        var sent = Sent(OutlineOf(WithFile(Read, kept), 40) + FileOutline(temp.Combine("a.txt")));

        var ex = await Assert.ThrowsAsync<FilePathsUnconfirmed>(() => UseCase(probe).ExecuteAsync(new ToolCallContext("c", Prompt: prompt), PageId, sent, null, null, Ct));

        Assert.True(ex.Retryable);
        Assert.Equal(FilePathsUnconfirmed.ChangedUserAction, ex.UserAction);
        Assert.Single(prompt.FileQuestions);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task APathThatChangedBetweenStep1AndStep3_IsWriteNotGranted_InStep4()
    {
        var path = temp.Combine("kept.txt");
        var reads = 0;
        gateway.Pages[PageId] = WithFile(Read, path);
        var probe = new ProbingGateway(gateway, () =>
        {
            // The second read is step 3's: by then OneNote holds another path on that element.
            if (++reads == 2)
            {
                gateway.Pages[PageId] = WithFile(Read, temp.Combine("other.txt"));
            }

            return Task.CompletedTask;
        });
        var prompt = new FakePrompt();

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => UseCase(probe).ExecuteAsync(new ToolCallContext("c", Prompt: prompt), PageId, Sent(OutlineOf(WithFile(Read, path), 40)), null, null, Ct));

        Assert.Equal(ErrorCode.WriteNotGranted, ex.Code);
        Assert.True(ex.Retryable);
        Assert.Empty(prompt.Order);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task Step1Order_AReadNotGrantedOrMissingWriteGrant_FailsBeforeAnyPathDialog()
    {
        File.Delete(Store.Path);
        var protectedPrompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted));
        var vault = Sent(OutlineAsRead(40) + FileOutline(temp.Combine("a.txt")), VaultPageId);

        var readNotGranted = await Assert.ThrowsAsync<OneNoteException>(() => UseCase(withDialog: true).ExecuteAsync(new ToolCallContext("c", Prompt: protectedPrompt), VaultPageId, vault, null, null, Ct));

        Assert.Equal(ErrorCode.ReadNotGranted, readNotGranted.Code);
        Assert.Empty(protectedPrompt.Order);

        var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Declined), new GrantPromptResult(GrantPromptStatus.Accepted));
        var notGranted = await Assert.ThrowsAsync<OneNoteException>(() => UseCase(withDialog: true).ExecuteAsync(new ToolCallContext("c", Prompt: prompt), PageId, Sent(FileOutline(temp.Combine("a.txt"))), null, null, Ct));

        Assert.Equal(ErrorCode.WriteNotGranted, notGranted.Code);
        Assert.Equal(["write"], prompt.Order);
        await AssertRefusedAsync();
    }

    [Fact]
    public void TheFilePathDialog_NamesEachPath_AndIsAcceptOrDecline()
    {
        var request = OneNoteMcp.Server.Grants.ElicitationGrantPrompt.FilePathRequest(new FilePathQuestion(PageId, [@"C:\a\one.txt", @"C:\b\two.png"]));

        Assert.Contains(@"C:\a\one.txt", request.Message, StringComparison.Ordinal);
        Assert.Contains(@"C:\b\two.png", request.Message, StringComparison.Ordinal);
        Assert.Empty(request.RequestedSchema!.Properties);
        Assert.Equal(GrantPromptStatus.Accepted, OneNoteMcp.Server.Grants.ElicitationGrantPrompt.ReadFilePaths(new ElicitResult { Action = "accept" }).Status);
        Assert.Equal(GrantPromptStatus.Declined, OneNoteMcp.Server.Grants.ElicitationGrantPrompt.ReadFilePaths(new ElicitResult { Action = "decline" }).Status);
        Assert.Equal(GrantPromptStatus.Cancelled, OneNoteMcp.Server.Grants.ElicitationGrantPrompt.ReadFilePaths(new ElicitResult { Action = "cancel" }).Status);
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
        var entry = JournalStore.Read(Assert.Single(Directory.GetFiles(Folder)));

        // 2.11 Q1: a failure after the snapshot names the entry; the error's meta stays {correlationId} (AD-9).
        Assert.Contains(entry.EntryId.ToString("D"), error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AClientCancelDuringTheComCall_IsOutcomeUnknown_AndNamesTheJournalEntry()
    {
        // 2.11: the pipeline's own step-7 cancel message carries the operation's note (Q1's entry ID).
        using var cts = new CancellationTokenSource();
        gateway.OnUpdateCall = async ct =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        };

        var error = Error(await Tool().UpdatePageContent(PageId, Sent(EditedOutlineB()), BasisArgument(), cancellationToken: cts.Token), "Cancelled");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        var entry = JournalStore.Read(Assert.Single(Directory.GetFiles(Folder)));
        Assert.Contains(entry.EntryId.ToString("D"), error.GetProperty("message").GetString(), StringComparison.Ordinal);
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
        // New content only: the locked section is protected with no read grant, so an objectID would be ReadNotGranted first.
        Error(await Tool().UpdatePageContent(LockedPageId, Sent(NewOutline(string.Empty), LockedPageId), cancellationToken: Ct), "SectionLocked");

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

    private PageWriteServices Services(IOneNoteGateway g, IssuedPathCaches? issued = null) => new(
        g,
        new WriteAccess(g, Store.Read),
        new ReadAccess(g, Store.Read, new SettingsWriter(Store, clock), clock),
        new PageSnapshot(g, new JournalStore(Store.Read), clock),
        issued);

    private UpdatePageContentUseCase UseCase(IOneNoteGateway? over = null, bool withDialog = false, IssuedPathCaches? issued = null)
    {
        var g = over ?? gateway;
        var dialog = withDialog ? new WriteGrantDialog(g, new SettingsWriter(Store, clock), clock) : null;
        return new UpdatePageContentUseCase(Services(g, issued), new WritePipeline(writeLock, g, cache, Store.Read, TimeProvider.System, dialog: dialog));
    }

    /// <summary>
    /// <paramref name="page"/> with a new OE <c>96</c> in Outline <c>40</c> holding an attachment as OneNote writes it: an
    /// InsertedFile with no objectID, its source at <paramref name="source"/> and its temp copy at <paramref name="cache"/>.
    /// </summary>
    private static string WithAttachment(string page, string source, string cache)
    {
        var root = XDocument.Parse(page);
        var outline = root.Descendants(One + "Outline").Single(o => (string?)o.Attribute("objectID") == G(40));
        outline.Element(One + "OEChildren")!.Add(new XElement(One + "OE", new XAttribute("objectID", G(96)),
            new XElement(One + "InsertedFile", new XAttribute("pathCache", cache), new XAttribute("pathSource", source), new XAttribute("preferredName", "kept.txt"))));
        return root.ToString();
    }

    /// <summary>A new Outline (no objectIDs) holding one OE with <paramref name="oeAttributes"/>.</summary>
    private static string NewOutline(string oeAttributes) =>
        $"<one:Outline><one:OEChildren><one:OE {oeAttributes}><one:T>new</one:T></one:OE></one:OEChildren></one:Outline>";

    /// <summary>A new Outline whose OE inserts the file at <paramref name="path"/>.</summary>
    private static string FileOutline(string path) =>
        $"<one:Outline><one:OEChildren><one:OE><one:InsertedFile pathSource=\"{System.Security.SecurityElement.Escape(path)}\" preferredName=\"a.txt\" /></one:OE></one:OEChildren></one:Outline>";

    /// <summary><paramref name="page"/> with a new OE <c>96</c> in Outline <c>40</c> holding InsertedFile <c>95</c> at <paramref name="path"/>.</summary>
    private static string WithFile(string page, string path)
    {
        var root = XDocument.Parse(page);
        var outline = root.Descendants(One + "Outline").Single(o => (string?)o.Attribute("objectID") == G(40));
        outline.Element(One + "OEChildren")!.Add(new XElement(One + "OE", new XAttribute("objectID", G(96)),
            new XElement(One + "InsertedFile", new XAttribute("objectID", G(95)), new XAttribute("pathSource", path), new XAttribute("preferredName", "a.txt"))));
        return root.ToString();
    }

    /// <summary>Outline <paramref name="n"/> of <paramref name="page"/>, as sent text.</summary>
    private static string OutlineOf(string page, int n) =>
        Text(new XElement(XDocument.Parse(page).Descendants().Single(e => (string?)e.Attribute("objectID") == G(n))));

    /// <summary>After a successful write to the vault page, it reads back as the read page changed by <paramref name="edit"/>.</summary>
    private void VaultAfterWrite(Action<XElement> edit)
    {
        var root = XDocument.Parse(gateway.Pages[VaultPageId]);
        edit(root.Root!);
        gateway.OnUpdate = _ => gateway.Pages[VaultPageId] = root.ToString();
    }

    private void GrantVaultRead() =>
        new SettingsWriter(Store, clock).SaveReadGrant(new ProtectedSectionGrant(
            GrantKeys.SectionKey(@"C:\notebooks\W\Vault.one"), "Vault", GrantKeys.NotebookKey(NotebookPath), ProtectedId, clock.GetUtcNow(), GrantedVia.Cli));

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

        public Task<string> CreateSectionAsync(string parentId, string sectionFileName, CancellationToken ct) => inner.CreateSectionAsync(parentId, sectionFileName, ct);

        public Task<string> CreateSectionGroupAsync(string parentId, string groupName, CancellationToken ct) => inner.CreateSectionGroupAsync(parentId, groupName, ct);

        public Task<string> CreateNotebookAsync(string folderPath, CancellationToken ct) => inner.CreateNotebookAsync(folderPath, ct);

        public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct) => inner.UpdatePageContentAsync(pageXml, expectedLastModified, ct);

        public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct) => inner.DeletePageContentAsync(pageId, objectId, expectedLastModified, ct);


        public Task UpdateHierarchyAsync(string changesXml, CancellationToken ct) => inner.UpdateHierarchyAsync(changesXml, ct);

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => inner.WaitForAbandonedWritesAsync(ct);
    }
}
