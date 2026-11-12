using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 2.8: AD-7 step 6, the page snapshot (AD-8), alone and through the write pipeline, against a scripted gateway
/// over captured page fixtures: notebook <c>A</c> with a section in a group and a password-protected section. Each test
/// has a write lock of its own and a temp journal folder.
/// </summary>
public sealed class PageSnapshotTests : IDisposable
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";
    private const string NotebookId = "{NBA00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string NotebookPath = @"C:\notebooks\A\";
    private const string GroupId = "{GA000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string SectionId = "{SA000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string SectionPath = @"C:\notebooks\A\Group\Section.one";
    private const string VaultId = "{SV000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string VaultPath = @"C:\notebooks\A\Vault.one";
    private const string PageId = "{SA000000-0000-4000-8000-000000000001}{1}{E1}";
    private const string VaultPageId = "{SV000000-0000-4000-8000-000000000001}{1}{E1}";

    private readonly TempDirectory temp = new();
    private readonly SnapshotGateway gateway = new();
    private readonly WriteLock writeLock = new($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}");

    public PageSnapshotTests()
    {
        gateway.Parents[PageId] = SectionId;
        gateway.Parents[SectionId] = GroupId;
        gateway.Parents[GroupId] = NotebookId;
        gateway.Parents[NotebookId] = string.Empty;
        gateway.Parents[VaultPageId] = VaultId;
        gateway.Parents[VaultId] = NotebookId;
        gateway.Selves[NotebookId] = $"<one:Notebook {Ns} name=\"A folder\" nickname=\"A\" ID=\"{NotebookId}\" path=\"{NotebookPath}\" />";
        gateway.Selves[GroupId] = $"<one:SectionGroup {Ns} name=\"Group\" ID=\"{GroupId}\" path=\"C:\\notebooks\\A\\Group\" />";
        gateway.Selves[SectionId] = $"<one:Section {Ns} name=\"Section\" ID=\"{SectionId}\" path=\"{SectionPath}\" />";
        gateway.Selves[VaultId] = $"<one:Section {Ns} name=\"Vault\" ID=\"{VaultId}\" path=\"{VaultPath}\" encrypted=\"true\" />";
        Page(PageId, "mcp-test/page/image-Basic.xml", "mcp-test/page/image-BinaryData.xml");
        Page(VaultPageId, "mcp-test/page/image-Basic.xml", "mcp-test/page/image-BinaryData.xml");
    }

    public void Dispose() => temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Folder => temp.Combine("journal");

    private SettingsStore Store => new(temp.Combine("settings.json"), Folder);

    // --- The snapshot ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task LocalPage_OneEntry_BinaryComplete_PlacedByNotebookKeyAndNamePath()
    {
        var written = await Snapshot().TakeAsync(Request(PageId), Ct);

        Assert.Equal(written.Path, Assert.Single(Directory.GetFiles(Folder)));
        var entry = JournalStore.Read(written.Path);
        Assert.Equal(JournalEntry.CurrentSchemaVersion, entry.SchemaVersion);
        Assert.Equal(written.EntryId, entry.EntryId);
        Assert.Equal("corr-1", entry.CorrelationId);
        Assert.Equal("update_page_content", entry.Operation);
        Assert.Equal("PageWrite", entry.OperationClass);
        Assert.Equal("page", entry.SnapshotKind);
        Assert.Equal(PageId, entry.ObjectId);
        Assert.Equal(NotebookId, entry.NotebookId);
        Assert.Equal(GrantKeys.NotebookKey(NotebookPath), entry.NotebookPath);
        Assert.Equal("A", entry.NotebookName);
        Assert.Equal(SectionId, entry.ParentId);
        Assert.Equal("Group/Section", entry.ParentNamePath);
        Assert.Null(entry.SectionKey);
        Assert.Equal("Fixture Image", entry.Title);
        Assert.True(entry.BinaryComplete);
        Assert.Empty(entry.MissingBinary);
        Assert.Equal(FakeGateway.ReadFixture("mcp-test/page/image-BinaryData.xml"), entry.Xml);
    }

    [Fact]
    public async Task CloudPage_ImagesPiBinaryDataLacks_AreListed_BinaryIncomplete()
    {
        Page(PageId, "cloud/page-Basic.xml", "cloud/page-BinaryData.xml");

        var entry = JournalStore.Read((await Snapshot().TakeAsync(Request(PageId), Ct)).Path);

        Assert.False(entry.BinaryComplete);
        Assert.Equal(
            [
                new MissingBinary("image", "{C10D0000-0000-4000-8000-000000000001}{106}{B2}", "{C10D0000-0000-4000-8000-000000000001}{107}{B2}"),
                new MissingBinary("image", "{C10D0000-0000-4000-8000-000000000001}{110}{B2}", "{C10D0000-0000-4000-8000-000000000001}{111}{B2}"),
            ],
            entry.MissingBinary);
    }

    [Fact]
    public async Task OneMissingImage_OneItem()
    {
        var basic = FakeGateway.ReadFixture("mcp-test/page/image-Basic.xml");
        var withoutImage = System.Text.RegularExpressions.Regex.Replace(basic, "<one:Image>.*?</one:Image>", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
        gateway.Pages[(PageId, PageInfo.Basic)] = basic;
        gateway.Pages[(PageId, PageInfo.BinaryData)] = withoutImage;

        var entry = JournalStore.Read((await Snapshot().TakeAsync(Request(PageId), Ct)).Path);

        Assert.False(entry.BinaryComplete);
        var item = Assert.Single(entry.MissingBinary);
        Assert.Equal(new MissingBinary("image", "{B870D211-3A03-48F1-BA23-6F34D7A02B73}{39}{B0}", "{B870D211-3A03-48F1-BA23-6F34D7A02B73}{40}{B0}"), item);
    }

    [Fact]
    public async Task Attachment_IsListed_BinaryIncomplete_DecisionQ4()
    {
        var xml = $"<?xml version=\"1.0\"?><one:Page {Ns} ID=\"{PageId}\" name=\"Files\"><one:Outline objectID=\"{{O}}{{1}}{{B0}}\"><one:OEChildren>"
            + "<one:OE objectID=\"{O}{2}{B0}\"><one:InsertedFile pathSource=\"C:\\x.pdf\" preferredName=\"x.pdf\"><one:CallbackID callbackID=\"{O}{3}{B0}\" /></one:InsertedFile></one:OE>"
            + "</one:OEChildren></one:Outline></one:Page>";
        gateway.Pages[(PageId, PageInfo.Basic)] = xml;
        gateway.Pages[(PageId, PageInfo.BinaryData)] = xml;

        var entry = JournalStore.Read((await Snapshot().TakeAsync(Request(PageId), Ct)).Path);

        Assert.False(entry.BinaryComplete);
        Assert.Equal(new MissingBinary("attachment", "{O}{2}{B0}", "{O}{3}{B0}"), Assert.Single(entry.MissingBinary));
    }

    [Fact]
    public async Task ProtectedSection_TitleAndXmlEncrypted_SectionKeySet_RestReadable()
    {
        var written = await Snapshot().TakeAsync(Request(VaultPageId), Ct);

        Assert.True(written.Protected);
        var text = Decompressed(written.Path);
        Assert.DoesNotContain("Fixture Image", text, StringComparison.Ordinal);
        Assert.DoesNotContain("A local PNG follows.", text, StringComparison.Ordinal);
        var entry = JournalStore.Read(written.Path);
        Assert.Equal(GrantKeys.SectionKey(VaultPath), entry.SectionKey);
        Assert.Equal("Vault", entry.ParentNamePath);
        Assert.Equal("Fixture Image", entry.Title);
        Assert.Equal(FakeGateway.ReadFixture("mcp-test/page/image-BinaryData.xml"), entry.Xml);
    }

    [Fact]
    public async Task ProtectionIsReadLiveAtTheSnapshot()
    {
        gateway.Selves[SectionId] = $"<one:Section {Ns} name=\"Section\" ID=\"{SectionId}\" path=\"{SectionPath}\" encrypted=\"1\" />";

        var written = await Snapshot().TakeAsync(Request(PageId), Ct);

        Assert.True(written.Protected);
        Assert.Equal(GrantKeys.SectionKey(SectionPath), JournalStore.Read(written.Path).SectionKey);
    }

    [Fact]
    public async Task UnreadablePageXml_IsJournalFailed_NoEntry()
    {
        gateway.Pages[(PageId, PageInfo.BinaryData)] = "<one:Page";

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => Snapshot().TakeAsync(Request(PageId), Ct));

        Assert.Equal(ErrorCode.JournalFailed, ex.Code);
        Assert.False(Directory.Exists(Folder) && Directory.EnumerateFileSystemEntries(Folder).Any());
    }

    [Fact]
    public async Task PageWithNoNotebookAbove_IsJournalFailed_BeforeAnyBinaryRead()
    {
        gateway.Parents[GroupId] = string.Empty;

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => Snapshot().TakeAsync(Request(PageId), Ct));

        Assert.Equal(ErrorCode.JournalFailed, ex.Code);
        Assert.Contains(PageId, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(gateway.PageReads, r => r.Info == PageInfo.BinaryData);
    }

    // --- Through the pipeline ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Pipeline_Snapshot_IsWrittenBeforeTheComCall()
    {
        var operation = new SnapshottingWrite(gateway, Snapshot(), PageId, onCall: () => Assert.Single(Directory.GetFiles(Folder)));

        await Pipeline().RunAsync(new ToolCallContext("corr-1"), operation, Ct);

        Assert.Single(gateway.UpdateCalls);
        Assert.Equal("corr-1", JournalStore.Read(Assert.Single(Directory.GetFiles(Folder))).CorrelationId);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Pipeline_JournalUnwritable_IsJournalFailed_NoComCall_LockReleased()
    {
        File.WriteAllText(Folder, "a file where the journal folder should be");

        var ex = await Assert.ThrowsAsync<OneNoteException>(() =>
            Pipeline().RunAsync(new ToolCallContext("c"), new SnapshottingWrite(gateway, Snapshot(), PageId), Ct));

        Assert.Equal(ErrorCode.JournalFailed, ex.Code);
        Assert.False(ex.OutcomeUnknown);
        Assert.False(ToolErrors.From(ex, OperationClass.PageWrite).Retryable);
        Assert.Empty(gateway.UpdateCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Pipeline_SnapshotThrowsAnythingElse_IsJournalFailed_NoComCall_LockFreeAtOnce()
    {
        var ex = await Assert.ThrowsAsync<OneNoteException>(() =>
            Pipeline().RunAsync(new ToolCallContext("c"), new FailingSnapshotWrite(gateway, new IOException("disk full")), Ct));

        Assert.Equal(ErrorCode.JournalFailed, ex.Code);
        Assert.False(ex.OutcomeUnknown);
        Assert.DoesNotContain("disk full", ex.Message, StringComparison.Ordinal);
        Assert.Empty(gateway.UpdateCalls);

        // A second write takes the lock at once.
        using var second = await writeLock.AcquireAsync("second", TimeSpan.Zero, Ct);
    }

    [Fact]
    public async Task Pipeline_SnapshotComReadBusy_KeepsComBusy_Retryable_NotJournalFailed_DecisionQ3()
    {
        gateway.BinaryReadFailure = new OneNoteException(ErrorCode.ComBusy, "GetPageContent was rejected: OneNote is busy.");

        var ex = await Assert.ThrowsAsync<OneNoteException>(() =>
            Pipeline().RunAsync(new ToolCallContext("c"), new SnapshottingWrite(gateway, Snapshot(), PageId), Ct));

        Assert.Equal(ErrorCode.ComBusy, ex.Code);
        Assert.False(ex.OutcomeUnknown);
        Assert.True(ToolErrors.From(ex, OperationClass.PageWrite).Retryable);
        Assert.Empty(gateway.UpdateCalls);
        Assert.False(Directory.Exists(Folder));
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Pipeline_SnapshotCancelled_StaysACancellation_NoComCall()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var operation = new FailingSnapshotWrite(gateway, null, onSnapshot: cts.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pipeline().RunAsync(new ToolCallContext("c"), operation, cts.Token));

        Assert.Empty(gateway.UpdateCalls);
        await AssertLockFreeAsync();
    }

    // --- Helpers -----------------------------------------------------------------------------------------------------

    private PageSnapshot Snapshot() => new(gateway, new JournalStore(Store.Read));

    private WritePipeline Pipeline() => new(writeLock, gateway, cache: null, Store.Read, TimeProvider.System);

    private PageSnapshotRequest Request(string pageId) =>
        new("corr-1", "update_page_content", OperationClass.PageWrite, pageId, gateway.Pages[(pageId, PageInfo.Basic)]);

    private void Page(string pageId, string basicFixture, string binaryFixture)
    {
        gateway.Pages[(pageId, PageInfo.Basic)] = FakeGateway.ReadFixture(basicFixture);
        gateway.Pages[(pageId, PageInfo.BinaryData)] = FakeGateway.ReadFixture(binaryFixture);
    }

    private async Task AssertLockFreeAsync()
    {
        using var probe = await writeLock.AcquireAsync("free?", TimeSpan.FromMilliseconds(500), Ct);
    }

    private static string Decompressed(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    /// <summary>A page write in the shape 2.9 will take: step 3 reads piBasic, step 6 snapshots, step 7 updates.</summary>
    private sealed class SnapshottingWrite(SnapshotGateway gateway, PageSnapshot snapshot, string pageId, Action? onCall = null) : WriteOperation<bool>
    {
        private string? basic;

        public override OperationClass Class => OperationClass.PageWrite;

        public override IReadOnlyCollection<string> WrittenPageIds => [pageId];

        public override Task AuthorizeAsync(CancellationToken ct) => Task.CompletedTask;

        public override async Task ReadAsync(CancellationToken ct) =>
            basic = await gateway.GetPageContentAsync(pageId, PageInfo.Basic, ct);

        public override void Validate()
        {
        }

        public override Task SnapshotAsync(CancellationToken ct) =>
            snapshot.TakeAsync(new PageSnapshotRequest("corr-1", "update_page_content", OperationClass.PageWrite, pageId, basic!), ct);

        public override async Task<bool> CallAsync(CancellationToken ct)
        {
            onCall?.Invoke();
            await gateway.UpdatePageContentAsync(basic!, "2026-11-05T04:01:00.000Z", ct);
            return true;
        }
    }

    /// <summary>A page write whose step-6 snapshot throws <c>failure</c> (or, when null, runs <c>onSnapshot</c> and checks the token).</summary>
    private sealed class FailingSnapshotWrite(SnapshotGateway gateway, Exception? failure, Action? onSnapshot = null) : WriteOperation<bool>
    {
        public override OperationClass Class => OperationClass.PageWrite;

        public override Task AuthorizeAsync(CancellationToken ct) => Task.CompletedTask;

        public override Task ReadAsync(CancellationToken ct) => Task.CompletedTask;

        public override void Validate()
        {
        }

        public override Task SnapshotAsync(CancellationToken ct)
        {
            onSnapshot?.Invoke();
            ct.ThrowIfCancellationRequested();
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }

        public override async Task<bool> CallAsync(CancellationToken ct)
        {
            await gateway.UpdatePageContentAsync("<x/>", "2026-11-05T04:01:00.000Z", ct);
            return true;
        }
    }

    /// <summary>Serves set hierarchy and page XML by ID; records page writes. Anything unscripted is ObjectNotFound.</summary>
    private sealed class SnapshotGateway : IOneNoteGateway
    {
        public Dictionary<string, string> Parents { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Selves { get; } = new(StringComparer.Ordinal);

        public Dictionary<(string Id, PageInfo Info), string> Pages { get; } = [];

        public OneNoteException? BinaryReadFailure { get; set; }

        public List<string> UpdateCalls { get; } = [];

        public Task<string> GetHierarchyAsync(string startNodeId, HierarchyScope scope, CancellationToken ct) =>
            scope == HierarchyScope.Self && Selves.TryGetValue(startNodeId, out var xml)
                ? Task.FromResult("<?xml version=\"1.0\"?>\n" + xml)
                : NotFound<string>();

        public Task<string> GetHierarchyParentAsync(string objectId, CancellationToken ct) =>
            Parents.TryGetValue(objectId, out var parent) ? Task.FromResult(parent) : NotFound<string>();

        public List<(string PageId, PageInfo Info)> PageReads { get; } = [];

        public Task<string> GetPageContentAsync(string pageId, PageInfo pageInfo, CancellationToken ct)
        {
            PageReads.Add((pageId, pageInfo));
            if (pageInfo == PageInfo.BinaryData && BinaryReadFailure is { } failure)
            {
                return Task.FromException<string>(failure);
            }

            return Pages.TryGetValue((pageId, pageInfo), out var xml) ? Task.FromResult(xml) : NotFound<string>();
        }

        public Task UpdatePageContentAsync(string pageXml, string expectedLastModified, CancellationToken ct)
        {
            UpdateCalls.Add(pageXml);
            return Task.CompletedTask;
        }

        public Task WaitForAbandonedWritesAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetBinaryPageContentAsync(string pageId, string callbackId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> FindPagesAsync(string startNodeId, string query, bool includeUnindexed, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> FindMetaAsync(string startNodeId, string name, bool includeUnindexed, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetWebHyperlinkToObjectAsync(string hierarchyId, string pageContentObjectId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSpecialLocationAsync(SpecialLocation location, CancellationToken ct) => throw new NotSupportedException();

        public Task NavigateToAsync(string hierarchyId, string objectId, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> CreateNewPageAsync(string sectionId, NewPageStyle style, CancellationToken ct) => throw new NotSupportedException();

        public Task DeletePageContentAsync(string pageId, string objectId, string expectedLastModified, CancellationToken ct) => throw new NotSupportedException();

        private static Task<T> NotFound<T>() =>
            Task.FromException<T>(new OneNoteException(ErrorCode.ObjectNotFound, "hrObjectDoesNotExist (0x80042014).", unchecked((int)0x80042014)));
    }
}
