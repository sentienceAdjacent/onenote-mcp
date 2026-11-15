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
/// Story 5.5: <c>delete_hierarchy_object</c> through the AD-7 write pipeline as a <c>SoftDelete</c>, one test or theory per
/// row of the plan's I/O matrix, against <see cref="FakeGateway"/> scripted with a synthetic notebook <c>W</c>: section
/// <c>Open</c> (the synthetic page, and a page with a subpage), a password-protected section, a locked one, the group
/// <c>Archive</c> (section <c>Plans</c> and a protected section), the group <c>Sealed</c> (a locked section), and the
/// recycle bin (Deleted Pages, a recycled section). A temporary <c>settings.json</c> and journal folder; each test has a
/// write lock of its own and checks it is free again. Refusals leave no COM delete and no journal entry.
/// </summary>
public sealed class SoftDeleteToolTests : IDisposable
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";
    private const string NotebookId = "{NBW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string NotebookPath = @"C:\notebooks\W\";
    private const string SectionId = "{SW000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string ProtectedId = "{SP000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string LockedId = "{SL000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string GroupId = "{GW000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string PlansId = "{S2000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string InnerVaultId = "{S3000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string SealedGroupId = "{GL000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string SealedId = "{S4000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string RecycleBinId = "{RBW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string DeletedPagesId = "{SDW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string BinnedSectionId = "{SBW00000-0000-4000-8000-000000000001}{1}{B0}";
    private const string VaultPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000002}";
    private const string LockedPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000003}";
    private const string BinnedPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000004}";
    private const string ParentPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000005}";
    private const string ChildPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000006}";
    private const string PlansPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000007}";
    private const string InnerVaultPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000008}";
    private const string RecycledId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000090}";
    private const string RecycledSectionPageId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000092}";
    private const string OtherRecycledId = "{5E900000-0000-4000-8000-000000000001}{1}{E1900000000000000000000000000000000000000091}";

    /// <summary>The page's hierarchy date, unlike its page-XML date (a displayed cloud page's two dates can differ, AD-17).</summary>
    private const string PageHierarchyDate = "2026-11-12T00:00:07.000Z";
    private const string SectionDate = "2026-11-13T09:00:00.000Z";
    private const string Title = "Synthetic Update";

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly FakeGateway gateway = new();
    private readonly ConcurrentQueue<WriteLockHeld> holds = new();
    private readonly WriteLock writeLock;
    private readonly PageTextCache cache = PageTextCache.WithDefaults();
    private readonly ReadAccess readAccess;
    private readonly List<(string Id, string Name)> deletedPages = [(BinnedPageId, "Old page")];
    private string openPages;

    public SoftDeleteToolTests()
    {
        writeLock = new WriteLock($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", holds.Enqueue);
        readAccess = new ReadAccess(gateway, Store.Read, new SettingsWriter(Store, clock), clock);

        foreach (var (child, parent) in new[]
        {
            (NotebookId, string.Empty), (SectionId, NotebookId), (ProtectedId, NotebookId), (LockedId, NotebookId), (GroupId, NotebookId),
            (PlansId, GroupId), (InnerVaultId, GroupId), (SealedGroupId, NotebookId), (SealedId, SealedGroupId), (RecycleBinId, NotebookId),
            (DeletedPagesId, RecycleBinId), (BinnedSectionId, RecycleBinId), (PageId, SectionId), (ParentPageId, SectionId), (ChildPageId, SectionId),
            (VaultPageId, ProtectedId), (LockedPageId, LockedId), (BinnedPageId, DeletedPagesId), (PlansPageId, PlansId), (InnerVaultPageId, InnerVaultId),
        })
        {
            gateway.Parents[child] = parent;
        }

        Self(NotebookId, $"<one:Notebook {Ns} name=\"W\" nickname=\"W\" ID=\"{NotebookId}\" path=\"{NotebookPath}\" lastModifiedTime=\"{SectionDate}\" />");
        Self(SectionId, Section(SectionId, "Open", string.Empty));
        Self(ProtectedId, Section(ProtectedId, "Vault", "encrypted=\"true\""));
        Self(LockedId, Section(LockedId, "Locked vault", "encrypted=\"true\" locked=\"true\""));
        Self(GroupId, Group(GroupId, "Archive", string.Empty));
        Self(PlansId, Section(PlansId, "Plans", string.Empty, @"Archive\"));
        Self(InnerVaultId, Section(InnerVaultId, "Inner vault", "encrypted=\"true\"", @"Archive\"));
        Self(SealedGroupId, Group(SealedGroupId, "Sealed", string.Empty));
        Self(SealedId, Section(SealedId, "Sealed vault", "encrypted=\"true\" locked=\"true\"", @"Sealed\"));
        Self(RecycleBinId, Group(RecycleBinId, "OneNote_RecycleBin", "isRecycleBin=\"true\""));
        Self(DeletedPagesId, Section(DeletedPagesId, "Deleted Pages", "isInRecycleBin=\"true\" isDeletedPages=\"true\"", @"OneNote_RecycleBin\"));
        Self(BinnedSectionId, Section(BinnedSectionId, "Gone", "isInRecycleBin=\"true\"", @"OneNote_RecycleBin\"));
        foreach (var page in new[] { PageId, ParentPageId, ChildPageId, VaultPageId, LockedPageId, PlansPageId, InnerVaultPageId })
        {
            Self(page, $"<one:Page {Ns} ID=\"{page}\" name=\"{Title}\" />");
        }

        Self(BinnedPageId, $"<one:Page {Ns} ID=\"{BinnedPageId}\" name=\"Old page\" isInRecycleBin=\"true\" />");

        openPages = $"""
            <one:Page ID="{PageId}" name="{Title}" lastModifiedTime="{PageHierarchyDate}" pageLevel="1" />
            <one:Page ID="{ParentPageId}" name="Parent" lastModifiedTime="{PageHierarchyDate}" pageLevel="1" />
            <one:Page ID="{ChildPageId}" name="Child" lastModifiedTime="{PageHierarchyDate}" pageLevel="2" />
            """;
        OpenSectionPages();
        Pages(ProtectedId, Section(ProtectedId, "Vault", $"encrypted=\"true\" lastModifiedTime=\"{SectionDate}\"", inner: $"<one:Page ID=\"{VaultPageId}\" name=\"{Title}\" lastModifiedTime=\"{SectionDate}\" />"));
        Pages(LockedId, Section(LockedId, "Locked vault", $"encrypted=\"true\" locked=\"true\" lastModifiedTime=\"{SectionDate}\""));
        Pages(PlansId, PlansXml(withNs: true));
        Pages(GroupId, Group(GroupId, "Archive", $"lastModifiedTime=\"{SectionDate}\"", PlansXml(withNs: false) + InnerVaultXml()));
        Pages(SealedGroupId, Group(SealedGroupId, "Sealed", $"lastModifiedTime=\"{SectionDate}\"", $"<one:Section name=\"Sealed vault\" ID=\"{SealedId}\" path=\"C:\\notebooks\\W\\Sealed\\Sealed vault.one\" encrypted=\"true\" locked=\"true\" />"));
        Pages(DeletedPagesId, Section(DeletedPagesId, "Deleted Pages", "isInRecycleBin=\"true\" isDeletedPages=\"true\"", @"OneNote_RecycleBin\"));
        Pages(BinnedSectionId, Section(BinnedSectionId, "Gone", $"isInRecycleBin=\"true\" lastModifiedTime=\"{SectionDate}\"", @"OneNote_RecycleBin\"));
        NotebookChildren(withBin: true);
        Bin();

        gateway.Pages[PageId] = Read;
        gateway.Pages[ParentPageId] = Read.Replace(PageId, ParentPageId, StringComparison.Ordinal);
        gateway.Pages[VaultPageId] = Read.Replace(PageId, VaultPageId, StringComparison.Ordinal);

        // A page soft delete lands in Deleted Pages under a new ID (spike 5.1 constraint 8).
        gateway.OnDeleteHierarchy = id =>
        {
            if (id == PageId)
            {
                deletedPages.Add((RecycledId, Title));
                Bin();
            }
        };

        Grant();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Folder => temp.Combine("journal");

    private SettingsStore Store => new(temp.Combine("settings.json"), Folder);

    public void Dispose() => temp.Dispose();

    // --- Pages ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Page_IsSoftDeleted_WithTheHierarchyDate_AndItsRecycleBinId_InMetaAndInTheJournalEntry()
    {
        cache.Set(PageId, DateTimeOffset.UnixEpoch, "cached text", long.MaxValue);

        var root = Ok(await Tool().DeleteHierarchyObject(PageId, ReadBasisArgument(), Ct));

        // AD-17's page-delete exception: the page-XML basis is compared, and DeleteHierarchy is sent the hierarchy date.
        Assert.Equal((PageId, PageHierarchyDate, false), Assert.Single(gateway.DeleteHierarchyCalls));
        var meta = root.GetProperty("meta");
        Assert.Equal(["correlationId", "journalEntryId", "objectId", "recycleBinId"], meta.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(PageId, meta.GetProperty("objectId").GetString());
        Assert.Equal(RecycledId, meta.GetProperty("recycleBinId").GetString());
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());

        var entry = JournalStore.Read(Assert.Single(Directory.GetFiles(Folder)));
        Assert.Equal(JournalEntry.PageKind, entry.SnapshotKind);
        Assert.Equal(SoftDeleteUseCase.OperationName, entry.Operation);
        Assert.Equal(nameof(OperationClass.SoftDelete), entry.OperationClass);
        Assert.Equal(PageId, entry.ObjectId);
        Assert.Equal(SectionId, entry.ParentId);
        Assert.Equal(RecycledId, entry.RecycleBinId);
        Assert.Equal(entry.EntryId.ToString("D"), meta.GetProperty("journalEntryId").GetString());
        Assert.Equal(meta.GetProperty("correlationId").GetString(), entry.CorrelationId);
        Assert.False(cache.TryGet(PageId, DateTimeOffset.UnixEpoch, out _));
        Assert.Equal(meta.GetProperty("correlationId").GetString(), Assert.Single(holds).CorrelationId);
        Assert.Empty(gateway.DeleteCalls);
        Assert.Empty(gateway.UpdateCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Page_TheSnapshotIsTakenBeforeTheCall()
    {
        var entriesAtCall = -1;
        gateway.OnDeleteHierarchy = _ => entriesAtCall = Directory.GetFiles(Folder).Length;

        Ok(await Tool().DeleteHierarchyObject(PageId, ReadBasisArgument(), Ct));

        Assert.Equal(1, entriesAtCall);
    }

    [Fact]
    public async Task Page_NoBasis_IsOk_WithNoBasis_AndStillSendsTheHierarchyDate()
    {
        var root = Ok(await Tool().DeleteHierarchyObject(PageId, null, Ct));

        Assert.Equal([ToolWarnings.NoBasis], Codes(root));
        var warning = root.GetProperty("warnings")[0];
        Assert.Equal(SoftDeleteUseCase.PageBasisUserAction, warning.GetProperty("userAction").GetString());
        Assert.Contains("leave out its elementHashes", SoftDeleteUseCase.PageBasisUserAction, StringComparison.Ordinal);
        Assert.DoesNotContain("overwritten", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(PageHierarchyDate, Assert.Single(gateway.DeleteHierarchyCalls).ExpectedLastModified);
    }

    [Fact]
    public async Task Page_DateOnlyBasis_IsOk_WithPartialBasis()
    {
        var root = Ok(await Tool().DeleteHierarchyObject(PageId, new DeleteBasisArgument(ReadBasis.LastModifiedTime), Ct));

        Assert.Equal([ToolWarnings.PartialBasis], Codes(root));
        Assert.Contains("pageHash", Warning(root, ToolWarnings.PartialBasis), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_ChangedSinceTheBasis_WithTheSameDate_IsStaleWrite_FromThePageHash_NoSnapshot_NoCom()
    {
        // A page that isn't displayed keeps its page-XML date after a COM edit (spike 2.2 constraint 4): the hash catches it.
        var basis = ReadBasisArgument();
        gateway.Pages[PageId] = After(p => Drop(p, 40));

        var error = Error(await Tool().DeleteHierarchyObject(PageId, basis, Ct), "StaleWrite");

        Assert.Contains("pageHash", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task Page_StaleDate_IsStaleWrite_NoSnapshot_NoCom()
    {
        Error(await Tool().DeleteHierarchyObject(PageId, new DeleteBasisArgument("2026-11-11T00:00:00.000Z", ReadBasis.PageHash), Ct), "StaleWrite");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task Page_FirstDeleteInANotebookWithNoRecycleBinYet_PicksTheOnlyPageThere()
    {
        NotebookChildren(withBin: false);
        deletedPages.Clear();
        gateway.OnDeleteHierarchy = _ =>
        {
            NotebookChildren(withBin: true);
            deletedPages.Add((RecycledId, Title));
            Bin();
        };

        var root = Ok(await Tool().DeleteHierarchyObject(PageId, ReadBasisArgument(), Ct));

        Assert.Equal(RecycledId, root.GetProperty("meta").GetProperty("recycleBinId").GetString());
        Assert.Equal(RecycledId, JournalStore.Read(Assert.Single(Directory.GetFiles(Folder))).RecycleBinId);
    }

    [Fact]
    public async Task Page_TwoNewPagesInDeletedPages_AreNarrowedByName()
    {
        gateway.OnDeleteHierarchy = _ =>
        {
            deletedPages.Add((OtherRecycledId, "Someone else's page"));
            deletedPages.Add((RecycledId, Title));
            Bin();
        };

        var root = Ok(await Tool().DeleteHierarchyObject(PageId, ReadBasisArgument(), Ct));

        Assert.Equal(RecycledId, root.GetProperty("meta").GetProperty("recycleBinId").GetString());
    }

    [Fact]
    public async Task Page_AnAmbiguousReadBack_IsOk_WithRecycleBinIdMissing_NoMetaId_AndTheEntryAsFirstWritten()
    {
        gateway.OnDeleteHierarchy = _ =>
        {
            deletedPages.Add((OtherRecycledId, Title));
            deletedPages.Add((RecycledId, Title));
            Bin();
        };

        var root = Ok(await Tool().DeleteHierarchyObject(PageId, ReadBasisArgument(), Ct));

        Assert.Equal([ToolWarnings.RecycleBinIdMissing], Codes(root));
        var warning = root.GetProperty("warnings")[0];
        Assert.False(warning.TryGetProperty("userAction", out _));
        Assert.Contains("2 pages are new", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(root.GetProperty("meta").TryGetProperty("recycleBinId", out _));
        Assert.Null(JournalStore.Read(Assert.Single(Directory.GetFiles(Folder))).RecycleBinId);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Page_NothingNewInDeletedPages_IsOk_WithRecycleBinIdMissing()
    {
        gateway.OnDeleteHierarchy = _ => { };

        var root = Ok(await Tool().DeleteHierarchyObject(PageId, ReadBasisArgument(), Ct));

        Assert.Equal([ToolWarnings.RecycleBinIdMissing], Codes(root));
        Assert.Contains("no page is new", Warning(root, ToolWarnings.RecycleBinIdMissing), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_ARewriteThatFails_IsOk_WithRecycleBinIdMissing_AndTheIdStillInMeta()
    {
        gateway.OnDeleteHierarchy = _ =>
        {
            deletedPages.Add((RecycledId, Title));
            Bin();
            foreach (var file in Directory.GetFiles(Folder))
            {
                File.Delete(file);
            }
        };

        var root = Ok(await Tool().DeleteHierarchyObject(PageId, ReadBasisArgument(), Ct));

        Assert.Equal([ToolWarnings.RecycleBinIdMissing], Codes(root));
        Assert.Contains("the entry file is gone", Warning(root, ToolWarnings.RecycleBinIdMissing), StringComparison.Ordinal);
        Assert.Equal(RecycledId, root.GetProperty("meta").GetProperty("recycleBinId").GetString());
        Assert.Empty(Directory.GetFiles(Folder));
    }

    [Fact]
    public async Task Page_AReadBackThatFails_IsOk_WithRecycleBinIdMissing_NeverAnError()
    {
        gateway.OnDeleteHierarchy = _ => gateway.Hierarchies.Remove((RecycleBinId, HierarchyScope.Pages));

        var root = Ok(await Tool().DeleteHierarchyObject(PageId, ReadBasisArgument(), Ct));

        Assert.Equal([ToolWarnings.RecycleBinIdMissing], Codes(root));
        Assert.Contains("ObjectNotFound", Warning(root, ToolWarnings.RecycleBinIdMissing), StringComparison.Ordinal);
        Assert.Single(gateway.DeleteHierarchyCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Page_InAPasswordProtectedSection_IsNotSupported_WithAd21sUserAction_BeforeThePageIsRead()
    {
        var error = Error(await Tool().DeleteHierarchyObject(VaultPageId, null, Ct), "NotSupported");

        Assert.Equal(SoftDeleteUseCase.ProtectedPageUserAction, error.GetProperty("userAction").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Empty(gateway.PageCalls);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task Page_WithSubpages_IsInvalidArgument_GivingTheCountOnly()
    {
        var error = Error(await Tool().DeleteHierarchyObject(ParentPageId, null, Ct), "InvalidArgument");

        var message = error.GetProperty("message").GetString()!;
        Assert.Contains("1 subpage(s)", message, StringComparison.Ordinal);
        Assert.DoesNotContain(ChildPageId, error.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Child", message, StringComparison.Ordinal);
        Assert.Equal(SoftDeleteUseCase.SubpagesUserAction, error.GetProperty("userAction").GetString());
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task Page_WhoseHierarchyGivesNoDate_IsRefused_AndMinValueIsNeverSent()
    {
        openPages = $"<one:Page ID=\"{PageId}\" name=\"{Title}\" pageLevel=\"1\" />";
        OpenSectionPages();

        Error(await Tool().DeleteHierarchyObject(PageId, ReadBasisArgument(), Ct), "Internal");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task Page_InALockedSection_IsSectionLocked()
    {
        Error(await Tool().DeleteHierarchyObject(LockedPageId, null, Ct), "SectionLocked");

        Assert.Empty(gateway.PageCalls);
        await AssertRefusedAsync();
    }

    // --- Sections and section groups -----------------------------------------------------------------------------------

    [Fact]
    public async Task Section_IsSoftDeleted_WithItsHierarchyDate_AndAHierarchyEntryOfItsPagesScopeRead()
    {
        cache.Set(PlansPageId, DateTimeOffset.UnixEpoch, "cached text", long.MaxValue);

        var root = Ok(await Tool().DeleteHierarchyObject(PlansId, new DeleteBasisArgument(SectionDate), Ct));

        Assert.Equal((PlansId, SectionDate, false), Assert.Single(gateway.DeleteHierarchyCalls));
        var meta = root.GetProperty("meta");
        Assert.Equal(["correlationId", "journalEntryId", "objectId"], meta.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(PlansId, meta.GetProperty("objectId").GetString());
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());

        var entry = JournalStore.Read(Assert.Single(Directory.GetFiles(Folder)));
        Assert.Equal(JournalEntry.HierarchyKind, entry.SnapshotKind);
        Assert.Equal(nameof(OperationClass.SoftDelete), entry.OperationClass);
        Assert.Equal(SoftDeleteUseCase.OperationName, entry.Operation);
        Assert.Equal(PlansId, entry.ObjectId);
        Assert.Equal(GroupId, entry.ParentId);
        Assert.Equal("Archive", entry.ParentNamePath);
        Assert.Equal(GrantKeys.NotebookKey(NotebookPath), entry.NotebookPath);
        Assert.Null(entry.SectionKey);
        Assert.Null(entry.RecycleBinId);
        Assert.Equal("Plans", entry.Title);
        Assert.Equal(PlansXml(withNs: true), entry.Xml);
        Assert.Equal(entry.EntryId.ToString("D"), meta.GetProperty("journalEntryId").GetString());
        Assert.False(cache.TryGet(PlansPageId, DateTimeOffset.UnixEpoch, out _));
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Section_NoBasis_IsOk_WithNoBasis_AndTheReadDateSent()
    {
        var root = Ok(await Tool().DeleteHierarchyObject(PlansId, null, Ct));

        Assert.Equal([ToolWarnings.NoBasis], Codes(root));
        Assert.Equal(SectionDate, Assert.Single(gateway.DeleteHierarchyCalls).ExpectedLastModified);
    }

    [Fact]
    public async Task Section_TheCallersDateGoesToCom_Unchanged()
    {
        // The same instant, written differently: compared as UTC, sent as the caller wrote it (AD-17).
        Ok(await Tool().DeleteHierarchyObject(PlansId, new DeleteBasisArgument("2026-11-13T09:00:00Z"), Ct));

        Assert.Equal("2026-11-13T09:00:00Z", Assert.Single(gateway.DeleteHierarchyCalls).ExpectedLastModified);
    }

    [Fact]
    public async Task Section_StaleDate_IsStaleWrite_NoSnapshot_NoCom()
    {
        var error = Error(await Tool().DeleteHierarchyObject(PlansId, new DeleteBasisArgument("2026-11-13T08:59:59.000Z"), Ct), "StaleWrite");

        Assert.Contains(PlansId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task ProtectedSection_Unlocked_IsSoftDeleted_WithAProtectedEntry_AndNoReadGrantAsked()
    {
        var root = Ok(await Tool().DeleteHierarchyObject(ProtectedId, new DeleteBasisArgument(SectionDate), Ct));

        Assert.Equal((ProtectedId, SectionDate, false), Assert.Single(gateway.DeleteHierarchyCalls));
        var file = Assert.Single(Directory.GetFiles(Folder));
        var raw = RawEntry(file);
        Assert.DoesNotContain(VaultPageId, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"xml\"", raw, StringComparison.Ordinal);
        var entry = JournalStore.Read(file);
        Assert.Equal(GrantKeys.SectionKey(@"C:\notebooks\W\Vault.one"), entry.SectionKey);
        Assert.Contains(VaultPageId, entry.Xml, StringComparison.Ordinal);
        Assert.DoesNotContain(VaultPageId, root.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Group_IsSoftDeleted_ItsEntryHoldsItsLayout_WithoutAProtectedSectionsPages()
    {
        cache.Set(PlansPageId, DateTimeOffset.UnixEpoch, "cached text", long.MaxValue);

        Ok(await Tool().DeleteHierarchyObject(GroupId, new DeleteBasisArgument(SectionDate), Ct));

        Assert.Equal((GroupId, SectionDate, false), Assert.Single(gateway.DeleteHierarchyCalls));
        var file = Assert.Single(Directory.GetFiles(Folder));
        var entry = JournalStore.Read(file);
        Assert.Equal(JournalEntry.HierarchyKind, entry.SnapshotKind);
        Assert.Null(entry.SectionKey); // a group's entry isn't protected: it never holds a protected section's pages
        Assert.Equal(NotebookId, entry.ParentId);
        Assert.Equal(string.Empty, entry.ParentNamePath);
        var layout = XDocument.Parse(entry.Xml!).Root!;
        Assert.Equal([PlansId, InnerVaultId], layout.Descendants(OneNoteXml.One + "Section").Select(s => (string?)s.Attribute("ID")));
        Assert.Equal([PlansPageId], layout.Descendants(OneNoteXml.One + "Page").Select(p => (string?)p.Attribute("ID")));
        Assert.DoesNotContain(InnerVaultPageId, RawEntry(file), StringComparison.Ordinal);
        Assert.False(cache.TryGet(PlansPageId, DateTimeOffset.UnixEpoch, out _));
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task LockedSection_IsSectionLocked()
    {
        Error(await Tool().DeleteHierarchyObject(LockedId, null, Ct), "SectionLocked");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task GroupHoldingALockedSection_IsSectionLocked_GivingACountOnly()
    {
        var error = Error(await Tool().DeleteHierarchyObject(SealedGroupId, null, Ct), "SectionLocked");

        Assert.Contains("1 locked section(s)", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SealedId, error.GetRawText(), StringComparison.Ordinal);
        await AssertRefusedAsync();
    }

    // --- Refusals ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Notebook_IsNotSupported_WithAd7sUserAction_NoGrantDialog_NoLock_NoCom()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        var prompt = new ScriptedGrantPrompt(GrantPromptStatus.Accepted, "W");

        var error = Error(await RunWithGrantPrompt(prompt, NotebookId, null), "NotSupported");

        Assert.Equal(SoftDeleteUseCase.NotebookUserAction, error.GetProperty("userAction").GetString());
        Assert.Equal(0, prompt.Asked);
        Assert.Empty(holds);
        await AssertRefusedAsync();
    }

    [Theory]
    [InlineData(RecycleBinId)]
    [InlineData(DeletedPagesId)]
    [InlineData(BinnedPageId)]
    [InlineData(BinnedSectionId)]
    public async Task TheRecycleBin_OrAnythingInIt_IsObjectNotFound(string id)
    {
        Error(await Tool().DeleteHierarchyObject(id, null, Ct), "ObjectNotFound");

        await AssertRefusedAsync();
    }

    [Fact]
    public async Task AnUnknownId_IsObjectNotFound_BeforeTheLock()
    {
        Error(await Tool().DeleteHierarchyObject("{00000000-0000-4000-8000-00000000DEAD}{1}{B0}", null, Ct), "ObjectNotFound");

        Assert.Empty(holds);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task EmptyObjectId_IsInvalidArgument_BeforeAnyRead()
    {
        Error(await Tool().DeleteHierarchyObject(string.Empty, null, Ct), "InvalidArgument");

        Assert.Empty(gateway.Calls);
        Assert.Empty(holds);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task ABasisWithElementHashes_IsInvalidArgument_BeforeAnyRead()
    {
        // The tool's schema closes basis to {lastModifiedTime?, pageHash?}; the use case refuses it too.
        var basis = new PageWriteBasis(ReadBasis.LastModifiedTime, ReadBasis.PageHash, ReadBasis.ElementHashes);

        var ex = await Assert.ThrowsAsync<OneNoteException>(() => UseCase().ExecuteAsync(new ToolCallContext("c"), PageId, basis, Ct));

        Assert.Equal(ErrorCode.InvalidArgument, ex.Code);
        Assert.Contains("elementHashes", ex.Message, StringComparison.Ordinal);
        Assert.Empty(gateway.Calls);
        Assert.Empty(holds);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task APageHashForASection_IsInvalidArgument_BeforeTheLock()
    {
        Error(await Tool().DeleteHierarchyObject(PlansId, new DeleteBasisArgument(SectionDate, new string('0', 64)), Ct), "InvalidArgument");

        Assert.Empty(holds);
        await AssertRefusedAsync();
    }

    // --- Grants --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ungranted_ClientWithoutElicitation_IsWriteNotGranted_NoLock_NoCom()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");

        var error = Error(await Tool().DeleteHierarchyObject(PlansId, null, Ct), "WriteNotGranted");

        Assert.Contains(NotebookId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(holds);
        await AssertRefusedAsync();
    }

    [Fact]
    public async Task Ungranted_TheGrantDialogTypedRight_GrantsTheNotebook_ThenDeletes()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        var prompt = new ScriptedGrantPrompt(GrantPromptStatus.Accepted, "W");

        Ok(await RunWithGrantPrompt(prompt, PageId, null));

        Assert.Equal(1, prompt.Asked);
        Assert.Equal(GrantedVia.Elicitation, Store.Read().Notebooks[GrantKeys.NotebookKey(NotebookPath)].GrantedVia);
        Assert.Single(gateway.DeleteHierarchyCalls);
    }

    [Fact]
    public async Task Ungranted_TheGrantDialogDeclined_IsWriteNotGranted_NoLock_NoCom()
    {
        File.WriteAllText(Store.Path, """{"schemaVersion": 1}""");
        var prompt = new ScriptedGrantPrompt(GrantPromptStatus.Declined, null);

        Error(await RunWithGrantPrompt(prompt, GroupId, null), "WriteNotGranted");

        Assert.Empty(holds);
        await AssertRefusedAsync();
    }

    // --- OneNote's own refusals ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ErrorCode.StaleWrite, 0x80042010)]
    [InlineData(ErrorCode.ObjectNotFound, 0x80042014)]
    [InlineData(ErrorCode.SectionLocked, 0x8004201B)]
    [InlineData(ErrorCode.ModalDialogOpen, 0x80042030)]
    public async Task OneNoteRefusingTheDelete_KeepsItsCode_AndTheMessageNamesTheJournalEntry(ErrorCode code, uint hresult)
    {
        gateway.DeleteHierarchyFailure = new OneNoteException(code, "DeleteHierarchy failed.", unchecked((int)hresult));

        foreach (var id in new[] { PageId, PlansId })
        {
            var result = await Tool().DeleteHierarchyObject(id, null, Ct);

            var error = Error(result, code.ToString());
            var entries = Directory.GetFiles(Folder).Select(JournalStore.Read).ToList();
            var entry = Assert.Single(entries, e => e.ObjectId == id);
            Assert.Contains(entry.EntryId.ToString("D"), error.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.False(error.TryGetProperty("outcomeUnknown", out var unknown) && unknown.GetBoolean());
            Assert.False(result.StructuredContent!.Value.GetProperty("meta").TryGetProperty("journalEntryId", out _));
        }

        Assert.Equal(2, gateway.DeleteHierarchyCalls.Count);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AComTimeout_IsOutcomeUnknown_AndNeverRetried()
    {
        gateway.DeleteHierarchyFailure = new OneNoteException(ErrorCode.Timeout, "DeleteHierarchy didn't return in time.");

        var error = Error(await Tool().DeleteHierarchyObject(PlansId, null, Ct), "Timeout");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Single(gateway.DeleteHierarchyCalls);
        await AssertLockFreeAsync();
    }

    // --- Helpers -------------------------------------------------------------------------------------------------------

    private void Self(string id, string xml) => gateway.Hierarchies[(id, HierarchyScope.Self)] = "<?xml version=\"1.0\"?>\n" + xml;

    private void Pages(string id, string xml) => gateway.Hierarchies[(id, HierarchyScope.Pages)] = xml;

    private static string Section(string id, string name, string extra, string folder = "", string? inner = null) =>
        inner is null
            ? $"<one:Section {Ns} name=\"{name}\" ID=\"{id}\" path=\"C:\\notebooks\\W\\{folder}{name}.one\" {extra} />"
            : $"<one:Section {Ns} name=\"{name}\" ID=\"{id}\" path=\"C:\\notebooks\\W\\{folder}{name}.one\" {extra}>{inner}</one:Section>";

    private static string Group(string id, string name, string extra, string? inner = null) =>
        inner is null
            ? $"<one:SectionGroup {Ns} name=\"{name}\" ID=\"{id}\" path=\"C:\\notebooks\\W\\{name}\" {extra} />"
            : $"<one:SectionGroup {Ns} name=\"{name}\" ID=\"{id}\" path=\"C:\\notebooks\\W\\{name}\" {extra}>{inner}</one:SectionGroup>";

    private static string PlansXml(bool withNs) =>
        $"<one:Section {(withNs ? Ns + " " : string.Empty)}name=\"Plans\" ID=\"{PlansId}\" path=\"C:\\notebooks\\W\\Archive\\Plans.one\" lastModifiedTime=\"{SectionDate}\"><one:Page ID=\"{PlansPageId}\" name=\"Plan\" lastModifiedTime=\"{SectionDate}\" /></one:Section>";

    private static string InnerVaultXml() =>
        $"<one:Section name=\"Inner vault\" ID=\"{InnerVaultId}\" path=\"C:\\notebooks\\W\\Archive\\Inner vault.one\" encrypted=\"true\"><one:Page ID=\"{InnerVaultPageId}\" name=\"Secret plan\" /></one:Section>";

    private void OpenSectionPages() =>
        Pages(SectionId, $"<one:Section {Ns} name=\"Open\" ID=\"{SectionId}\" path=\"C:\\notebooks\\W\\Open.one\" lastModifiedTime=\"{SectionDate}\">{openPages}</one:Section>");

    private void NotebookChildren(bool withBin) =>
        gateway.Hierarchies[(NotebookId, HierarchyScope.Children)] =
            $"<one:Notebook {Ns} name=\"W\" nickname=\"W\" ID=\"{NotebookId}\" path=\"{NotebookPath}\">"
            + $"<one:Section name=\"Open\" ID=\"{SectionId}\" path=\"C:\\notebooks\\W\\Open.one\" />"
            + $"<one:SectionGroup name=\"Archive\" ID=\"{GroupId}\" path=\"C:\\notebooks\\W\\Archive\" />"
            + (withBin ? $"<one:SectionGroup name=\"OneNote_RecycleBin\" ID=\"{RecycleBinId}\" path=\"C:\\notebooks\\W\\OneNote_RecycleBin\" isRecycleBin=\"true\" />" : string.Empty)
            + "</one:Notebook>";

    /// <summary>The recycle bin at <c>Pages</c> scope: Deleted Pages holding <see cref="deletedPages"/>, and a recycled section with a page of its own.</summary>
    private void Bin() =>
        Pages(RecycleBinId,
            $"<one:SectionGroup {Ns} name=\"OneNote_RecycleBin\" ID=\"{RecycleBinId}\" path=\"C:\\notebooks\\W\\OneNote_RecycleBin\" isRecycleBin=\"true\">"
            + $"<one:Section name=\"Deleted Pages\" ID=\"{DeletedPagesId}\" isInRecycleBin=\"true\" isDeletedPages=\"true\">"
            + string.Concat(deletedPages.Select(p => $"<one:Page ID=\"{p.Id}\" name=\"{p.Name}\" isInRecycleBin=\"true\" />"))
            + "</one:Section>"
            + $"<one:Section name=\"Gone\" ID=\"{BinnedSectionId}\" isInRecycleBin=\"true\"><one:Page ID=\"{RecycledSectionPageId}\" name=\"{Title}\" /></one:Section>"
            + "</one:SectionGroup>");

    private void Grant() =>
        new SettingsWriter(Store, clock).SaveWriteGrant(
            new NotebookGrant(GrantKeys.NotebookKey(NotebookPath), "W", NotebookId, Write: true, PermanentDelete: false, clock.GetUtcNow(), GrantedVia.Cli));

    /// <summary>The synthetic page's basis as a delete takes it: the page-XML date and the pageHash.</summary>
    private static DeleteBasisArgument ReadBasisArgument() => new(ReadBasis.LastModifiedTime, ReadBasis.PageHash);

    private static PageBasis ReadBasis => PageBasis.From(Read);

    private SoftDeleteUseCase UseCase()
    {
        var services = new PageWriteServices(gateway, new WriteAccess(gateway, Store.Read), readAccess, new PageSnapshot(gateway, new JournalStore(Store.Read), clock));
        return new SoftDeleteUseCase(
            services,
            new HierarchySnapshot(gateway, new JournalStore(Store.Read), clock),
            new WritePipeline(writeLock, gateway, cache, Store.Read, TimeProvider.System, null, new WriteGrantDialog(gateway, new SettingsWriter(Store, clock), clock)));
    }

    private static ToolRunner Runner() => new(NullLogger<ToolRunner>.Instance);

    private DeleteHierarchyObjectTool Tool() => new(Runner(), UseCase());

    private Task<CallToolResult> RunWithGrantPrompt(IGrantPrompt prompt, string objectId, PageWriteBasis? basis)
    {
        var open = $"<?xml version=\"1.0\"?>\n<one:Notebooks {Ns}><one:Notebook name=\"W\" nickname=\"W\" ID=\"{NotebookId}\" path=\"{NotebookPath}\" /></one:Notebooks>";
        gateway.Hierarchies[(string.Empty, HierarchyScope.Notebooks)] = open;
        gateway.Hierarchies[(string.Empty, HierarchyScope.Sections)] = open;
        var useCase = UseCase();
        return Runner().RunAsync(DeleteHierarchyObjectTool.Name, useCase, (context, ct) => useCase.ExecuteAsync(context, objectId, basis, ct), Ct, prompt);
    }

    private async Task AssertRefusedAsync()
    {
        Assert.Empty(gateway.DeleteHierarchyCalls);
        Assert.Empty(gateway.DeleteCalls);
        Assert.Empty(gateway.UpdateCalls);
        Assert.False(Directory.Exists(Folder) && Directory.EnumerateFileSystemEntries(Folder).Any());
        await AssertLockFreeAsync();
    }

    private async Task AssertLockFreeAsync()
    {
        using var probe = await writeLock.AcquireAsync("free?", TimeSpan.FromMilliseconds(500), Ct);
    }

    private static string RawEntry(string file)
    {
        using var stream = File.OpenRead(file);
        using var gzip = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
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

    private sealed class ScriptedGrantPrompt(GrantPromptStatus status, string? typed) : IGrantPrompt
    {
        public int Asked { get; private set; }

        public Task<GrantPromptResult> AskAsync(GrantQuestion question, CancellationToken ct) =>
            throw new InvalidOperationException("A delete never asks for a read grant.");

        public Task<GrantPromptResult> AskWriteAsync(WriteGrantQuestion question, CancellationToken ct)
        {
            Asked++;
            return Task.FromResult(new GrantPromptResult(status, typed));
        }
    }
}
