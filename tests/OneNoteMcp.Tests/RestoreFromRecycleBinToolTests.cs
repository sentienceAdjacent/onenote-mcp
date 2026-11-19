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

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 3.5: <c>restore_from_recycle_bin</c> (<see cref="RestoreFromRecycleBinWrite"/>), one test or theory per row of
/// the plan's I/O matrix, against <see cref="FakeGateway"/> scripted with three synthetic notebooks and soft-delete entries
/// written straight into a temporary journal. A (local): sections Plans (three pages), Ideas (a page and its subpage),
/// Vault (protected), Locked (protected, locked), group Archive (section Old); its recycle bin holds Deleted Pages (pages
/// Lost, Named, Twin ×2, Orphan, Stranded, Sub) and the sections Gone (with a page), Rooty, Reopened, plans (a clash),
/// Grouped, Secret (protected, locked) and Lonely (protected, no entry), and a binned group. Other (local): section
/// Inbox. Cloud (a OneDrive URL): its bin holds section Drift. The fake applies a restore as OneNote does (spike 3.1
/// Implication 6): a page gets a new ID, a section keeps its own.
/// </summary>
public sealed class RestoreFromRecycleBinToolTests : IDisposable
{
    private const string Lmt = "2026-11-17T10:00:00.000Z";

    private const string AId = "{A0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string APath = @"C:\notebooks\A";
    private const string PlansId = "{A1000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string P1 = "{A1000000-0000-4000-8000-000000000001}{1}{E1}";
    private const string P2 = "{A1000000-0000-4000-8000-000000000001}{1}{E2}";
    private const string P3 = "{A1000000-0000-4000-8000-000000000001}{1}{E3}";
    private const string IdeasId = "{A2000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string I1 = "{A2000000-0000-4000-8000-000000000001}{1}{E1}";
    private const string I1a = "{A2000000-0000-4000-8000-000000000001}{1}{E2}";
    private const string VaultId = "{A3000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string LockedId = "{A4000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string ArchiveId = "{A5000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string OldId = "{A6000000-0000-4000-8000-000000000001}{1}{B0}";

    private const string BinId = "{AB000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string BinPath = @"C:\notebooks\A\OneNote_RecycleBin";
    private const string DpId = "{AC000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string LostId = "{AC000000-0000-4000-8000-000000000001}{1}{E1}";
    private const string NamedId = "{AC000000-0000-4000-8000-000000000001}{1}{E2}";
    private const string TwinAId = "{AC000000-0000-4000-8000-000000000001}{1}{E3}";
    private const string TwinBId = "{AC000000-0000-4000-8000-000000000001}{1}{E4}";
    private const string OrphanId = "{AC000000-0000-4000-8000-000000000001}{1}{E5}";
    private const string StrandedId = "{AC000000-0000-4000-8000-000000000001}{1}{E6}";
    private const string SubId = "{AC000000-0000-4000-8000-000000000001}{1}{E7}";
    private const string GoneId = "{AD000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string GonePageId = "{AD000000-0000-4000-8000-000000000001}{1}{E1}";
    private const string RootyId = "{AE000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string ReopenedId = "{AF000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string ClashId = "{B0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string GroupedId = "{B1000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string SecretId = "{B2000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string LonelyId = "{B3000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string BinGroupId = "{B4000000-0000-4000-8000-000000000001}{1}{B0}";

    private const string OtherId = "{C0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string OtherPath = @"C:\notebooks\Other";
    private const string InboxId = "{C1000000-0000-4000-8000-000000000001}{1}{B0}";

    private const string CloudId = "{D0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string CloudPath = "https://d.docs.live.net/0123abcd/Cloud/";
    private const string CloudBinId = "{D1000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string DriftId = "{D2000000-0000-4000-8000-000000000001}{1}{B0}";

    /// <summary>The recycled page Lost's name appears nowhere else, so a test can show it never reaches a message.</summary>
    private const string LostName = "Lost Zq7";

    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";

    private static readonly XNamespace One = OneNoteXml.One;

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly FakeGateway gateway = new();
    private readonly ConcurrentQueue<WriteLockHeld> holds = new();
    private readonly WriteLock writeLock;
    private readonly ReadAccess readAccess;
    private readonly PageTextCache cache = PageTextCache.WithDefaults();
    private readonly List<TimeSpan> settles = [];

    private readonly Dictionary<string, (string Element, string Attributes)> nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> children = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> entries = new(StringComparer.Ordinal);
    private int newPages;
    private int loads;

    public RestoreFromRecycleBinToolTests()
    {
        writeLock = new WriteLock($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", holds.Enqueue);
        readAccess = new ReadAccess(gateway, Store.Read, new SettingsWriter(Store, clock), clock);
        Directory.CreateDirectory(temp.Combine("cfg"));

        Node(AId, "Notebook", "A", APath, null, "nickname=\"A\"");
        Node(PlansId, "Section", "Plans", APath + @"\Plans.one", AId);
        Page(P1, "One", PlansId);
        Page(P2, "Two", PlansId);
        Page(P3, "Three", PlansId);
        Node(IdeasId, "Section", "Ideas", APath + @"\Ideas.one", AId);
        Page(I1, "Idea", IdeasId);
        Page(I1a, "Idea detail", IdeasId, level: 2);
        Node(VaultId, "Section", "Vault", APath + @"\Vault.one", AId, "encrypted=\"true\"");
        Node(LockedId, "Section", "Locked", APath + @"\Locked.one", AId, "encrypted=\"true\" locked=\"true\"");
        Node(ArchiveId, "SectionGroup", "Archive", APath + @"\Archive", AId);
        Node(OldId, "Section", "Old", APath + @"\Archive\Old.one", ArchiveId);

        Node(BinId, "SectionGroup", "OneNote_RecycleBin", BinPath, AId, "isRecycleBin=\"true\"");
        Node(DpId, "Section", "Deleted Pages", BinPath + @"\OneNote_DeletedPages.one", BinId, "isInRecycleBin=\"true\" isDeletedPages=\"true\"");
        Page(LostId, LostName, DpId, binned: true);
        Page(NamedId, "Named", DpId, binned: true);
        Page(TwinAId, "Twin", DpId, binned: true);
        Page(TwinBId, "Twin", DpId, binned: true);
        Page(OrphanId, "Orphan", DpId, binned: true);
        Page(StrandedId, "Stranded", DpId, binned: true);
        Page(SubId, "Sub", DpId, level: 2, binned: true);
        Node(GoneId, "Section", "Gone", BinPath + @"\Gone.one", BinId, "isInRecycleBin=\"true\"");
        Page(GonePageId, "Inside", GoneId, binned: true);
        Node(RootyId, "Section", "Rooty", BinPath + @"\Rooty.one", BinId, "isInRecycleBin=\"true\"");
        Node(ReopenedId, "Section", "Reopened", BinPath + @"\Reopened.one", BinId, "isInRecycleBin=\"true\"");
        Node(ClashId, "Section", "plans", BinPath + @"\plans.one", BinId, "isInRecycleBin=\"true\"");
        Node(GroupedId, "Section", "Grouped", BinPath + @"\Grouped.one", BinId, "isInRecycleBin=\"true\"");
        Node(SecretId, "Section", "Secret", BinPath + @"\Secret.one", BinId, "isInRecycleBin=\"true\" encrypted=\"true\" locked=\"true\"");
        Node(LonelyId, "Section", "Lonely", BinPath + @"\Lonely.one", BinId, "isInRecycleBin=\"true\" encrypted=\"true\" locked=\"true\"");
        Node(BinGroupId, "SectionGroup", "Binned group", BinPath + @"\Binned group", BinId, "isInRecycleBin=\"true\"");

        Node(OtherId, "Notebook", "Other", OtherPath, null, "nickname=\"Elsewhere\"");
        Node(InboxId, "Section", "Inbox", OtherPath + @"\Inbox.one", OtherId);

        Node(CloudId, "Notebook", "Cloud", CloudPath, null, "nickname=\"Cloud\"");
        Node(CloudBinId, "SectionGroup", "OneNote_RecycleBin", CloudPath + "OneNote_RecycleBin", CloudId, "isRecycleBin=\"true\"");
        Node(DriftId, "Section", "Drift", CloudPath + "OneNote_RecycleBin/Drift.one", CloudBinId, "isInRecycleBin=\"true\"");
        Publish();

        gateway.Hierarchies[(string.Empty, HierarchyScope.Sections)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebooks {Ns}><one:Notebook {Attributes(AId)} /><one:Notebook {Attributes(OtherId)} /><one:Notebook {Attributes(CloudId)} /></one:Notebooks>";
        ApplyRestores();

        // The soft-delete entries delete_hierarchy_object would have written (AD-8).
        PageEntry("lost", "{OLD-LOST}", PlansId, "Plans", LostName, recycleBinId: LostId);
        PageEntry("named", "{OLD-NAMED}", PlansId, "Plans", "Named", recycleBinId: "{STALE}");
        PageEntry("twinA", "{OLD-TWIN-A}", PlansId, "Plans", "Twin");
        PageEntry("twinB", "{OLD-TWIN-B}", IdeasId, "Ideas", "Twin");
        PageEntry("stranded", "{OLD-STRANDED}", GoneId, "Gone", "Stranded");
        PageEntry("sub", "{OLD-SUB}", IdeasId, "Ideas", "Sub", recycleBinId: SubId);
        SectionEntry("gone", GoneId, ArchiveId, "Archive", "Gone", APath + @"\Archive\Gone.one");
        SectionEntry("rooty", RootyId, "{STALE-PARENT}", string.Empty, "Rooty", APath + @"\Rooty.one");
        SectionEntry("reopened", "{OLD-REOPENED}", "{STALE-PARENT}", "Archive", "Reopened", APath + @"\Archive\Reopened.one");
        SectionEntry("clash", ClashId, AId, string.Empty, "plans", APath + @"\plans.one");
        GroupEntry("group", "{OLD-GROUP}", AId, "Grouped");
        ProtectedEntry("secret", "{OLD-SECRET}", "{STALE-PARENT}", APath + @"\Archive\Secret.one");
        SectionEntry("drift", DriftId, CloudId, string.Empty, "Drift", CloudPath + "Drift.one", notebook: CloudPath);
        SectionEntry("elsewhere", "{OTHER-NOTEBOOK}", InboxId, string.Empty, "Inbox", OtherPath + @"\x.one", notebook: OtherPath);
        Write("notsoftdelete", Base("{OLD-LOST}", PlansId, "Plans", APath) with { Operation = "update_page_content", SnapshotKind = JournalEntry.PageKind, Title = LostName });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SettingsStore Store => new(Path.Combine(temp.Combine("cfg"), "settings.json"), temp.Combine("journal"));

    private string JournalFolder => temp.Combine("journal");

    public void Dispose() => temp.Dispose();

    // --- Pages that come back ---------------------------------------------------------------------------------------

    [Fact]
    public async Task APage_ByItsRecycleBinId_ComesBackLastInItsSection_AtLevelOne_UnderANewId_WithAJournalEntry()
    {
        Grant();
        cache.Set(LostId, DateTimeOffset.UnixEpoch, "cached text", long.MaxValue);
        var before = EntryCount();

        var root = Ok(await Tool().RestoreFromRecycleBin(LostId, null, null, null, Ct));

        var sent = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal(One + "Section", sent.Name);
        Assert.Equal(PlansId, (string?)sent.Attribute("ID"));
        Assert.Equal([P1, P2, P3, LostId], sent.Elements(One + "Page").Select(e => (string?)e.Attribute("ID")));
        Assert.All(sent.Elements(One + "Page"), e => Assert.Equal("1", (string?)e.Attribute("pageLevel")));
        Assert.Empty(SchemaValidation.Problems(gateway.UpdateHierarchyCalls[0]));

        var meta = root.GetProperty("meta");
        var newId = meta.GetProperty("objectId").GetString();
        Assert.Equal(NewPageId(1), newId);
        Assert.Equal(PlansId, meta.GetProperty("parentId").GetString());
        var entry = Entry(meta.GetProperty("journalEntryId").GetString()!);
        Assert.Equal(PlansId, entry.ObjectId);
        Assert.Equal(RestoreFromRecycleBinWrite.OperationName, entry.Operation);
        Assert.Equal(before + 1, EntryCount());
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
        Assert.DoesNotContain(LostId, children[DpId]);
        Assert.False(cache.TryGet(LostId, DateTimeOffset.UnixEpoch, out _));
        Assert.DoesNotContain(LostName, root.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(settles);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task APage_MatchedByName_ComesBack_WithRestoredByName_NamingTheEntryAndTheParent()
    {
        Grant();

        var root = Ok(await Tool().RestoreFromRecycleBin(NamedId, null, null, null, Ct));

        Assert.Equal(PlansId, root.GetProperty("meta").GetProperty("parentId").GetString());
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.RestoredByName, warning.GetProperty("code").GetString());
        var message = warning.GetProperty("message").GetString()!;
        Assert.Contains(entries["named"].ToString("D"), message, StringComparison.Ordinal);
        Assert.Contains(PlansId, message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecycledSubpage_ComesBackAtLevelOne()
    {
        Grant();

        Ok(await Tool().RestoreFromRecycleBin(SubId, null, null, null, Ct));

        var sent = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal(IdeasId, (string?)sent.Attribute("ID"));
        Assert.Equal(["1", "2", "1"], sent.Elements(One + "Page").Select(e => (string?)e.Attribute("pageLevel")));
    }

    [Fact]
    public async Task TwoSameNamedPages_WithoutEntryId_SendNothing_AndListBothCandidates()
    {
        Grant();
        var before = EntryCount();

        var error = Error(await Tool().RestoreFromRecycleBin(TwinAId, null, null, null, Ct), "InvalidArgument");

        var message = error.GetProperty("message").GetString()!;
        Assert.Contains(entries["twinA"].ToString("D"), message, StringComparison.Ordinal);
        Assert.Contains(entries["twinB"].ToString("D"), message, StringComparison.Ordinal);
        Assert.Contains(PlansId, message, StringComparison.Ordinal);
        Assert.Contains(IdeasId, message, StringComparison.Ordinal);
        Assert.Matches(@"deleted \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ", message);
        Assert.DoesNotContain("Twin", message, StringComparison.Ordinal);
        Assert.Equal(RestoreFromRecycleBinWrite.CandidatesUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Equal(before, EntryCount());
    }

    [Fact]
    public async Task ARestore_RecordsTheSoftDeleteEntryItUsed_AndALaterChoiceMarksItAlreadyRestored()
    {
        // Backlog 16 (Phill, 2026-11-19): the used entry stays listed, marked; the unused one isn't.
        Grant();
        var restored = Ok(await Tool().RestoreFromRecycleBin(TwinAId, null, entries["twinA"].ToString("D"), null, Ct));
        var restoreEntry = Entry(restored.GetProperty("meta").GetProperty("journalEntryId").GetString()!);
        Assert.Equal(entries["twinA"], restoreEntry.RestoredFrom);

        // The other twin, still in the bin, matches both entries by name.
        var error = Error(await Tool().RestoreFromRecycleBin(TwinBId, null, null, null, Ct), "InvalidArgument");

        var message = error.GetProperty("message").GetString()!;
        Assert.Contains($"entry {entries["twinA"]:D}, deleted ", message, StringComparison.Ordinal);
        Assert.Matches($@"entry {entries["twinA"]:D}, deleted [^;]*, already restored", message);
        Assert.DoesNotMatch($@"entry {entries["twinB"]:D}, deleted [^;]*, already restored", message);
    }

    [Fact]
    public async Task AFailedRestore_RecordsNoSource_SoItsEntryIsNeverMarkedAlreadyRestored()
    {
        // Review of backlog 16: the link is written only once the restore happened.
        Grant();
        gateway.UpdateHierarchyFailure = Com.HResultMap.ToException("UpdateHierarchy", new System.Runtime.InteropServices.COMException("refused", unchecked((int)0x80042004)));
        var before = Directory.GetFiles(JournalFolder).Where(f => JournalFiles.IsEntry(Path.GetFileName(f))).ToHashSet();

        Assert.True((await Tool().RestoreFromRecycleBin(TwinAId, null, entries["twinA"].ToString("D"), null, Ct)).IsError);

        var written = Directory.GetFiles(JournalFolder).Where(f => JournalFiles.IsEntry(Path.GetFileName(f)) && !before.Contains(f)).Select(JournalStore.Read).ToList();
        Assert.All(written, e => Assert.Null(e.RestoredFrom));
        Assert.Empty(SoftDeleteEntries.Used(SoftDeleteEntries.Load(JournalFolder)));
    }

    [Fact]
    public async Task ARestoreWithNoJournalEntry_RecordsNoSource()
    {
        Grant();

        var restored = Ok(await Tool().RestoreFromRecycleBin(OrphanId, PlansId, null, null, Ct));

        Assert.Null(Entry(restored.GetProperty("meta").GetProperty("journalEntryId").GetString()!).RestoredFrom);
    }

    [Theory]
    [InlineData("twinA", PlansId)]
    [InlineData("twinB", IdeasId)]
    public async Task AChosenEntry_PutsThePageBackUnderThatEntrysParent(string entry, string parent)
    {
        Grant();

        var root = Ok(await Tool().RestoreFromRecycleBin(TwinAId, null, entries[entry].ToString("D"), null, Ct));

        Assert.Equal(parent, root.GetProperty("meta").GetProperty("parentId").GetString());
        Assert.Equal(parent, (string?)XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls)).Attribute("ID"));
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
    }

    [Theory]
    [InlineData(LostId, "elsewhere")]
    [InlineData(GoneId, "lost")]
    [InlineData(LostId, "gone")]
    [InlineData(LostId, "group")]
    [InlineData(LostId, "notsoftdelete")]
    public async Task AWrongEntryId_IsInvalidArgument(string objectId, string entry)
    {
        Grant();

        var error = Error(await Tool().RestoreFromRecycleBin(objectId, null, entries[entry].ToString("D"), null, Ct), "InvalidArgument");

        Assert.Equal(RestoreFromRecycleBinWrite.EntryIdUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("{12345")]
    public async Task AnEntryIdThatIsntAGuid_IsInvalidArgument_BeforeAnyRead(string entryId)
    {
        var error = Error(await Tool().RestoreFromRecycleBin(LostId, null, entryId, null, Ct), "InvalidArgument");

        Assert.Equal(RestoreFromRecycleBinWrite.EntryIdUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task ACallersTarget_InTheSameNotebook_Wins()
    {
        Grant();

        var root = Ok(await Tool().RestoreFromRecycleBin(LostId, IdeasId, null, null, Ct));

        Assert.Equal(IdeasId, root.GetProperty("meta").GetProperty("parentId").GetString());
        Assert.Equal(IdeasId, (string?)XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls)).Attribute("ID"));
    }

    [Fact]
    public async Task ACallersTarget_InAnotherNotebook_TakesThePageThere_AskingForBothGrants()
    {
        var prompt = new FakePrompt(Typed("A"), Typed("Elsewhere"));

        var root = Ok(await RestoreWithPrompt(prompt, LostId, InboxId));

        Assert.Equal(["write", "write"], prompt.Order);
        Assert.Equal(InboxId, root.GetProperty("meta").GetProperty("parentId").GetString());
        Assert.Contains(root.GetProperty("meta").GetProperty("objectId").GetString(), children[InboxId]);
    }

    [Fact]
    public async Task ACallersTarget_WithNoEntry_RestoresAnOrphan()
    {
        Grant();

        var root = Ok(await Tool().RestoreFromRecycleBin(OrphanId, PlansId, null, null, Ct));

        Assert.Equal(PlansId, root.GetProperty("meta").GetProperty("parentId").GetString());
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task APageAtPosition2_InAThreePageSection_LandsSecond_AtLevelOne()
    {
        Grant();

        var root = Ok(await Tool().RestoreFromRecycleBin(LostId, null, null, 2, Ct));

        var sent = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal([P1, LostId, P2, P3], sent.Elements(One + "Page").Select(e => (string?)e.Attribute("ID")));
        Assert.Equal(NewPageId(1), children[PlansId][1]);
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
    }

    [Theory]
    [InlineData(LostId, null, 0)]
    [InlineData(LostId, null, 5)]
    [InlineData(LostId, IdeasId, 2)]
    public async Task ABadPosition_IsInvalidArgument_WithNoPageCount(string objectId, string? parentId, int position)
    {
        Grant();

        var error = Error(await Tool().RestoreFromRecycleBin(objectId, parentId, null, position, Ct), "InvalidArgument");

        Assert.DoesNotContain(" 3 ", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task APosition_InAProtectedTarget_WithoutAReadGrant_IsReadNotGranted_BeforeTheWriteGrantDialog()
    {
        var prompt = new FakePrompt(Typed("A"));

        var error = Error(await RestoreWithPrompt(prompt, LostId, VaultId, position: 1), "ReadNotGranted");

        Assert.Equal(MovePageWrite.ProtectedPositionUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(prompt.Order);
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task APosition_InATargetProtectedByPath_WithoutAReadGrant_IsReadNotGranted()
    {
        // Entry 15 (story 3.15): a section under an export root counts as protected without the marker.
        Grant();
        TestSettings.SetExportRoots(Store.Path, APath);

        var error = Error(await Tool().RestoreFromRecycleBin(LostId, PlansId, null, 1, Ct), "ReadNotGranted");

        Assert.Contains("export root or the backups folder", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task ASectionProtectedByPath_IsRestoredOnlyAsAProtectedOne_NeverSentElsewhere()
    {
        // Entry 15: under an export root the recycled section counts as protected. Its entry was written without a
        // SectionKey (it wasn't protected then), so its own place can't be proven: the protected orphan answer, and
        // nothing is sent, whatever parentId says.
        Grant();
        TestSettings.SetExportRoots(Store.Path, APath);

        Error(await Tool().RestoreFromRecycleBin(GoneId, AId, null, null, Ct), "ObjectNotFound");
        Error(await Tool().RestoreFromRecycleBin(GoneId, null, null, null, Ct), "ObjectNotFound");

        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task IntoAProtectedTarget_WithoutAPosition_NeedsOnlyTheWriteGrant()
    {
        Grant();

        var root = Ok(await Tool().RestoreFromRecycleBin(LostId, VaultId, null, null, Ct));

        Assert.Equal(VaultId, root.GetProperty("meta").GetProperty("parentId").GetString());
        var entry = Entry(root.GetProperty("meta").GetProperty("journalEntryId").GetString()!);
        Assert.Equal(GrantKeys.SectionKey(APath + @"\Vault.one"), entry.SectionKey);
    }

    [Fact]
    public async Task APosition_InAProtectedTarget_WithAReadGrant_Lands()
    {
        Grant();
        new SettingsWriter(Store, clock).SaveReadGrant(new ProtectedSectionGrant(
            GrantKeys.SectionKey(APath + @"\Vault.one"), "Vault", GrantKeys.NotebookKey(APath), VaultId, clock.GetUtcNow(), GrantedVia.Cli));

        Ok(await Tool().RestoreFromRecycleBin(LostId, VaultId, null, 1, Ct));

        Assert.Single(gateway.UpdateHierarchyCalls);
    }

    // --- Sections that come back -------------------------------------------------------------------------------------

    [Fact]
    public async Task ASection_ComesBackUnderItsParent_WithItsId_LastAmongTheSections()
    {
        Grant();

        var root = Ok(await Tool().RestoreFromRecycleBin(GoneId, null, null, null, Ct));

        var sent = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal(One + "SectionGroup", sent.Name);
        Assert.Equal(ArchiveId, (string?)sent.Attribute("ID"));
        Assert.Equal([OldId, GoneId], sent.Elements(One + "Section").Select(e => (string?)e.Attribute("ID")));
        Assert.Empty(SchemaValidation.Problems(gateway.UpdateHierarchyCalls[0]));
        var meta = root.GetProperty("meta");
        Assert.Equal(GoneId, meta.GetProperty("objectId").GetString());
        Assert.Equal(ArchiveId, meta.GetProperty("parentId").GetString());
        Assert.Equal(ArchiveId, Entry(meta.GetProperty("journalEntryId").GetString()!).ObjectId);
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
        Assert.DoesNotContain(GoneId, children[BinId]);
        Assert.Empty(settles);
    }

    [Fact]
    public async Task ASectionAtPosition1_LandsFirst()
    {
        Grant();

        Ok(await Tool().RestoreFromRecycleBin(GoneId, null, null, 1, Ct));

        var sent = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal([GoneId, OldId], sent.Elements(One + "Section").Select(e => (string?)e.Attribute("ID")));
    }

    [Fact]
    public async Task ASectionPositionPastTheEnd_IsInvalidArgument()
    {
        Grant();

        var error = Error(await Tool().RestoreFromRecycleBin(GoneId, null, null, 3, Ct), "InvalidArgument");

        Assert.Equal(RestoreFromRecycleBinWrite.SectionPositionUserAction, error.GetProperty("userAction").GetString());
    }

    [Fact]
    public async Task ASectionAfterAReopen_MatchesByName_AndGoesBackByItsGroupPath()
    {
        Grant();

        var root = Ok(await Tool().RestoreFromRecycleBin(ReopenedId, null, null, null, Ct));

        Assert.Equal(ArchiveId, root.GetProperty("meta").GetProperty("parentId").GetString());
        Assert.Equal(ReopenedId, root.GetProperty("meta").GetProperty("objectId").GetString());
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.RestoredByName, warning.GetProperty("code").GetString());
        Assert.Contains(entries["reopened"].ToString("D"), warning.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASectionMatchedByName_NeverInheritsTheReadGrantAtItsEntrysFormerKey(bool byEntryId)
    {
        // 3.5 security review: only an entry matched by the item's own ID proves the landing key was this section's. A name
        // match (or an entryId that picks one) could be a different section, so a read grant there must still refuse it.
        Grant();
        ReadGrantAt(APath + @"\Archive\Reopened.one");

        var error = Error(
            await Tool().RestoreFromRecycleBin(ReopenedId, null, byEntryId ? entries["reopened"].ToString("D") : null, null, Ct),
            "InvalidArgument");

        Assert.Equal(RestoreFromRecycleBinWrite.GrantLocationUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task ASectionWithAnEmptyPath_AndAStaleParent_GoesBackToTheNotebookRoot_BeforeItsGroups()
    {
        Grant();

        var root = Ok(await Tool().RestoreFromRecycleBin(RootyId, null, null, null, Ct));

        Assert.Equal(AId, root.GetProperty("meta").GetProperty("parentId").GetString());
        var sent = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal(One + "Notebook", sent.Name);
        Assert.Equal([PlansId, IdeasId, VaultId, LockedId, RootyId], sent.Elements(One + "Section").Select(e => (string?)e.Attribute("ID")));
        Assert.Empty(sent.Elements(One + "SectionGroup"));
    }

    // --- Q7: a section into another notebook, restored in place and then moved ------------------------------------

    [Fact]
    public async Task ASection_IntoAnotherNotebook_IsRestoredInPlace_ThenMovedThere_WithTheRestoresAndTheMovesEntries()
    {
        Grant();
        Grant(OtherPath, OtherId);

        var root = Ok(await Tool().RestoreFromRecycleBin(GoneId, OtherId, null, 1, Ct));

        Assert.Equal(2, gateway.UpdateHierarchyCalls.Count);
        var restore = XElement.Parse(gateway.UpdateHierarchyCalls[0]);
        Assert.Equal(ArchiveId, (string?)restore.Attribute("ID"));
        Assert.Equal([OldId, GoneId], restore.Elements(One + "Section").Select(e => (string?)e.Attribute("ID")));
        var move = XElement.Parse(gateway.UpdateHierarchyCalls[1]);
        Assert.Equal(One + "Notebook", move.Name);
        Assert.Equal(OtherId, (string?)move.Attribute("ID"));
        Assert.Equal([GoneId, InboxId], move.Elements(One + "Section").Select(e => (string?)e.Attribute("ID")));

        var meta = root.GetProperty("meta");
        Assert.Equal(GoneId, meta.GetProperty("objectId").GetString());
        Assert.Equal(OtherId, meta.GetProperty("parentId").GetString());
        var ids = meta.GetProperty("journalEntryIds").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(meta.GetProperty("journalEntryId").GetString(), ids[0]);
        Assert.Equal([ArchiveId, ArchiveId, OtherId], ids.Select(id => Entry(id).ObjectId));
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
        Assert.Contains(GoneId, children[OtherId]);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ACloudSection_IntoAnotherNotebooksGroup_IsRestoredInPlace_AndTheJ1RefusalIsRestoredNotMoved()
    {
        Grant();
        Grant(CloudPath, CloudId);

        var root = Ok(await Tool().RestoreFromRecycleBin(DriftId, ArchiveId, null, null, Ct));

        var sent = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal(CloudId, (string?)sent.Attribute("ID"));
        Assert.Equal(CloudId, root.GetProperty("meta").GetProperty("parentId").GetString());
        Assert.Equal(DriftId, root.GetProperty("meta").GetProperty("objectId").GetString());
        var warnings = root.GetProperty("warnings").EnumerateArray().ToList();
        Assert.Equal([ToolWarnings.Settled, "restoredNotMoved"], warnings.Select(w => w.GetProperty("code").GetString()));
        Assert.Contains("NotSupported", warnings[1].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("move_hierarchy_object", warnings[1].GetProperty("userAction").GetString(), StringComparison.Ordinal);
        Assert.Contains(DriftId, children[CloudId]);
        Assert.DoesNotContain(DriftId, children[ArchiveId]);
    }

    [Fact]
    public async Task ASection_IntoAnotherNotebook_WhoseGrantIsDeclined_StaysRestored_WithRestoredNotMoved()
    {
        Grant();

        var root = Ok(await RestoreWithPrompt(new FakePrompt(), GoneId, OtherId));

        Assert.Single(gateway.UpdateHierarchyCalls);
        Assert.Equal(ArchiveId, root.GetProperty("meta").GetProperty("parentId").GetString());
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal("restoredNotMoved", warning.GetProperty("code").GetString());
        Assert.Contains("WriteNotGranted", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains(GoneId, children[ArchiveId]);
    }

    [Theory]
    [InlineData(ErrorCode.Timeout)]
    [InlineData(ErrorCode.Cancelled)]
    public async Task ASection_IntoAnotherNotebook_WhoseMoveOutcomeIsUnknown_SaysItMayOrMayNotHaveMoved_AndClaimsNoParent(ErrorCode code)
    {
        // Phill, 2026-11-17 (3.5 Fable finding 4, the 3.14 pattern): after an outcomeUnknown or cancelled move, the section
        // may be in either notebook, so the warning says so and meta.parentId names neither.
        Grant();
        Grant(OtherPath, OtherId);
        var calls = 0;
        gateway.OnUpdateHierarchyCall = _ =>
        {
            if (++calls == 2)
            {
                throw new OneNoteException(code, "UpdateHierarchy did not return in time.");
            }

            return Task.CompletedTask;
        };

        var root = Ok(await Tool().RestoreFromRecycleBin(GoneId, OtherId, null, null, Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal("restoredNotMoved", warning.GetProperty("code").GetString());
        Assert.Contains("may or may not have been moved", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("re-read", warning.GetProperty("userAction").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(root.GetProperty("meta").TryGetProperty("parentId", out _));
    }

    [Fact]
    public async Task ASection_IntoAnotherNotebook_WithNoOriginalParent_SendsNothing_AndAsksForAParentInItsOwnNotebook()
    {
        Grant();
        Grant(OtherPath, OtherId);

        var error = Error(await Tool().RestoreFromRecycleBin(GroupedId, OtherId, null, null, Ct), "InvalidArgument");

        Assert.Contains("no original parent", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("own notebook", error.GetProperty("userAction").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    // --- Review fixes -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AProtectedSection_WithAnotherSectionsEntryId_IsInvalidArgument()
    {
        // Review fix 1: entryId may only pick among the entries the item itself matches.
        Grant();
        ProtectedEntry("foreign", "{OLD-FOREIGN}", "{STALE-PARENT}", APath + @"\Foreign.one");

        var error = Error(await Tool().RestoreFromRecycleBin(SecretId, null, entries["foreign"].ToString("D"), null, Ct), "InvalidArgument");

        Assert.Equal(RestoreFromRecycleBinWrite.EntryIdUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task APage_WithAnEntryIdItDoesntMatch_IsInvalidArgument()
    {
        Grant();

        var error = Error(await Tool().RestoreFromRecycleBin(LostId, null, entries["twinA"].ToString("D"), null, Ct), "InvalidArgument");

        Assert.Equal(RestoreFromRecycleBinWrite.EntryIdUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task TheJournal_IsLoadedOnce_PerCall()
    {
        // Review fix 3: step 1 loads the soft-delete entries, step 3 reuses them.
        Grant();

        Ok(await Tool().RestoreFromRecycleBin(LostId, null, null, null, Ct));

        Assert.Equal(1, loads);
    }

    [Fact]
    public void TheNoTargetUserAction_AndTheToolDescription_NameTheRetentionPeriod_NotThirtyDays()
    {
        // Review fix 4: retention comes from journal.retentionDays.
        var description = typeof(RestoreFromRecycleBinTool).GetMethod(nameof(RestoreFromRecycleBinTool.RestoreFromRecycleBin))!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Single()
            .Description;
        foreach (var text in new[] { RestoreFromRecycleBinWrite.NoTargetUserAction, description })
        {
            Assert.DoesNotContain("30 days", text, StringComparison.Ordinal);
            Assert.Contains("retention period", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AProtectedSection_WhoseEntryParentIdDisagreesWithItsSectionKeyPlace_IsObjectNotFound()
    {
        // Review fix 5: the entry's parentId is a shortcut that must agree with the SectionKey place.
        Grant();
        ProtectedEntry("lonely", LonelyId, AId, APath + @"\Archive\Lonely.one");

        var error = Error(await Tool().RestoreFromRecycleBin(LonelyId, null, null, null, Ct), "ObjectNotFound");

        Assert.Equal(RestoreFromRecycleBinWrite.ProtectedOrphanUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task AProtectedSection_WhoseEntryParentIdAgrees_Restores()
    {
        Grant();
        ProtectedEntry("lonely", LonelyId, ArchiveId, APath + @"\Archive\Lonely.one");

        var root = Ok(await Tool().RestoreFromRecycleBin(LonelyId, null, null, null, Ct));

        Assert.Equal(ArchiveId, root.GetProperty("meta").GetProperty("parentId").GetString());
    }

    [Fact]
    public async Task ASectionInACloudNotebook_SettlesTenSeconds_ThenReadsBack()
    {
        Grant(CloudPath, CloudId);

        var root = Ok(await Tool().RestoreFromRecycleBin(DriftId, null, null, null, Ct));

        Assert.Equal([CloudSettle.Time], settles);
        Assert.Equal(CloudId, root.GetProperty("meta").GetProperty("parentId").GetString());
        var codes = root.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ToList();
        Assert.Equal([ToolWarnings.Settled], codes);
    }

    // --- Protected sections ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AProtectedSection_GoesBackToItsOwnPlace_BySectionKey_StillEncryptedAndLocked_WithItsId()
    {
        Grant();

        var root = Ok(await Tool().RestoreFromRecycleBin(SecretId, null, null, null, Ct));

        Assert.Equal(ArchiveId, root.GetProperty("meta").GetProperty("parentId").GetString());
        Assert.Equal(SecretId, root.GetProperty("meta").GetProperty("objectId").GetString());
        Assert.Equal(ArchiveId, (string?)XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls)).Attribute("ID"));
        var self = HierarchyTarget.Read(gateway.Hierarchies[(SecretId, HierarchyScope.Self)]);
        Assert.True(self.EncryptedMarker);
        Assert.True(self.Locked);
        Assert.False(self.InRecycleBin);
    }

    [Theory]
    [InlineData(AId)]
    [InlineData(PlansId)]
    [InlineData(OtherId)]
    public async Task AProtectedSection_SentElsewhere_IsInvalidArgument_AndNothingIsSent(string parentId)
    {
        Grant();

        var error = Error(await Tool().RestoreFromRecycleBin(SecretId, parentId, null, null, Ct), "InvalidArgument");

        Assert.Equal(RestoreFromRecycleBinWrite.ProtectedElsewhereUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task AProtectedSection_WithItsOwnPlaceAsParentId_Restores()
    {
        Grant();

        Ok(await Tool().RestoreFromRecycleBin(SecretId, ArchiveId, null, null, Ct));

        Assert.Single(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task AProtectedSection_WithNoEntry_IsObjectNotFound()
    {
        Grant();

        var error = Error(await Tool().RestoreFromRecycleBin(LonelyId, ArchiveId, null, null, Ct), "ObjectNotFound");

        Assert.Equal(RestoreFromRecycleBinWrite.ProtectedOrphanUserAction, error.GetProperty("userAction").GetString());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task AProtectedSection_WhoseOwnPlaceIsGone_IsObjectNotFound()
    {
        Grant();
        ProtectedEntry("lonely", LonelyId, ArchiveId, APath + @"\Removed group\Lonely.one");

        var error = Error(await Tool().RestoreFromRecycleBin(LonelyId, null, null, null, Ct), "ObjectNotFound");

        Assert.Contains(entries["lonely"].ToString("D"), error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    // --- Refusals ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(OrphanId)]
    [InlineData(GroupedId)]
    [InlineData(StrandedId)]
    public async Task NoOriginalParent_AndNoTarget_SendsNothing_AndAsksForParentId(string objectId)
    {
        // Deleted in OneNote or pruned (Orphan); binned by a group's delete, whose entry names the group (Grouped); its entry's
        // section soft-deleted too (Stranded).
        Grant();

        var error = Error(await Tool().RestoreFromRecycleBin(objectId, null, null, null, Ct), "InvalidArgument");

        Assert.Equal(RestoreFromRecycleBinWrite.NoTargetUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Theory]
    [InlineData(PlansId, RestoreFromRecycleBinWrite.LiveUserAction)]
    [InlineData(P1, RestoreFromRecycleBinWrite.LiveUserAction)]
    [InlineData(ArchiveId, RestoreFromRecycleBinWrite.LiveUserAction)]
    [InlineData(AId, RestoreFromRecycleBinWrite.LiveUserAction)]
    [InlineData(BinId, RestoreFromRecycleBinWrite.BinItselfUserAction)]
    [InlineData(DpId, RestoreFromRecycleBinWrite.BinItselfUserAction)]
    [InlineData(GonePageId, RestoreFromRecycleBinWrite.RecycledSectionPageUserAction)]
    [InlineData(BinGroupId, RestoreFromRecycleBinWrite.GroupUserAction)]
    public async Task AnythingButARecycledPageOrSection_IsInvalidArgument_BeforeAnyGrantDialog(string objectId, string userAction)
    {
        var prompt = new FakePrompt(Typed("A"));

        var error = Error(await RestoreWithPrompt(prompt, objectId, null), "InvalidArgument");

        Assert.Equal(userAction, error.GetProperty("userAction").GetString());
        Assert.Empty(prompt.Order);
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Theory]
    [InlineData("{FFFFFFFF-0000-4000-8000-000000000001}{1}{E1}", null)]
    [InlineData(LostId, "{FFFFFFFF-0000-4000-8000-000000000001}{1}{B0}")]
    public async Task AnUnknownObjectOrParent_IsObjectNotFound(string objectId, string? parentId)
    {
        Grant();

        Error(await Tool().RestoreFromRecycleBin(objectId, parentId, null, null, Ct), "ObjectNotFound");

        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Theory]
    [InlineData(LostId, ArchiveId, RestoreFromRecycleBinWrite.PageTargetUserAction)]
    [InlineData(LostId, AId, RestoreFromRecycleBinWrite.PageTargetUserAction)]
    [InlineData(LostId, GoneId, RestoreFromRecycleBinWrite.PageTargetUserAction)]
    [InlineData(LostId, DpId, RestoreFromRecycleBinWrite.PageTargetUserAction)]
    [InlineData(GoneId, PlansId, RestoreFromRecycleBinWrite.SectionTargetUserAction)]
    [InlineData(GoneId, BinId, RestoreFromRecycleBinWrite.SectionTargetUserAction)]
    public async Task AWrongKindOrBinnedTarget_IsInvalidArgument(string objectId, string parentId, string userAction)
    {
        Grant();

        var error = Error(await Tool().RestoreFromRecycleBin(objectId, parentId, null, null, Ct), "InvalidArgument");

        Assert.Equal(userAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task ALockedTarget_IsSectionLocked()
    {
        Grant();

        Error(await Tool().RestoreFromRecycleBin(LostId, LockedId, null, null, Ct), "SectionLocked");

        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Theory]
    [InlineData(LostId, LockedId, "SectionLocked")]
    [InlineData(LostId, ArchiveId, "InvalidArgument")]
    [InlineData(ClashId, null, "InvalidArgument")]
    [InlineData(GroupedId, OtherId, "InvalidArgument")]
    public async Task ARefusalAboutTheTarget_ComesBeforeAnyGrantDialog(string objectId, string? parentId, string code)
    {
        var prompt = new FakePrompt(Typed("A"), Typed("Elsewhere"));

        Error(await RestoreWithPrompt(prompt, objectId, parentId), code);

        Assert.Empty(prompt.Order);
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task ATargetMovedToAnotherGrantedNotebookAfterStep1_IsWriteNotGranted_Retryable_NothingWritten()
    {
        // Backlog 18 (AD-7, story 3.12): step 1 granted A for the page and for its original section Plans; before the lock
        // Plans moves to notebook Other, granted too. Step 1's last read is A's fourth self read, the end of the target's grant walk (after the state read's, the parent walk's and the item's).
        Grant();
        Grant(OtherPath, OtherId);
        var before = EntryCount();
        var moved = NotebookMoveKit.MoveOnRead(gateway, AId, HierarchyScope.Self, () => gateway.Parents[PlansId] = OtherId, nth: 4);

        var error = Error(await Tool().RestoreFromRecycleBin(LostId, null, null, null, Ct), "WriteNotGranted");

        Assert.True(moved.Moved(), "the section never moved: the test's trigger read didn't happen");
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(WriteAccess.NotebookChangedUserAction, error.GetProperty("userAction").GetString());
        // The read order is pinned: step 1 reads A's self four times (the move ran on the last), and the re-resolve under the
        // lock once more (the item's walk) before the target's walk finds Other. If step 1 ever read A one time fewer, the
        // move would land inside the re-resolve and this count would change.
        Assert.Equal(5, moved.Reads());
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Equal(before, EntryCount());
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Step4_RepeatsTheChecks_UnderTheLock()
    {
        // The target is locked between step 1's read and step 3's (the second children read of Plans).
        Grant();
        var reads = 0;
        gateway.OnGetHierarchy = (id, scope) =>
        {
            if (id == PlansId && scope == HierarchyScope.Children && ++reads == 2)
            {
                nodes[PlansId] = (nodes[PlansId].Element, nodes[PlansId].Attributes + " encrypted=\"true\" locked=\"true\"");
                Publish();
            }
        };

        Error(await Tool().RestoreFromRecycleBin(LostId, null, null, null, Ct), "SectionLocked");

        Assert.Equal(2, reads);
        Assert.Empty(gateway.UpdateHierarchyCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ASectionWhoseNameALiveSiblingHas_IsInvalidArgument()
    {
        Grant();

        var error = Error(await Tool().RestoreFromRecycleBin(ClashId, null, null, null, Ct), "InvalidArgument");

        Assert.Equal(RestoreFromRecycleBinWrite.NameClashUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task ASectionLandingOnAnotherSectionsReadGrant_IsInvalidArgument_ButItsOwnFormerKeyIsAllowed()
    {
        Grant();
        ReadGrantAt(APath + @"\Gone.one");
        ReadGrantAt(APath + @"\Archive\Gone.one");

        var error = Error(await Tool().RestoreFromRecycleBin(GoneId, AId, null, null, Ct), "InvalidArgument");
        Assert.Equal(RestoreFromRecycleBinWrite.GrantLocationUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);

        Ok(await Tool().RestoreFromRecycleBin(GoneId, null, null, null, Ct));
        Assert.Single(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task ATargetNotebookNotGranted_IsWriteNotGranted()
    {
        Grant();

        Error(await RestoreWithPrompt(new FakePrompt(), LostId, InboxId), "WriteNotGranted");

        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    // --- After the call ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task APartialCopy_IsObjectNotFound_OutcomeUnknown_NamingTheJournalEntry()
    {
        Grant();
        gateway.OnUpdateHierarchy = null;
        gateway.UpdateHierarchyFailure = Com.HResultMap.ToException("UpdateHierarchy", new System.Runtime.InteropServices.COMException("refused", unchecked((int)0x80042004)));

        var error = Error(await Tool().RestoreFromRecycleBin(LostId, null, null, null, Ct), "ObjectNotFound");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains("partial copy", message, StringComparison.Ordinal);
        Assert.Contains("journal entry", message, StringComparison.Ordinal);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AReadBackThatDoesntFindThePage_IsReadBackDiffers()
    {
        Grant();
        gateway.OnUpdateHierarchy = null;

        var root = Ok(await Tool().RestoreFromRecycleBin(LostId, null, null, null, Ct));

        var codes = root.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ToList();
        Assert.Equal([ToolWarnings.ReadBackDiffers, ToolWarnings.ReadBackDiffers], codes);
        Assert.Equal(LostId, root.GetProperty("meta").GetProperty("objectId").GetString());
    }

    [Fact]
    public async Task APageLandingElsewhere_IsReadBackDiffers_NamingThePositionFound()
    {
        // Spike 3.1 R1 and R2: restored pages landed at position 1.
        Grant();
        var apply = gateway.OnUpdateHierarchy!;
        gateway.OnUpdateHierarchy = sent =>
        {
            apply(sent);
            var restored = children[PlansId][^1];
            children[PlansId].Remove(restored);
            children[PlansId].Insert(0, restored);
            Publish();
        };

        var root = Ok(await Tool().RestoreFromRecycleBin(LostId, null, null, null, Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.ReadBackDiffers, warning.GetProperty("code").GetString());
        Assert.Contains("at position 1", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(NewPageId(1), root.GetProperty("meta").GetProperty("objectId").GetString());
    }

    [Fact]
    public async Task AReadBackThatFails_IsReadBackFailed_AndTheRestoreStands()
    {
        Grant();
        var apply = gateway.OnUpdateHierarchy!;
        gateway.OnUpdateHierarchy = sent =>
        {
            apply(sent);
            gateway.Hierarchies.Remove((PlansId, HierarchyScope.Children));
        };

        var root = Ok(await Tool().RestoreFromRecycleBin(LostId, null, null, null, Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.ReadBackFailed, warning.GetProperty("code").GetString());
        Assert.False(root.GetProperty("meta").TryGetProperty("objectId", out _));
        await AssertLockFreeAsync();
    }

    // --- Fixture -----------------------------------------------------------------------------------------------------

    private static string NewPageId(int n) => $"{{A1000000-0000-4000-8000-000000000001}}{{1}}{{E9{n}}}";

    private void Node(string id, string element, string name, string path, string? parent, string extra = "")
    {
        var pathAttribute = path.Length > 0 ? $" path=\"{path}\"" : string.Empty;
        nodes[id] = (element, $"name=\"{name}\" ID=\"{id}\"{pathAttribute} lastModifiedTime=\"{Lmt}\" {extra}".TrimEnd());
        children.TryAdd(id, []);
        gateway.Parents[id] = parent ?? string.Empty;
        if (parent is not null && !children[parent].Contains(id))
        {
            children[parent].Add(id);
        }
    }

    private void Page(string id, string name, string section, int level = 1, bool binned = false) =>
        Node(id, "Page", name, string.Empty, section, $"pageLevel=\"{level}\"{(binned ? " isInRecycleBin=\"true\"" : string.Empty)}");

    private string Attributes(string id) => nodes[id].Attributes;

    /// <summary>Rewrites every node's self, children, sections and pages reads from <see cref="nodes"/> and <see cref="children"/>.</summary>
    private void Publish()
    {
        foreach (var (id, (element, attributes)) in nodes)
        {
            gateway.Hierarchies[(id, HierarchyScope.Self)] = $"<?xml version=\"1.0\"?>\n<one:{element} {Ns} {attributes} />";
            var kids = string.Concat(children[id].Select(k => $"<one:{nodes[k].Element} {nodes[k].Attributes} />"));
            gateway.Hierarchies[(id, HierarchyScope.Children)] = $"<?xml version=\"1.0\"?>\n<one:{element} {Ns} {attributes}>{kids}</one:{element}>";
            gateway.Hierarchies[(id, HierarchyScope.Sections)] = $"<?xml version=\"1.0\"?>\n<one:{element} {Ns} {attributes}>{Below(id, pages: false)}</one:{element}>";
            gateway.Hierarchies[(id, HierarchyScope.Pages)] = $"<?xml version=\"1.0\"?>\n<one:{element} {Ns} {attributes}>{Below(id, pages: true)}</one:{element}>";
        }
    }

    private string Below(string id, bool pages) => string.Concat(children[id]
        .Where(k => pages || nodes[k].Element != "Page")
        .Select(k => $"<one:{nodes[k].Element} {nodes[k].Attributes}>{Below(k, pages)}</one:{nodes[k].Element}>"));

    /// <summary>
    /// Makes the fake apply a restore as OneNote does (spike 3.1 Implication 6): a page leaves Deleted Pages under a new
    /// ID at level 1; a section keeps its ID, leaves the bin, and takes the target's folder.
    /// </summary>
    private void ApplyRestores() => gateway.OnUpdateHierarchy = sent =>
    {
        var root = XElement.Parse(sent);
        var target = (string)root.Attribute("ID")!;
        var listed = root.Elements().Select(e => (string)e.Attribute("ID")!).ToList();
        var moved = listed.Single(id => !children[target].Contains(id));
        children[gateway.Parents[moved]].Remove(moved);
        var (element, attributes) = nodes[moved];
        var restored = moved;
        if (element == "Page")
        {
            restored = NewPageId(++newPages);
            nodes.Remove(moved);
            gateway.Parents.Remove(moved);
            foreach (var scope in Enum.GetValues<HierarchyScope>())
            {
                gateway.Hierarchies.Remove((moved, scope));
            }

            var name = XElement.Parse($"<x {attributes.Replace("isInRecycleBin=\"true\"", string.Empty, StringComparison.Ordinal)} />").Attribute("name")!.Value;
            Node(restored, "Page", name, string.Empty, null, "pageLevel=\"1\"");
            listed[listed.IndexOf(moved)] = restored;
        }
        else
        {
            var leaf = System.Text.RegularExpressions.Regex.Match(attributes, "path=\"([^\"]*)\"").Groups[1].Value.Split('\\', '/')[^1];
            var folder = System.Text.RegularExpressions.Regex.Match(nodes[target].Attributes, "path=\"([^\"]*)\"").Groups[1].Value.TrimEnd('\\', '/');
            var separator = folder.Contains("://", StringComparison.Ordinal) ? "/" : "\\";
            var moves = System.Text.RegularExpressions.Regex.Replace(attributes, "path=\"[^\"]*\"", $"path=\"{folder}{separator}{leaf}\"");
            nodes[moved] = (element, moves.Replace(" isInRecycleBin=\"true\"", string.Empty, StringComparison.Ordinal));
        }

        var others = children[target].Where(k => !listed.Contains(k)).ToList();
        children[target] = [.. listed, .. others];
        gateway.Parents[restored] = target;
        Publish();
    };

    private void Grant(string path = APath, string id = AId) =>
        new SettingsWriter(Store, clock).SaveWriteGrant(
            new NotebookGrant(GrantKeys.NotebookKey(path), "A", id, Write: true, PermanentDelete: false, clock.GetUtcNow(), GrantedVia.Cli));

    private void ReadGrantAt(string sectionPath) =>
        new SettingsWriter(Store, clock).SaveReadGrant(new ProtectedSectionGrant(
            GrantKeys.SectionKey(sectionPath), "Earlier", GrantKeys.NotebookKey(APath), OldId, clock.GetUtcNow(), GrantedVia.Cli));

    private static GrantPromptResult Typed(string name) => new(GrantPromptStatus.Accepted, name);

    // --- Journal entries --------------------------------------------------------------------------------------------

    private static JournalEntry Base(string objectId, string parentId, string parentNamePath, string notebook) => new()
    {
        EntryId = Guid.NewGuid(),
        CorrelationId = "earlier",
        Operation = SoftDeleteUseCase.OperationName,
        OperationClass = "SoftDelete",
        ObjectId = objectId,
        NotebookPath = GrantKeys.NotebookKey(notebook),
        ParentId = parentId,
        ParentNamePath = parentNamePath,
        TakenAtUtc = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc),
        BinaryComplete = true,
    };

    private void Write(string key, JournalEntry entry)
    {
        new JournalStore(Store.Read).WriteTo(JournalFolder, entry);
        entries[key] = entry.EntryId;
    }

    private void PageEntry(string key, string oldId, string parentId, string parentNamePath, string title, string? recycleBinId = null) =>
        Write(key, Base(oldId, parentId, parentNamePath, APath) with
        {
            SnapshotKind = JournalEntry.PageKind,
            Title = title,
            RecycleBinId = recycleBinId,
            Xml = $"<one:Page {Ns} ID=\"{oldId}\" name=\"{title}\" />",
        });

    private void SectionEntry(string key, string oldId, string parentId, string parentNamePath, string name, string oldPath, string notebook = APath) =>
        Write(key, Base(oldId, parentId, parentNamePath, notebook) with
        {
            SnapshotKind = JournalEntry.HierarchyKind,
            Title = name,
            Xml = $"<one:Section {Ns} ID=\"{oldId}\" name=\"{name}\" path=\"{oldPath}\" />",
        });

    private void GroupEntry(string key, string oldId, string parentId, string name) =>
        Write(key, Base(oldId, parentId, string.Empty, APath) with
        {
            SnapshotKind = JournalEntry.HierarchyKind,
            Title = name,
            Xml = $"<one:SectionGroup {Ns} ID=\"{oldId}\" name=\"{name}\"><one:Section ID=\"{GroupedId}\" name=\"Grouped\" /></one:SectionGroup>",
        });

    private void ProtectedEntry(string key, string oldId, string parentId, string oldPath) =>
        Write(key, Base(oldId, parentId, "Archive", APath) with
        {
            SnapshotKind = JournalEntry.HierarchyKind,
            SectionKey = GrantKeys.SectionKey(oldPath),
            Title = "Secret",
            Xml = $"<one:Section {Ns} ID=\"{oldId}\" name=\"Secret\" path=\"{oldPath}\" encrypted=\"true\" />",
        });

    private JournalEntry Entry(string entryId) =>
        Directory.GetFiles(JournalFolder)
            .Where(f => JournalFiles.IsEntry(Path.GetFileName(f)))
            .Select(JournalStore.Read)
            .Single(e => e.EntryId == Guid.Parse(entryId));

    private int EntryCount() => Directory.GetFiles(JournalFolder).Count(f => JournalFiles.IsEntry(Path.GetFileName(f)));

    // --- Running it -------------------------------------------------------------------------------------------------

    private RestoreFromRecycleBinUseCase UseCase()
    {
        var services = new HierarchyWriteServices(
            gateway,
            new WriteAccess(gateway, Store.Read),
            readAccess,
            new HierarchySnapshot(gateway, new JournalStore(Store.Read), clock));
        return new RestoreFromRecycleBinUseCase(
            services,
            new WritePipeline(writeLock, gateway, cache, Store.Read, TimeProvider.System, null, new WriteGrantDialog(gateway, new SettingsWriter(Store, clock), clock)),
            Store.Read,
            (delay, _) =>
            {
                settles.Add(delay);
                return Task.CompletedTask;
            },
            folder =>
            {
                loads++;
                return SoftDeleteEntries.Load(folder);
            });
    }

    private static ToolRunner Runner() => new(NullLogger<ToolRunner>.Instance);

    private RestoreFromRecycleBinTool Tool() => new(Runner(), UseCase());

    private Task<CallToolResult> RestoreWithPrompt(IGrantPrompt prompt, string objectId, string? parentId, int? position = null)
    {
        var useCase = UseCase();
        return Runner().RunAsync(
            RestoreFromRecycleBinTool.Name,
            useCase,
            (context, token) => useCase.RestoreAsync(context, objectId, parentId, null, position, token),
            Ct,
            prompt);
    }

    private async Task AssertLockFreeAsync()
    {
        using var probe = await writeLock.AcquireAsync("free?", TimeSpan.FromMilliseconds(500), Ct);
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
}
