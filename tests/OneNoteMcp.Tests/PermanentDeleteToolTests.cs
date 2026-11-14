using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Journal;
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
/// Story 5.6: <c>permanently_delete_hierarchy_object</c> through the AD-7 write pipeline, one test per row of the plan's
/// I/O matrix and each acceptance criterion, against <see cref="FakeGateway"/>: notebook <c>W</c> with a live section
/// (two pages), a section holding a page with a subpage, a protected section, a locked one, the recycle bin with Deleted
/// Pages (one recycled page) and a recycled section, an empty and a non-empty section group; a temporary
/// <c>settings.json</c> and journal folder. Each test has a write lock of its own. Refusals leave no COM delete, no
/// <c>Publish</c> and no journal entry.
/// </summary>
public sealed class PermanentDeleteToolTests : IDisposable
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";
    private const string NotebookId = "{NBW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string NotebookPath = @"C:\notebooks\W\";
    private const string SectionId = "{SW000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string TreeId = "{ST000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string ProtectedId = "{SP000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string LockedId = "{SL000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string RecycleBinId = "{RBW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string DeletedPagesId = "{SDW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string RecycledSectionId = "{SRW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string EmptyGroupId = "{GE000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string FullGroupId = "{GF000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string InnerSectionId = "{SI000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string SecondPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000012}";
    private const string ParentPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000021}";
    private const string ChildPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000022}";
    private const string VaultPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000002}";
    private const string LockedPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000003}";
    private const string BinnedPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000004}";
    private const string RecycledVaultId = "{SV000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string RecycledSectionPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000005}";

    /// <summary>The page's hierarchy date, a second after its page-XML date: AD-17 sends this one to DeleteHierarchy.</summary>
    private const string PageHierarchyDate = "2026-11-12T00:00:01.000Z";
    private const string SectionDate = "2026-11-12T00:10:00.000Z";
    private const string GroupDate = "2026-11-12T00:20:00.000Z";

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly FakeGateway gateway = new();
    private readonly ConcurrentQueue<WriteLockHeld> holds = new();
    private readonly WriteLock writeLock;
    private readonly PageTextCache cache = PageTextCache.WithDefaults();
    private readonly ReadAccess readAccess;

    public PermanentDeleteToolTests()
    {
        writeLock = new WriteLock($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", holds.Enqueue);
        readAccess = new ReadAccess(gateway, Store.Read, new SettingsWriter(Store, clock), clock);

        gateway.Parents[NotebookId] = string.Empty;
        foreach (var id in new[] { SectionId, TreeId, ProtectedId, LockedId, RecycleBinId, EmptyGroupId, FullGroupId })
        {
            gateway.Parents[id] = NotebookId;
        }

        gateway.Parents[DeletedPagesId] = RecycleBinId;
        gateway.Parents[RecycledSectionId] = RecycleBinId;
        gateway.Parents[RecycledVaultId] = RecycleBinId;
        gateway.Parents[InnerSectionId] = FullGroupId;
        gateway.Parents[PageId] = SectionId;
        gateway.Parents[SecondPageId] = SectionId;
        gateway.Parents[ParentPageId] = TreeId;
        gateway.Parents[ChildPageId] = TreeId;
        gateway.Parents[VaultPageId] = ProtectedId;
        gateway.Parents[LockedPageId] = LockedId;
        gateway.Parents[BinnedPageId] = DeletedPagesId;
        gateway.Parents[RecycledSectionPageId] = RecycledSectionId;

        Self(NotebookId, $"<one:Notebook {Ns} name=\"W\" nickname=\"W\" ID=\"{NotebookId}\" path=\"{NotebookPath}\" lastModifiedTime=\"{GroupDate}\" />");
        Section(SectionId, "Open", string.Empty, PageId, SecondPageId);
        Section(TreeId, "Tree", string.Empty, ParentPageId, ChildPageId);
        Section(ProtectedId, "Vault", "encrypted=\"true\"", VaultPageId);
        Section(LockedId, "Locked vault", "encrypted=\"true\" locked=\"true\"");
        Section(DeletedPagesId, "Deleted Pages", "isInRecycleBin=\"true\" isDeletedPages=\"true\"", BinnedPageId);
        Section(RecycledSectionId, "Old", "isInRecycleBin=\"true\"", RecycledSectionPageId);
        Section(RecycledVaultId, "Old vault", "isInRecycleBin=\"true\" encrypted=\"true\"");
        Self(RecycleBinId, $"<one:SectionGroup {Ns} name=\"OneNote_RecycleBin\" ID=\"{RecycleBinId}\" path=\"C:\\notebooks\\W\\OneNote_RecycleBin\" isRecycleBin=\"true\" lastModifiedTime=\"{GroupDate}\" />");
        Group(EmptyGroupId, "Empty", string.Empty);
        Group(FullGroupId, "Full", $"<one:Section name=\"Inner\" ID=\"{InnerSectionId}\" path=\"C:\\notebooks\\W\\Full\\Inner.one\" />");

        foreach (var (id, extra) in new[]
        {
            (PageId, string.Empty), (SecondPageId, string.Empty), (ParentPageId, string.Empty), (ChildPageId, "pageLevel=\"2\""),
            (VaultPageId, string.Empty), (LockedPageId, string.Empty), (BinnedPageId, "isInRecycleBin=\"true\""), (RecycledSectionPageId, string.Empty),
        })
        {
            Self(id, $"<one:Page {Ns} ID=\"{id}\" name=\"Synthetic Update\" lastModifiedTime=\"{PageHierarchyDate}\" {extra} />");
            gateway.Pages[id] = Read.Replace(PageId, id, StringComparison.Ordinal);
        }

        Grant(permanentDelete: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Folder => temp.Combine("journal");

    private string Backups => Path.Combine(Folder, JournalFiles.BackupsFolderName);

    private SettingsStore Store => new(temp.Combine("settings.json"), Folder);

    public void Dispose() => temp.Dispose();

    // --- Pages ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Page_IsDeletedPermanently_WithItsHierarchyDate_AndOnePageEntry()
    {
        cache.Set(PageId, DateTimeOffset.UnixEpoch, "cached text", long.MaxValue);

        var root = Ok(await Tool().PermanentlyDeleteHierarchyObject(PageId, PageBasis(), Ct));

        // AD-17: the caller's page-XML date is compared; DeleteHierarchy gets the page's hierarchy date.
        Assert.Equal((PageId, PageHierarchyDate, true), Assert.Single(gateway.DeleteHierarchyCalls));
        Assert.Empty(gateway.PublishCalls);
        var meta = root.GetProperty("meta");
        Assert.Equal(PageId, meta.GetProperty("objectId").GetString());
        Assert.False(meta.TryGetProperty("backupFile", out _));
        Assert.False(meta.TryGetProperty("pageEntryIds", out _));
        var entry = JournalStore.Read(Assert.Single(Directory.GetFiles(Folder)));
        Assert.Equal(JournalEntry.PageKind, entry.SnapshotKind);
        Assert.Equal(PermanentDeleteWrite.OperationName, entry.Operation);
        Assert.Equal(nameof(OperationClass.PermanentDelete), entry.OperationClass);
        Assert.Equal(entry.EntryId.ToString("D"), meta.GetProperty("journalEntryId").GetString());
        Assert.Null(entry.SectionKey);
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
        Assert.False(cache.TryGet(PageId, DateTimeOffset.UnixEpoch, out _));
        Assert.Single(holds);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Page_WithNoBasis_IsDeleted_WithNoBasis()
    {
        var root = Ok(await Tool().PermanentlyDeleteHierarchyObject(PageId, null, Ct));

        Assert.Equal(["noBasis"], Codes(root));
        Assert.Equal((PageId, PageHierarchyDate, true), Assert.Single(gateway.DeleteHierarchyCalls));
    }

    [Fact]
    public async Task Page_WithADateOnlyBasis_IsDeleted_WithPartialBasis()
    {
        var root = Ok(await Tool().PermanentlyDeleteHierarchyObject(PageId, new PageBasisArgument("2026-11-12T00:00:00Z"), Ct));

        Assert.Equal(["partialBasis"], Codes(root));
        Assert.Single(gateway.DeleteHierarchyCalls);
    }

    [Fact]
    public async Task Page_InAProtectedSection_WithoutAReadGrant_DateOnlyBasis_IsDeleted_WithADpapiEntry()
    {
        var root = Ok(await Tool().PermanentlyDeleteHierarchyObject(VaultPageId, new PageBasisArgument("2026-11-12T00:00:00.000Z"), Ct));

        Assert.Equal((VaultPageId, PageHierarchyDate, true), Assert.Single(gateway.DeleteHierarchyCalls));
        var file = Assert.Single(Directory.GetFiles(Folder));
        Assert.DoesNotContain("Synthetic Update", Decompressed(file), StringComparison.Ordinal);
        var entry = JournalStore.Read(file);
        Assert.Equal(GrantKeys.SectionKey(@"C:\notebooks\W\Vault.one"), entry.SectionKey);
        Assert.DoesNotContain("Vault", root.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_InAProtectedSection_WithoutAReadGrant_AHashBasis_IsReadNotGranted_AtStep1_NeverAsking()
    {
        var prompt = new FakePrompt();

        var error = Error(await RunAsync(VaultPageId, PageBasis(VaultPageId).ToCore(), prompt), "ReadNotGranted");

        Assert.Contains("get_page_content", error.GetProperty("userAction").GetString(), StringComparison.Ordinal);
        Assert.Empty(prompt.Order);
        Assert.Empty(holds);
        AssertNothingWritten();
    }

    [Fact]
    public async Task Page_WithSubpages_IsInvalidArgument_WithTheCount()
    {
        var error = Error(await Tool().PermanentlyDeleteHierarchyObject(ParentPageId, null, Ct), "InvalidArgument");

        Assert.Contains("1 subpage(s)", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task Subpage_ItselfHasNoSubpages_AndIsDeleted()
    {
        Ok(await Tool().PermanentlyDeleteHierarchyObject(ChildPageId, null, Ct));

        Assert.Single(gateway.DeleteHierarchyCalls);
    }

    [Fact]
    public async Task RecycledPage_IsNotSupported_ToEmptyTheRecycleBinInOneNote()
    {
        var error = Error(await Tool().PermanentlyDeleteHierarchyObject(BinnedPageId, null, Ct), "NotSupported");

        Assert.Equal(PermanentDeleteWrite.RecycledPageUserAction, error.GetProperty("userAction").GetString());
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task PageInALockedSection_IsSectionLocked_BeforeCom()
    {
        Error(await Tool().PermanentlyDeleteHierarchyObject(LockedPageId, new PageBasisArgument("2026-11-12T00:00:00.000Z"), Ct), "SectionLocked");

        await AssertRefusedAsync();
    }

    [Theory]
    [InlineData("2026-11-12T00:00:09.000Z", null)]
    [InlineData(null, "0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task Page_StaleBasis_IsStaleWrite_WithNoSnapshot_AndNoCom(string? date, string? hash)
    {
        var basis = PageBasis();
        Error(await Tool().PermanentlyDeleteHierarchyObject(PageId, new PageBasisArgument(date ?? basis.LastModifiedTime, hash ?? basis.PageHash), Ct), "StaleWrite");

        await AssertRefusedAsync();
    }

    // --- Sections ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Section_IsBackedUp_BeforeTheDelete_ThenDeleted_WithItsEntries()
    {
        long? sizeAtDelete = null;
        gateway.OnDeleteHierarchy = _ => sizeAtDelete = Directory.GetFiles(Backups).Select(f => new FileInfo(f).Length).SingleOrDefault();
        cache.Set(SecondPageId, DateTimeOffset.UnixEpoch, "cached text", long.MaxValue);

        var root = Ok(await Tool().PermanentlyDeleteHierarchyObject(SectionId, new PageBasisArgument(SectionDate), Ct));

        // AC 2: the backup exists and isn't empty when DeleteHierarchy is called; the section's own date goes to COM.
        Assert.True(sizeAtDelete > 0);
        Assert.Equal((SectionId, SectionDate, true), Assert.Single(gateway.DeleteHierarchyCalls));
        var publish = Assert.Single(gateway.PublishCalls);
        Assert.Equal(SectionId, publish.ObjectId);
        var meta = root.GetProperty("meta");
        var backupFile = meta.GetProperty("backupFile").GetString()!;
        Assert.Equal(Path.GetFileName(backupFile), backupFile);
        Assert.Equal(Path.Combine(Backups, backupFile), publish.TargetPath);

        // The onenoteBackup entry names the file and keeps the pages-scope XML; its name matches the backup's.
        var entryId = Guid.Parse(meta.GetProperty("journalEntryId").GetString()!);
        var entryFile = JournalFiles.Find(Folder, entryId)!;
        Assert.Equal(backupFile, JournalFiles.BackupName(Path.GetFileName(entryFile)));
        var entry = JournalStore.Read(entryFile);
        Assert.Equal(JournalEntry.OnenoteBackupKind, entry.SnapshotKind);
        Assert.Equal(backupFile, entry.BackupFile);
        Assert.Equal(SectionId, entry.ObjectId);
        Assert.Equal(NotebookId, entry.ParentId);
        Assert.Equal(GrantKeys.NotebookKey(NotebookPath), entry.NotebookPath);
        Assert.Contains(SecondPageId, entry.Xml, StringComparison.Ordinal);

        // Q3 b: a page entry per page, in section order, sharing the call's correlationId.
        var pageEntryIds = meta.GetProperty("pageEntryIds").EnumerateArray().Select(e => Guid.Parse(e.GetString()!)).ToList();
        Assert.Equal(2, pageEntryIds.Count);
        var pages = pageEntryIds.Select(id => JournalStore.Read(JournalFiles.Find(Folder, id)!)).ToList();
        Assert.Equal([PageId, SecondPageId], pages.Select(p => p.ObjectId));
        Assert.All(pages, p => Assert.Equal(JournalEntry.PageKind, p.SnapshotKind));
        Assert.All(pages, p => Assert.Equal(entry.CorrelationId, p.CorrelationId));
        Assert.Equal(meta.GetProperty("correlationId").GetString(), entry.CorrelationId);
        Assert.False(cache.TryGet(SecondPageId, DateTimeOffset.UnixEpoch, out _));
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task RecycledSection_IsBackedUp_AndDeleted()
    {
        var root = Ok(await Tool().PermanentlyDeleteHierarchyObject(RecycledSectionId, new PageBasisArgument(SectionDate), Ct));

        Assert.Equal((RecycledSectionId, SectionDate, true), Assert.Single(gateway.DeleteHierarchyCalls));
        Assert.True(File.Exists(Path.Combine(Backups, root.GetProperty("meta").GetProperty("backupFile").GetString()!)));
        var entry = JournalStore.Read(JournalFiles.Find(Folder, Guid.Parse(root.GetProperty("meta").GetProperty("journalEntryId").GetString()!))!);
        Assert.Equal("OneNote_RecycleBin", entry.ParentNamePath);
    }

    [Fact]
    public async Task Section_StaleDate_IsStaleWrite_WithNoBackup_AndNoCom()
    {
        Error(await Tool().PermanentlyDeleteHierarchyObject(SectionId, new PageBasisArgument("2026-11-12T00:09:59.000Z"), Ct), "StaleWrite");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task Section_WithAPageHash_IsInvalidArgument_AtStep1()
    {
        var prompt = new FakePrompt();

        Error(await RunAsync(SectionId, new PageWriteBasis(SectionDate, "abc"), prompt), "InvalidArgument");

        Assert.Empty(prompt.Order);
        Assert.Empty(holds);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ElementHashes_AreInvalidArgument_BeforeAnythingIsRead()
    {
        Error(await Tool().PermanentlyDeleteHierarchyObject(PageId, PageBasis(withElementHashes: true), Ct), "InvalidArgument");

        Assert.Empty(gateway.Calls);
        Assert.Empty(holds);
        AssertNothingWritten();
    }

    [Fact]
    public async Task EmptyObjectId_IsInvalidArgument()
    {
        Error(await Tool().PermanentlyDeleteHierarchyObject(string.Empty, null, Ct), "InvalidArgument");

        Assert.Empty(gateway.Calls);
        AssertNothingWritten();
    }

    // --- Backup failures (AC 3) -----------------------------------------------------------------------------------------

    [Fact]
    public async Task PublishHResult_IsBackupFailed_WithTheHResult_AndNothingIsDeleted()
    {
        gateway.PublishFailure = new OneNoteException(ErrorCode.Internal, "Publish failed: hrFileDoesNotExist (0x80042006).", unchecked((int)0x80042006));

        var error = Error(await Tool().PermanentlyDeleteHierarchyObject(SectionId, new PageBasisArgument(SectionDate), Ct), "BackupFailed");

        Assert.Equal("0x80042006", error.GetProperty("hresult").GetString());
        Assert.DoesNotContain(Folder, error.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.DeleteHierarchyCalls);
        Assert.Empty(Entries(JournalEntry.OnenoteBackupKind));
        await AssertLockFreeAsync();
    }

    [Theory]
    [InlineData(false, "never appeared")]
    [InlineData(true, "stayed empty")]
    public async Task ABackupFileThatNeverLands_OrStaysEmpty_IsBackupFailed_AndNothingIsDeleted(bool empty, string why)
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1, "timeouts": {"publishSeconds": 1}}""");
        Grant(permanentDelete: true);
        gateway.PublishedBytes = empty ? [] : null;

        var error = Error(await Tool().PermanentlyDeleteHierarchyObject(SectionId, new PageBasisArgument(SectionDate), Ct), "BackupFailed");

        Assert.Contains(why, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.DeleteHierarchyCalls);
        Assert.Empty(Entries(JournalEntry.OnenoteBackupKind));
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ABackupFileStillOpenForWriting_IsBackupFailed_AndNothingIsDeleted()
    {
        // Fresh review: a stable size alone isn't proof that OneNote has finished writing the backup.
        File.WriteAllText(Store.Path, """{"schemaVersion": 1, "timeouts": {"publishSeconds": 1}}""");
        Grant(permanentDelete: true);
        FileStream? held = null;
        gateway.OnPublish = async (_, target, ct) =>
        {
            held = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            await held.WriteAsync(new byte[] { 0x4F, 0x4E, 0x45, 0x21 }, ct);
            await held.FlushAsync(ct);
        };

        try
        {
            var error = Error(await Tool().PermanentlyDeleteHierarchyObject(SectionId, new PageBasisArgument(SectionDate), Ct), "BackupFailed");

            Assert.Contains("still open", error.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Empty(gateway.DeleteHierarchyCalls);
            Assert.Empty(Entries(JournalEntry.OnenoteBackupKind));
            await AssertLockFreeAsync();
        }
        finally
        {
            held?.Dispose();
        }
    }

    [Fact]
    public async Task ABackupFile_CountsAsFinished_OnceItCanBeOpenedExclusively()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.OnPublish = async (sectionId, target, ct) =>
        {
            var held = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            await held.WriteAsync(new byte[] { 0x4F, 0x4E, 0x45, 0x21 }, ct);
            await held.FlushAsync(ct);
            _ = Task.Run(async () =>
            {
                await Task.Delay(150);
                released.SetResult();
                await held.DisposeAsync();
            });
        };

        Ok(await Tool().PermanentlyDeleteHierarchyObject(SectionId, new PageBasisArgument(SectionDate), Ct));

        Assert.True(released.Task.IsCompleted, "The backup counted as finished while the file was still open.");
        Assert.Single(gateway.DeleteHierarchyCalls);
    }

    [Fact]
    public async Task APublishThatTimesOut_IsBackupFailed_AndKeepsTheLockUntilTheAbandonedCallSettles()
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.OnPublish = (_, _, _) =>
        {
            gateway.AbandonedWrites = settled;
            throw new OneNoteException(ErrorCode.Timeout, "Publish timed out.");
        };

        Error(await Tool().PermanentlyDeleteHierarchyObject(SectionId, new PageBasisArgument(SectionDate), Ct), "BackupFailed");

        Assert.Empty(gateway.DeleteHierarchyCalls);
        await Assert.ThrowsAsync<OneNoteException>(() => writeLock.AcquireAsync("held?", TimeSpan.FromMilliseconds(200), Ct));
        gateway.AbandonedWrites = null;
        settled.SetResult();
        await AssertLockFreeAsync();
    }

    // --- Section groups -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EmptySectionGroup_IsDeleted_WithAHierarchyEntry()
    {
        var root = Ok(await Tool().PermanentlyDeleteHierarchyObject(EmptyGroupId, new PageBasisArgument(GroupDate), Ct));

        Assert.Equal((EmptyGroupId, GroupDate, true), Assert.Single(gateway.DeleteHierarchyCalls));
        Assert.Empty(gateway.PublishCalls);
        var entry = JournalStore.Read(Assert.Single(Directory.GetFiles(Folder)));
        Assert.Equal(JournalEntry.HierarchyKind, entry.SnapshotKind);
        Assert.Equal("Empty", entry.Title);
        Assert.Equal(entry.EntryId.ToString("D"), root.GetProperty("meta").GetProperty("journalEntryId").GetString());
    }

    [Fact]
    public async Task NonEmptySectionGroup_IsNotSupported_BeforeCom()
    {
        var error = Error(await Tool().PermanentlyDeleteHierarchyObject(FullGroupId, new PageBasisArgument(GroupDate), Ct), "NotSupported");

        Assert.Equal(PermanentDeleteWrite.NonEmptyGroupUserAction, error.GetProperty("userAction").GetString());
        await AssertRefusedAsync();
    }

    // --- Refusals before any dialog (AC 4) -----------------------------------------------------------------------------

    [Theory]
    [InlineData(NotebookId, PermanentDeleteWrite.NotebookUserAction)]
    [InlineData(ProtectedId, PermanentDeleteWrite.ProtectedSectionUserAction)]
    [InlineData(LockedId, PermanentDeleteWrite.ProtectedSectionUserAction)]
    [InlineData(RecycledVaultId, PermanentDeleteWrite.ProtectedSectionUserAction)]
    public async Task NotebookOrProtectedSection_IsNotSupported_WithNoDialog_NoLock_NoPublish_NoCom(string id, string userAction)
    {
        Grant(permanentDelete: false);
        var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted, "anything"));

        var error = Error(await RunAsync(id, null, prompt), "NotSupported");

        Assert.Equal(userAction, error.GetProperty("userAction").GetString());
        Assert.Empty(prompt.Order);
        Assert.Empty(holds);
        AssertNothingWritten();
    }

    [Fact]
    public async Task TheRecycleBin_IsObjectNotFound()
    {
        Error(await Tool().PermanentlyDeleteHierarchyObject(RecycleBinId, null, Ct), "ObjectNotFound");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task DeletedPages_IsNotSupported()
    {
        var error = Error(await Tool().PermanentlyDeleteHierarchyObject(DeletedPagesId, null, Ct), "NotSupported");

        Assert.Equal(PermanentDeleteWrite.DeletedPagesUserAction, error.GetProperty("userAction").GetString());
        await AssertRefusedAsync();
    }

    // --- The permanent-delete grant (Q1 c, AC 1) ----------------------------------------------------------------------

    [Fact]
    public async Task WithoutThePermanentDeleteGrant_AndNoDialog_IsPermanentDeleteNotGranted_NothingDeletedOrJournaled()
    {
        Grant(permanentDelete: false);

        var error = Error(await Tool().PermanentlyDeleteHierarchyObject(PageId, PageBasis(), Ct), "PermanentDeleteNotGranted");

        Assert.Contains("grant --permanent-delete", error.GetProperty("userAction").GetString(), StringComparison.Ordinal);
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Empty(holds);
        AssertNothingWritten();
    }

    [Theory]
    [InlineData(GrantPromptStatus.Declined, null, "declined")]
    [InlineData(GrantPromptStatus.Cancelled, null, "cancelled")]
    [InlineData(GrantPromptStatus.Unsupported, null, "can't show")]
    [InlineData(GrantPromptStatus.Accepted, "Something else", "didn't match")]
    [InlineData(GrantPromptStatus.Accepted, "", "didn't match")]
    public async Task TheConfirmation_NotAnsweredWithTheName_IsPermanentDeleteNotGranted(GrantPromptStatus status, string? typed, string why)
    {
        Grant(permanentDelete: false);
        var prompt = new FakePrompt(new GrantPromptResult(status, typed));

        var error = Error(await RunAsync(PageId, PageBasis().ToCore(), prompt), "PermanentDeleteNotGranted");

        Assert.Contains(why, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(["permanent"], prompt.Order);
        Assert.Empty(holds);
        AssertNothingWritten();
        Assert.False(Store.Read().Notebooks.Values.Single().PermanentDelete);
    }

    [Fact]
    public async Task TheConfirmation_AnsweredWithTheName_DeletesThisOnce_AndStoresNothing()
    {
        Grant(permanentDelete: false);
        var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted, "  synthetic update "));

        Ok(await RunAsync(PageId, PageBasis().ToCore(), prompt));

        var question = Assert.Single(prompt.PermanentQuestions);
        Assert.Equal(("page", "Synthetic Update", "W", "Synthetic Update"), (question.Kind, question.ObjectName, question.NotebookName, question.ExpectedText));
        Assert.Equal((GrantKeys.NotebookKey(NotebookPath), "Open", (int?)null), (question.NotebookPath, question.SectionName, question.PageCount));
        Assert.Single(gateway.DeleteHierarchyCalls);
        Assert.False(Store.Read().Notebooks.Values.Single().PermanentDelete);
    }

    [Fact]
    public async Task WithTheStoredGrant_NoConfirmationIsAsked()
    {
        var prompt = new FakePrompt();

        Ok(await RunAsync(EmptyGroupId, null, prompt));

        Assert.Empty(prompt.Order);
    }

    [Fact]
    public async Task WithoutAWriteGrant_TheWriteDialogComesFirst_ThenTheConfirmation()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        gateway.Hierarchies[(string.Empty, HierarchyScope.Sections)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebooks {Ns}><one:Notebook name=\"W\" nickname=\"W\" ID=\"{NotebookId}\" path=\"{NotebookPath}\" /></one:Notebooks>";
        var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted, "W"), new GrantPromptResult(GrantPromptStatus.Accepted, "Empty"));

        Ok(await RunAsync(EmptyGroupId, null, prompt));

        Assert.Equal(["write", "permanent"], prompt.Order);
        var grant = Store.Read().Notebooks.Values.Single();
        Assert.True(grant.Write);
        Assert.False(grant.PermanentDelete);
    }

    [Theory]
    [InlineData(RecycleBinId, "ObjectNotFound")]
    [InlineData(DeletedPagesId, "NotSupported")]
    [InlineData(BinnedPageId, "NotSupported")]
    [InlineData(RecycledSectionPageId, "NotSupported")]
    [InlineData(FullGroupId, "NotSupported")]
    [InlineData(LockedPageId, "SectionLocked")]
    [InlineData(ParentPageId, "InvalidArgument")]
    public async Task WhatStep4WouldRefuse_IsRefusedAtStep1_BeforeEitherDialog(string id, string code)
    {
        // Review: no write-grant dialog and no confirmation for a delete that can't run.
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted, "W"), new GrantPromptResult(GrantPromptStatus.Accepted, "anything"));

        Error(await RunAsync(id, null, prompt), code);

        Assert.Empty(prompt.Order);
        Assert.Empty(holds);
        AssertNothingWritten();
    }

    [Theory]
    [InlineData(LockedPageId, "SectionLocked", "Section " + LockedId + " is locked, so page " + LockedPageId + " wasn't deleted.")]
    [InlineData(ParentPageId, "InvalidArgument", "Page " + ParentPageId + " has 1 subpage(s), so nothing was deleted.")]
    public async Task APageInALockedSection_OrWithSubpages_IsRefused_BeforeTheConfirmationIsAsked(string id, string code, string message)
    {
        // Fresh review: the user typed the name, and only then was the delete refused.
        Grant(permanentDelete: false);
        var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted, "Synthetic Update"));

        var error = Error(await RunAsync(id, null, prompt), code);

        Assert.Equal(message, error.GetProperty("message").GetString());
        Assert.Empty(prompt.Order);
        Assert.Empty(holds);
        AssertNothingWritten();
    }

    [Fact]
    public async Task AProtectedPage_WithoutAReadGrant_OrAStoredGrant_IsPermanentDeleteNotGranted_WithNoDialog_AndNoTitleAnywhere()
    {
        Grant(permanentDelete: false);
        var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted, "Synthetic Update"));

        var result = await RunAsync(VaultPageId, new PageWriteBasis("2026-11-12T00:00:00.000Z"), prompt);

        var error = Error(result, "PermanentDeleteNotGranted");
        Assert.Contains("grant --permanent-delete \"<notebook name>\"", error.GetProperty("userAction").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic Update", result.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic Update", string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text)), StringComparison.Ordinal);
        Assert.Empty(prompt.Order);
        Assert.Empty(holds);
        AssertNothingWritten();
    }

    [Fact]
    public async Task TheConfirmation_ShowsASanitisedName_AndTheUserTypesThatName()
    {
        Grant(permanentDelete: false);
        var title = "Re\u202Ead \"me\"\nnow" + new string('x', 100);
        Self(SecondPageId, $"<one:Page {Ns} ID=\"{SecondPageId}\" name=\"{System.Security.SecurityElement.Escape(title).Replace("\n", "&#10;", StringComparison.Ordinal)}\" lastModifiedTime=\"{PageHierarchyDate}\" />");
        var expected = "Read 'me'now" + new string('x', 79 - "Read 'me'now".Length);
        var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted, expected.ToUpperInvariant()));

        Ok(await RunAsync(SecondPageId, null, prompt));

        var question = Assert.Single(prompt.PermanentQuestions);
        Assert.Equal(expected, question.ExpectedText);
        Assert.Equal(expected + "…", question.ObjectName);
        Assert.Equal(80, question.ObjectName.Length);
        Assert.Single(gateway.DeleteHierarchyCalls);
    }

    [Fact]
    public async Task TheConfirmation_ForASection_CountsItsPages()
    {
        Grant(permanentDelete: false);
        var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted, "Open"));

        Ok(await RunAsync(SectionId, null, prompt));

        var question = Assert.Single(prompt.PermanentQuestions);
        Assert.Equal(("section", 2, (string?)null), (question.Kind, question.PageCount, question.SectionName));
    }

    [Theory]
    [InlineData("Synthetic Update", "2026-11-12 00:00:01 UTC")]
    [InlineData("SYNTHETIC\u200B update", "2026-11-12 00:00:01 UTC")]
    [InlineData("Synthetic Update 2", null)]
    public async Task TheConfirmation_ForAPageNamedLikeAnotherInItsSection_ShowsItsLastModifiedDate(string otherName, string? shown)
    {
        // Fresh review: two same-named pages in one section gave word-for-word the same dialog.
        Grant(permanentDelete: false);
        var attributes = $"name=\"Open\" ID=\"{SectionId}\" path=\"C:\\notebooks\\W\\Open.one\" lastModifiedTime=\"{SectionDate}\"";
        gateway.Hierarchies[(SectionId, HierarchyScope.Pages)] =
            $"<?xml version=\"1.0\"?>\n<one:Section {Ns} {attributes}><one:Page ID=\"{PageId}\" name=\"Synthetic Update\" pageLevel=\"1\" />"
            + $"<one:Page ID=\"{SecondPageId}\" name=\"{otherName}\" pageLevel=\"1\" /></one:Section>";
        var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted, "Synthetic Update"));

        Ok(await RunAsync(PageId, null, prompt));

        Assert.Equal(shown, Assert.Single(prompt.PermanentQuestions).LastModified);
    }

    // --- Changes while the backup runs (review) ----------------------------------------------------------------------

    [Theory]
    [InlineData("pageAdded")]
    [InlineData("pageEdited")]
    [InlineData("sectionDate")]
    public async Task ASectionChangedDuringItsBackup_IsStaleWrite_NothingDeleted_TheBackupStays(string change)
    {
        gateway.OnPublish = async (_, target, ct) =>
        {
            await File.WriteAllBytesAsync(target, [1, 2, 3], ct);
            var pages = gateway.Hierarchies[(SectionId, HierarchyScope.Pages)];
            switch (change)
            {
                case "pageAdded":
                    gateway.Hierarchies[(SectionId, HierarchyScope.Pages)] = pages.Replace("</one:Section>", "<one:Page ID=\"{NEW}\" name=\"New\" /></one:Section>", StringComparison.Ordinal);
                    break;
                case "pageEdited":
                    gateway.Hierarchies[(SectionId, HierarchyScope.Pages)] = pages.Replace($"<one:Page ID=\"{SecondPageId}\"", $"<one:Page lastModifiedTime=\"2026-11-13T00:00:00.000Z\" ID=\"{SecondPageId}\"", StringComparison.Ordinal);
                    break;
                default:
                    gateway.Hierarchies[(SectionId, HierarchyScope.Self)] = gateway.Hierarchies[(SectionId, HierarchyScope.Self)].Replace(SectionDate, "2026-11-13T00:00:00.000Z", StringComparison.Ordinal);
                    break;
            }
        };

        var error = Error(await Tool().PermanentlyDeleteHierarchyObject(SectionId, new PageBasisArgument(SectionDate), Ct), "StaleWrite");

        Assert.Empty(gateway.DeleteHierarchyCalls);
        var backupEntry = Assert.Single(Entries(JournalEntry.OnenoteBackupKind));
        Assert.Contains(backupEntry.EntryId.ToString("D"), error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Backups));
        await AssertLockFreeAsync();
    }

    [Theory]
    [InlineData(true, "WriteNotGranted")]
    [InlineData(false, "PermanentDeleteNotGranted")]
    public async Task AGrantRevokedDuringTheBackup_StopsTheDelete(bool revokeWrite, string code)
    {
        gateway.OnPublish = async (_, target, ct) =>
        {
            await File.WriteAllBytesAsync(target, [1, 2, 3], ct);
            var writer = new SettingsWriter(Store, clock);
            if (revokeWrite)
            {
                writer.RevokeWriteGrant(GrantKeys.NotebookKey(NotebookPath));
            }
            else
            {
                writer.RevokePermanentDeleteGrant(GrantKeys.NotebookKey(NotebookPath));
            }
        };

        Error(await Tool().PermanentlyDeleteHierarchyObject(SectionId, new PageBasisArgument(SectionDate), Ct), code);

        Assert.Empty(gateway.DeleteHierarchyCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AConfirmedDelete_StillRuns_WithNoStoredPermanentDeleteGrant_ButStopsIfTheWriteGrantGoes()
    {
        Grant(permanentDelete: false);
        Ok(await RunAsync(EmptyGroupId, null, new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted, "Empty"))));
        Assert.Single(gateway.DeleteHierarchyCalls);

        gateway.OnPublish = async (_, target, ct) =>
        {
            await File.WriteAllBytesAsync(target, [1], ct);
            new SettingsWriter(Store, clock).RevokeWriteGrant(GrantKeys.NotebookKey(NotebookPath));
        };
        Error(await RunAsync(SectionId, null, new FakePrompt(new GrantPromptResult(GrantPromptStatus.Accepted, "Open"))), "WriteNotGranted");
        Assert.Single(gateway.DeleteHierarchyCalls);
    }

    // --- OneNote's own refusals -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ErrorCode.StaleWrite, 0x80042010)]
    [InlineData(ErrorCode.ObjectNotFound, 0x80042014)]
    [InlineData(ErrorCode.SectionLocked, 0x8004201B)]
    [InlineData(ErrorCode.ModalDialogOpen, 0x80042030)]
    public async Task AComRefusal_KeepsItsCode_AndNamesTheJournalEntry(ErrorCode code, uint hresult)
    {
        gateway.DeleteHierarchyFailure = new OneNoteException(code, "DeleteHierarchy failed.", unchecked((int)hresult));

        var error = Error(await Tool().PermanentlyDeleteHierarchyObject(PageId, PageBasis(), Ct), code.ToString());

        var entry = JournalStore.Read(Assert.Single(Directory.GetFiles(Folder)));
        Assert.Contains(entry.EntryId.ToString("D"), error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(error.TryGetProperty("outcomeUnknown", out var unknown) && unknown.GetBoolean());
        Assert.Single(gateway.DeleteHierarchyCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AComTimeout_IsOutcomeUnknown_AndNotRetried()
    {
        gateway.DeleteHierarchyFailure = new OneNoteException(ErrorCode.Timeout, "DeleteHierarchy didn't finish.");

        var error = Error(await Tool().PermanentlyDeleteHierarchyObject(PageId, PageBasis(), Ct), "Timeout");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Single(gateway.DeleteHierarchyCalls);
    }

    // --- The pipeline ---------------------------------------------------------------------------------------------------

    [Fact]
    public void PermanentDelete_RunsUnderThe600SecondDeadline_OthersUnder120_AndATestOverrideWinsForBoth()
    {
        var pipeline = new WritePipeline(writeLock, gateway, cache, Store.Read);
        var overridden = new WritePipeline(writeLock, gateway, cache, Store.Read, deadline: TimeSpan.FromSeconds(5));

        Assert.Equal(TimeSpan.FromSeconds(600), pipeline.DeadlineFor(OperationClass.PermanentDelete));
        Assert.Equal(TimeSpan.FromSeconds(120), pipeline.DeadlineFor(OperationClass.PageWrite));
        Assert.Equal(TimeSpan.FromSeconds(5), overridden.DeadlineFor(OperationClass.PermanentDelete));
        Assert.Equal(OperationClass.PermanentDelete, UseCase().Class);
    }

    // --- The target reader (Xml) ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("A", 3)] // B, C (under B) and D
    [InlineData("B", 1)]
    [InlineData("C", 0)]
    [InlineData("D", 0)]
    [InlineData("E", 0)]
    public void Subpages_CountsTheDeeperPagesStraightAfter_UpToTheNextAtTheSameLevel(string page, int expected)
    {
        var xml = $"<one:Section {Ns} ID=\"S\">"
            + "<one:Page ID=\"A\" pageLevel=\"1\" /><one:Page ID=\"B\" pageLevel=\"2\" /><one:Page ID=\"C\" pageLevel=\"3\" />"
            + "<one:Page ID=\"D\" pageLevel=\"2\" /><one:Page ID=\"E\" pageLevel=\"1\" /></one:Section>";

        Assert.Equal(expected, PermanentDeleteTarget.Subpages(xml, page));
        Assert.Null(PermanentDeleteTarget.Subpages(xml, "Z"));
        Assert.Equal(["A", "B", "C", "D", "E"], PermanentDeleteTarget.PageIds(xml));
    }

    [Fact]
    public void Target_ReadsTheRefusalAttributes_AndARecycleBinByItsName()
    {
        var bin = PermanentDeleteTarget.Read($"<one:SectionGroup {Ns} ID=\"G\" name=\"OneNote_RecycleBin\" />");
        var deleted = PermanentDeleteTarget.Read($"<one:Section {Ns} ID=\"S\" name=\"Deleted Pages\" isInRecycleBin=\"true\" isDeletedPages=\"true\" lastModifiedTime=\"{SectionDate}\" />");
        var vault = PermanentDeleteTarget.Read($"<one:Section {Ns} ID=\"V\" name=\"Vault\" encrypted=\"1\" locked=\"true\" />");

        Assert.True(bin.IsRecycleBin);
        Assert.Equal((true, true, SectionDate), (deleted.IsInRecycleBin, deleted.IsDeletedPages, deleted.LastModifiedTime));
        Assert.False(deleted.IsRecycleBin);
        Assert.Equal((true, true), (vault.Encrypted, vault.Locked));
        Assert.Equal(0, PermanentDeleteTarget.ContainersBeneath($"<one:SectionGroup {Ns} ID=\"G\" />"));
        Assert.Equal(2, PermanentDeleteTarget.ContainersBeneath($"<one:SectionGroup {Ns} ID=\"G\"><one:SectionGroup ID=\"H\"><one:Section ID=\"S\" /></one:SectionGroup></one:SectionGroup>"));
    }

    // --- Helpers --------------------------------------------------------------------------------------------------------

    private void Self(string id, string xml) => gateway.Hierarchies[(id, HierarchyScope.Self)] = "<?xml version=\"1.0\"?>\n" + xml;

    private void Section(string id, string name, string extra, params string[] pages)
    {
        var attributes = $"name=\"{name}\" ID=\"{id}\" path=\"C:\\notebooks\\W\\{name}.one\" lastModifiedTime=\"{SectionDate}\" {extra}";
        Self(id, $"<one:Section {Ns} {attributes} />");
        var children = string.Concat(pages.Select(p => $"<one:Page ID=\"{p}\" name=\"Synthetic Update\" pageLevel=\"{(p == ChildPageId ? 2 : 1)}\" />"));
        gateway.Hierarchies[(id, HierarchyScope.Pages)] = $"<?xml version=\"1.0\"?>\n<one:Section {Ns} {attributes}>{children}</one:Section>";
    }

    private void Group(string id, string name, string children)
    {
        var attributes = $"name=\"{name}\" ID=\"{id}\" path=\"C:\\notebooks\\W\\{name}\" lastModifiedTime=\"{GroupDate}\"";
        Self(id, $"<one:SectionGroup {Ns} {attributes} />");
        gateway.Hierarchies[(id, HierarchyScope.Pages)] = $"<?xml version=\"1.0\"?>\n<one:SectionGroup {Ns} {attributes}>{children}</one:SectionGroup>";
    }

    private void Grant(bool permanentDelete)
    {
        var writer = new SettingsWriter(Store, clock);
        var key = GrantKeys.NotebookKey(NotebookPath);
        writer.SaveWriteGrant(new NotebookGrant(key, "W", NotebookId, Write: true, PermanentDelete: false, clock.GetUtcNow(), GrantedVia.Cli));
        if (permanentDelete)
        {
            writer.SavePermanentDeleteGrant(key);
        }
        else
        {
            writer.RevokePermanentDeleteGrant(key);
        }
    }

    /// <summary>The read's basis for <paramref name="pageId"/>: its date and pageHash, element hashes only when asked.</summary>
    private static PageBasisArgument PageBasis(string pageId = PageId, bool withElementHashes = false)
    {
        var basis = OneNoteMcp.Xml.PageBasis.From(Read.Replace(PageId, pageId, StringComparison.Ordinal));
        return new PageBasisArgument(basis.LastModifiedTime, basis.PageHash, withElementHashes ? basis.ElementHashes : null);
    }

    private PermanentDeleteUseCase UseCase()
    {
        var journal = new JournalStore(Store.Read);
        var services = new PermanentDeleteServices(
            gateway,
            new WriteAccess(gateway, Store.Read),
            readAccess,
            new PageSnapshot(gateway, journal, clock),
            new HierarchySnapshot(gateway, journal, clock),
            new SectionBackup(gateway, journal, Store.Read, TimeProvider.System, TimeSpan.FromMilliseconds(10)));
        return new PermanentDeleteUseCase(
            services,
            new WritePipeline(writeLock, gateway, cache, Store.Read, TimeProvider.System, dialog: new WriteGrantDialog(gateway, new SettingsWriter(Store, clock), clock)));
    }

    private PermanentlyDeleteHierarchyObjectTool Tool() => new(new ToolRunner(NullLogger<ToolRunner>.Instance), UseCase());

    private Task<CallToolResult> RunAsync(string objectId, PageWriteBasis? basis, IGrantPrompt prompt)
    {
        var useCase = UseCase();
        return new ToolRunner(NullLogger<ToolRunner>.Instance).RunAsync(
            PermanentlyDeleteHierarchyObjectTool.Name,
            useCase,
            (context, token) => useCase.ExecuteAsync(context, objectId, basis, token),
            Ct,
            prompt);
    }

    private List<JournalEntry> Entries(string kind) =>
        Directory.Exists(Folder)
            ? [.. Directory.GetFiles(Folder).Where(f => JournalFiles.IsEntry(Path.GetFileName(f))).Select(JournalStore.Read).Where(e => e.SnapshotKind == kind)]
            : [];

    private static string Decompressed(string file)
    {
        using var stream = new System.IO.Compression.GZipStream(File.OpenRead(file), System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void AssertNothingWritten()
    {
        Assert.Empty(gateway.DeleteHierarchyCalls);
        Assert.Empty(gateway.PublishCalls);
        Assert.False(Directory.Exists(Folder) && Directory.EnumerateFileSystemEntries(Folder, "*", SearchOption.AllDirectories).Any());
    }

    private async Task AssertRefusedAsync()
    {
        AssertNothingWritten();
        await AssertLockFreeAsync();
    }

    private async Task AssertLockFreeAsync()
    {
        using var probe = await writeLock.AcquireAsync("free?", TimeSpan.FromMilliseconds(2000), Ct);
    }

    private static List<string> Codes(JsonElement root) =>
        [.. root.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()!)];

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
}
