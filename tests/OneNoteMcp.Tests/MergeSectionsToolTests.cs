using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
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
/// Story 3.6: <c>merge_sections</c> (<see cref="MergeSectionsWrite"/>), one test or theory per row of the plan's I/O matrix,
/// plus the call sent, the journal entry, the step-1 refusals before any grant dialog and step 4's repeat, against
/// <see cref="FakeGateway"/> scripted with three synthetic notebooks: A (local; sections Source (S1, its subpage S2, S3),
/// Dest (D1), Vault (protected, V1), Locked (protected, locked), Empty (no pages); group Archive; the recycle bin with
/// section Gone (G1) and Deleted Pages (X1)), Other (local; section Inbox (I1)) and Cloud (a OneDrive URL; sections Drift
/// (R1) and Harbor (H1)). The fake applies a merge as OneNote does (spike 3.1 Implication 4): the source and its pages
/// stay, and copies of its live pages go to the end of the destination under new IDs, levels kept.
/// </summary>
public sealed class MergeSectionsToolTests : IDisposable
{
    private const string Lmt = "2026-11-18T10:00:00.000Z";
    private const string OlderLmt = "2026-11-18T09:00:00.000Z";

    private const string AId = "{A0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string APath = @"C:\notebooks\A\";
    private const string SourceId = "{A1000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string DestId = "{A2000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string VaultId = "{A3000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string LockedId = "{A4000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string EmptyId = "{A5000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string ArchiveId = "{A6000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string BinId = "{A7000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string GoneId = "{A8000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string DeletedPagesId = "{A9000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string S1 = "{A1000000-0000-4000-8000-000000000001}{1}{E11}";
    private const string S2 = "{A1000000-0000-4000-8000-000000000001}{1}{E12}";
    private const string S3 = "{A1000000-0000-4000-8000-000000000001}{1}{E13}";
    private const string D1 = "{A2000000-0000-4000-8000-000000000001}{1}{E21}";
    private const string V1 = "{A3000000-0000-4000-8000-000000000001}{1}{E31}";
    private const string G1 = "{A8000000-0000-4000-8000-000000000001}{1}{E81}";
    private const string X1 = "{A9000000-0000-4000-8000-000000000001}{1}{E91}";

    private const string OtherId = "{C0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string OtherPath = @"C:\notebooks\Other\";
    private const string InboxId = "{C1000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string I1 = "{C1000000-0000-4000-8000-000000000001}{1}{E11}";

    private const string CloudId = "{D0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string CloudPath = "https://d.docs.live.net/0123abcd/Cloud/";
    private const string DriftId = "{D1000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string HarborId = "{D2000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string R1 = "{D1000000-0000-4000-8000-000000000001}{1}{E11}";
    private const string H1 = "{D2000000-0000-4000-8000-000000000001}{1}{E11}";

    /// <summary>The source's and its first page's names appear nowhere else, so a test can show they never reach a message.</summary>
    private const string SourceName = "Source Zq7";
    private const string PageName = "Plan Kx9";

    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly FakeGateway gateway = new();
    private readonly ConcurrentQueue<WriteLockHeld> holds = new();
    private readonly WriteLock writeLock;
    private readonly ReadAccess readAccess;
    private readonly PageTextCache cache = PageTextCache.WithDefaults();
    private readonly List<TimeSpan> settles = [];

    /// <summary>Every node's attributes, by ID, and its element name; the fake's XML is rebuilt from them.</summary>
    private readonly Dictionary<string, (string Element, string Attributes)> nodes = new(StringComparer.Ordinal);

    /// <summary>Each node's children, in order.</summary>
    private readonly Dictionary<string, List<string>> children = new(StringComparer.Ordinal);

    private int copies;

    public MergeSectionsToolTests()
    {
        writeLock = new WriteLock($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", holds.Enqueue);
        readAccess = new ReadAccess(gateway, Store.Read, new SettingsWriter(Store, clock), clock);
        Directory.CreateDirectory(temp.Combine("cfg"));

        Node(AId, "Notebook", "A", APath, null, "nickname=\"A\"");
        Node(SourceId, "Section", SourceName, @"C:\notebooks\A\Source Zq7.one", AId);
        Page(S1, PageName, 1, SourceId);
        Page(S2, "S2", 2, SourceId);
        Page(S3, "S3", 1, SourceId);
        Node(DestId, "Section", "Dest", @"C:\notebooks\A\Dest.one", AId);
        Page(D1, "D1", 1, DestId);
        Node(VaultId, "Section", "Vault", @"C:\notebooks\A\Vault.one", AId, "encrypted=\"true\"");
        Page(V1, "V1", 1, VaultId);
        Node(LockedId, "Section", "Locked", @"C:\notebooks\A\Locked.one", AId, "encrypted=\"true\" locked=\"true\"");
        Node(EmptyId, "Section", "Empty", @"C:\notebooks\A\Empty.one", AId);
        Node(ArchiveId, "SectionGroup", "Archive", @"C:\notebooks\A\Archive", AId);
        Node(BinId, "SectionGroup", "OneNote_RecycleBin", @"C:\notebooks\A\OneNote_RecycleBin", AId, "isRecycleBin=\"true\"");
        Node(GoneId, "Section", "Gone", @"C:\notebooks\A\OneNote_RecycleBin\Gone.one", BinId, "isInRecycleBin=\"true\"");
        Page(G1, "G1", 1, GoneId, "isInRecycleBin=\"true\"");
        Node(DeletedPagesId, "Section", "Deleted Pages", @"C:\notebooks\A\OneNote_RecycleBin\OneNote_DeletedPages.one", BinId, "isInRecycleBin=\"true\" isDeletedPages=\"true\"");
        Page(X1, "X1", 1, DeletedPagesId, "isInRecycleBin=\"true\"");

        Node(OtherId, "Notebook", "Other", OtherPath, null, "nickname=\"Elsewhere\"");
        Node(InboxId, "Section", "Inbox", @"C:\notebooks\Other\Inbox.one", OtherId);
        Page(I1, "I1", 1, InboxId);

        Node(CloudId, "Notebook", "Cloud", CloudPath, null, "nickname=\"Cloud\"");
        Node(DriftId, "Section", "Drift", CloudPath + "Drift.one", CloudId);
        Page(R1, "R1", 1, DriftId);
        Node(HarborId, "Section", "Harbor", CloudPath + "Harbor.one", CloudId);
        Page(H1, "H1", 1, HarborId);
        Publish();

        // The grant dialog's list of open notebooks (AD-6).
        gateway.Hierarchies[(string.Empty, HierarchyScope.Sections)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebooks {Ns}><one:Notebook {Attributes(AId)} /><one:Notebook {Attributes(OtherId)} /><one:Notebook {Attributes(CloudId)} /></one:Notebooks>";
        ApplyMerges();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SettingsStore Store => new(Path.Combine(temp.Combine("cfg"), "settings.json"), temp.Combine("journal"));

    private string JournalFolder => temp.Combine("journal");

    public void Dispose() => temp.Dispose();

    // --- Merges that go through ------------------------------------------------------------------------------------

    [Fact]
    public async Task SameNotebook_CopiesTheSourcesPagesToTheEnd_UnderNewIds_SubpageKept_SourceUntouched_OneEntry()
    {
        Grant();

        var root = Ok(await Tool().MergeSections(SourceId, DestId, Basis(Lmt), Ct));

        // One MergeSections(source, destination), and nothing else sent.
        Assert.Equal([(SourceId, DestId)], gateway.MergeSectionsCalls);
        Assert.Empty(gateway.UpdateHierarchyCalls);

        var meta = root.GetProperty("meta");
        Assert.Equal(SourceId, meta.GetProperty("objectId").GetString());
        Assert.Equal(DestId, meta.GetProperty("parentId").GetString());
        var pageIds = meta.GetProperty("pageIds").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal([Copy(1), Copy(2), Copy(3)], pageIds);
        Assert.Equal([D1, Copy(1), Copy(2), Copy(3)], children[DestId]);
        Assert.Contains("pageLevel=\"2\"", nodes[Copy(2)].Attributes, StringComparison.Ordinal);
        Assert.Equal([S1, S2, S3], children[SourceId]);

        // One hierarchy entry, of the destination as it was (AD-8).
        var entryId = meta.GetProperty("journalEntryId").GetString()!;
        Assert.False(meta.TryGetProperty("journalEntryIds", out _));
        var entry = Assert.Single(Entries());
        Assert.Equal(Guid.Parse(entryId), entry.EntryId);
        Assert.Equal(DestId, entry.ObjectId);
        Assert.Equal(MergeSectionsWrite.OperationName, entry.Operation);
        Assert.Contains(D1, entry.Xml, StringComparison.Ordinal);
        Assert.DoesNotContain(Copy(1), entry.Xml, StringComparison.Ordinal);
        Assert.Null(entry.SectionKey);

        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
        Assert.Empty(settles);
        Assert.DoesNotContain(SourceName, root.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(PageName, root.GetRawText(), StringComparison.Ordinal);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task IntoAnotherNotebook_Granted_MergesIt_WithNoSettle()
    {
        Grant();
        Grant(OtherPath, OtherId);

        var root = Ok(await Tool().MergeSections(SourceId, InboxId, Basis(Lmt), Ct));

        Assert.Equal([(SourceId, InboxId)], gateway.MergeSectionsCalls);
        Assert.Equal(3, root.GetProperty("meta").GetProperty("pageIds").GetArrayLength());
        Assert.Equal(InboxId, Assert.Single(Entries()).ObjectId);
        Assert.Empty(settles);
    }

    [Fact]
    public async Task NeitherNotebookGranted_TheDialogAsksOncePerNotebook_SourcesFirst()
    {
        var prompt = new FakePrompt(Typed("A"), Typed("Elsewhere"));

        Ok(await MergeWithPrompt(prompt, SourceId, InboxId));

        Assert.Equal(["write", "write"], prompt.Order);
        Assert.Equal(AId, prompt.WriteQuestions[0].NotebookId);
        Assert.Equal(OtherId, prompt.WriteQuestions[1].NotebookId);
        Assert.Single(gateway.MergeSectionsCalls);
    }

    [Fact]
    public async Task OneNotebook_TheDialogAsksOnce()
    {
        var prompt = new FakePrompt(Typed("A"));

        Ok(await MergeWithPrompt(prompt, SourceId, DestId));

        Assert.Equal(["write"], prompt.Order);
    }

    [Fact]
    public async Task IntoAnUnlockedProtectedSection_NeedsOnlyTheWriteGrant_EntryEncrypted_CountsOnly()
    {
        // AD-21 Writes: the copies land inside its encryption (spike 3.1 SP); a write-grant-only write returns nothing from
        // inside the section, so no meta.pageIds (Q6 a).
        Grant();

        var root = Ok(await Tool().MergeSections(SourceId, VaultId, Basis(Lmt), Ct));

        Assert.Equal([(SourceId, VaultId)], gateway.MergeSectionsCalls);
        var meta = root.GetProperty("meta");
        Assert.False(meta.TryGetProperty("pageIds", out _));
        Assert.Equal(VaultId, meta.GetProperty("parentId").GetString());
        var file = JournalFiles.Find(JournalFolder, Guid.Parse(meta.GetProperty("journalEntryId").GetString()!))!;
        Assert.Equal(GrantKeys.SectionKey(@"C:\notebooks\A\Vault.one"), JournalStore.Read(file).SectionKey);
        Assert.DoesNotContain(V1, RawEntryJson(file), StringComparison.Ordinal);
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task IntoAProtectedSection_ACountThatDiffers_IsReadBackDiffers_WithCountsOnly()
    {
        Grant();
        gateway.OnMergeSections = (source, destination) => CopyPages(source, destination, take: 2);

        var root = Ok(await Tool().MergeSections(SourceId, VaultId, Basis(Lmt), Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.ReadBackDiffers, warning.GetProperty("code").GetString());
        var text = warning.GetRawText();
        Assert.Contains("2", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Copy(1), text, StringComparison.Ordinal);
        Assert.DoesNotContain(V1, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SourceId, HarborId)]
    [InlineData(DriftId, DestId)]
    [InlineData(DriftId, HarborId)]
    public async Task EitherSideInACloudNotebook_SettlesTenSecondsUnderTheLock_BeforeTheReadBack(string sourceId, string destinationId)
    {
        // Q2 a (AD-7 Moves, M2).
        Grant();
        Grant(CloudPath, CloudId);
        var readsAtSettle = -1;
        settleHook = () => readsAtSettle = gateway.Calls.Count;

        var root = Ok(await Tool().MergeSections(sourceId, destinationId, Basis(Lmt), Ct));

        Assert.Equal([TimeSpan.FromSeconds(10)], settles);
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.Settled, warning.GetProperty("code").GetString());
        Assert.Equal(CloudSettle.SettledUserAction, warning.GetProperty("userAction").GetString());
        Assert.Equal(sourceId == DriftId ? 1 : 3, root.GetProperty("meta").GetProperty("pageIds").GetArrayLength());

        // The read-back's re-reads come after the settle.
        Assert.Equal([(sourceId, HierarchyScope.Children), (destinationId, HierarchyScope.Children)], gateway.Calls.Skip(readsAtSettle));
        await AssertLockFreeAsync();
    }

    // --- Step 1: arguments, kinds, the recycle bin --------------------------------------------------------------------

    [Theory]
    [InlineData("", DestId)]
    [InlineData(SourceId, "")]
    public async Task AnEmptyId_IsInvalidArgument_BeforeAnyRead(string sourceId, string destinationId)
    {
        Grant();

        Error(await Tool().MergeSections(sourceId, destinationId, null, Ct), "InvalidArgument");

        Assert.Empty(gateway.Calls);
        AssertNothingWritten();
    }

    [Fact]
    public async Task TheSameSection_IsInvalidArgument_BeforeAnyRead()
    {
        Grant();

        var error = Error(await Tool().MergeSections(SourceId, SourceId, null, Ct), "InvalidArgument");

        Assert.Equal(MergeSectionsWrite.SameSectionUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.Calls);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ABasisWithAHash_IsInvalidArgument_BeforeAnyRead()
    {
        Grant();

        var basis = new PageBasisArgument(Lmt, PageHash: "abc");
        Error(await Tool().MergeSections(SourceId, DestId, basis, Ct), "InvalidArgument");

        Assert.Empty(gateway.Calls);
        AssertNothingWritten();
    }

    [Fact]
    public async Task AnUnknownId_IsObjectNotFound_NothingSent()
    {
        Grant();
        const string Unknown = "{EEEEEEEE-0000-4000-8000-000000000001}{1}{B0}";

        Error(await Tool().MergeSections(Unknown, DestId, null, Ct), "ObjectNotFound");
        Error(await Tool().MergeSections(SourceId, Unknown, null, Ct), "ObjectNotFound");

        AssertNothingWritten();
    }

    [Theory]
    [InlineData(GoneId, DestId)]
    [InlineData(SourceId, GoneId)]
    [InlineData(BinId, DestId)]
    [InlineData(SourceId, BinId)]
    [InlineData(SourceId, DeletedPagesId)]
    [InlineData(G1, DestId)]
    [InlineData(SourceId, X1)]
    public async Task TheRecycleBin_OrAnythingInIt_IsObjectNotFound_BeforeTheKind_NoDialog(string sourceId, string destinationId)
    {
        // AD-7: nothing in or into the recycle bin outside restore. The bin is a section group and G1, X1 are pages, so
        // the kind rule would say InvalidArgument: the recycle-bin rule comes first.
        var prompt = new FakePrompt();

        Error(await MergeWithPrompt(prompt, sourceId, destinationId), "ObjectNotFound");

        Assert.Empty(prompt.Order);
        AssertNothingWritten();
    }

    [Theory]
    [InlineData(S1, DestId)]
    [InlineData(S2, DestId)]
    [InlineData(SourceId, D1)]
    [InlineData(ArchiveId, DestId)]
    [InlineData(SourceId, ArchiveId)]
    [InlineData(AId, DestId)]
    [InlineData(SourceId, OtherId)]
    public async Task APageGroupOrNotebook_IsInvalidArgument_NoDialog(string sourceId, string destinationId)
    {
        var prompt = new FakePrompt();

        var error = Error(await MergeWithPrompt(prompt, sourceId, destinationId), "InvalidArgument");

        var action = error.GetProperty("userAction").GetString();
        Assert.Contains(action, new[] { MergeSectionsWrite.SourceUserAction, MergeSectionsWrite.DestinationUserAction });
        Assert.Empty(prompt.Order);
        AssertNothingWritten();
    }

    [Fact]
    public async Task APageInALockedSection_IsInvalidArgument_ByTheWalk_NeverASelfReadFirst()
    {
        // 3.3 S1: OneNote hides a locked section's pages, so a self read of the page would fail; the walk answers.
        const string Hidden = "{A4000000-0000-4000-8000-000000000001}{1}{E41}";
        gateway.Parents[Hidden] = LockedId;
        Grant();

        Error(await Tool().MergeSections(Hidden, DestId, null, Ct), "InvalidArgument");

        Assert.DoesNotContain((Hidden, HierarchyScope.Self), gateway.Calls);
        AssertNothingWritten();
    }

    // --- Step 1: protected, locked, empty; then the grants -----------------------------------------------------------

    [Theory]
    [InlineData(VaultId, LockedId)]
    [InlineData(LockedId, DestId)]
    public async Task AProtectedSource_LockedOrNot_IsNotSupported_NothingSent_NoDialog(string sourceId, string destinationId)
    {
        // AD-7 Merges, E8, H2: checked first, so a locked source is NotSupported, never SectionLocked, and a protected
        // source into a locked destination is NotSupported too.
        var prompt = new FakePrompt();

        var error = Error(await MergeWithPrompt(prompt, sourceId, destinationId), "NotSupported");

        Assert.Equal(MergeSectionsWrite.ProtectedUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(prompt.Order);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ASourceUnderAnExportRoot_IsNotSupported_AsAProtectedOne()
    {
        // Entry 15: no marker, but the notebook's folder is an export root.
        Grant();
        TestSettings.SetExportRoots(Store.Path, @"C:\notebooks\A");

        var error = Error(await Tool().MergeSections(SourceId, InboxId, null, Ct), "NotSupported");

        Assert.Contains("export root or the backups folder", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ALockedDestination_IsSectionLocked_NothingSent_NoDialog()
    {
        var prompt = new FakePrompt();

        Error(await MergeWithPrompt(prompt, SourceId, LockedId), "SectionLocked");

        Assert.Empty(prompt.Order);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ASourceWithNoLivePages_IsInvalidArgument_NothingSent_NoDialog()
    {
        // Q7 a: an unprobed call is never sent.
        var prompt = new FakePrompt();

        var error = Error(await MergeWithPrompt(prompt, EmptyId, DestId), "InvalidArgument");

        Assert.Equal(MergeSectionsWrite.EmptySourceUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(prompt.Order);
        AssertNothingWritten();
    }

    [Fact]
    public async Task TheDestinationsNotebookNotGranted_WithNoDialog_IsWriteNotGranted_NothingSent()
    {
        Grant();

        Error(await Tool().MergeSections(SourceId, InboxId, Basis(Lmt), Ct), "WriteNotGranted");

        AssertNothingWritten();
        Assert.Empty(holds);
    }

    // --- Step 4 repeats step 1 under the lock; the notebooks are re-resolved ------------------------------------------

    [Fact]
    public async Task ADestinationLockedBetweenStep1AndStep3_IsSectionLocked_ByStep4_NothingSent()
    {
        Grant();
        var reads = 0;
        gateway.OnGetHierarchy = (id, scope) =>
        {
            // Step 1 reads the destination at self once; the second read is step 3's.
            if (id == DestId && scope == HierarchyScope.Self && ++reads == 2)
            {
                Set(DestId, "locked=\"true\" encrypted=\"true\"");
            }
        };

        Error(await Tool().MergeSections(SourceId, DestId, Basis(Lmt), Ct), "SectionLocked");

        Assert.Equal(2, reads);
        Assert.NotEmpty(holds);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ASourceProtectedBetweenStep1AndStep3_IsNotSupported_ByStep4()
    {
        Grant();
        var reads = 0;
        gateway.OnGetHierarchy = (id, scope) =>
        {
            if (id == SourceId && scope == HierarchyScope.Self && ++reads == 2)
            {
                Set(SourceId, "encrypted=\"true\"");
            }
        };

        Error(await Tool().MergeSections(SourceId, DestId, Basis(Lmt), Ct), "NotSupported");

        Assert.NotEmpty(holds);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ASourceEmptiedBetweenStep1AndStep3_IsInvalidArgument_ByStep4()
    {
        Grant();
        var reads = 0;
        gateway.OnGetHierarchy = (id, scope) =>
        {
            if (id == SourceId && scope == HierarchyScope.Children && ++reads == 2)
            {
                children[SourceId].Clear();
                Publish();
            }
        };

        Error(await Tool().MergeSections(SourceId, DestId, Basis(Lmt), Ct), "InvalidArgument");

        Assert.NotEmpty(holds);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ASourceMovedToAnotherNotebookAfterItsGrant_IsWriteNotGranted_Retryable_ByThePipelinesReResolve()
    {
        // Story 3.12's ledger: both grants are re-resolved under the lock. Step 1 reads notebook A at self four times (each
        // section's walk, then each grant's); after the fourth, Source sits in Other (also granted).
        Grant();
        Grant(OtherPath, OtherId);
        var reads = 0;
        gateway.OnGetHierarchy = (id, scope) =>
        {
            if (id == AId && scope == HierarchyScope.Self && ++reads == 4)
            {
                gateway.Parents[SourceId] = OtherId;
            }
        };

        var error = Error(await Tool().MergeSections(SourceId, DestId, Basis(Lmt), Ct), "WriteNotGranted");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        AssertNothingWritten();
        await AssertLockFreeAsync();
    }

    // --- Step 5: the basis ------------------------------------------------------------------------------------------

    [Fact]
    public async Task AStaleBasis_IsStaleWrite_NothingSent()
    {
        Grant();

        var error = Error(await Tool().MergeSections(SourceId, DestId, Basis(OlderLmt), Ct), "StaleWrite");

        Assert.Equal(MergeSectionsWrite.StaleUserAction, error.GetProperty("userAction").GetString());
        AssertNothingWritten();
    }

    [Fact]
    public async Task NoBasis_MergesWithTheWarningNoBasis()
    {
        Grant();

        var root = Ok(await Tool().MergeSections(SourceId, DestId, null, Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.NoBasis, warning.GetProperty("code").GetString());
        Assert.Equal(MergeSectionsWrite.NoBasisUserAction, warning.GetProperty("userAction").GetString());
    }

    // --- Step 7: failures --------------------------------------------------------------------------------------------

    [Fact]
    public async Task MergeFailed_AfterAPartialCopy_IsInternal_OutcomeUnknown_NamingTheCopies_AndSayingToDeleteThem()
    {
        // Q3 a: never retried; the destination is re-read by its ID before the throw.
        Grant();
        gateway.OnMergeSections = null;
        gateway.OnMergeSectionsCall = _ =>
        {
            CopyPages(SourceId, DestId, take: 1);
            return Task.CompletedTask;
        };
        gateway.MergeSectionsFailure = Com.HResultMap.ToException("MergeSections", new System.Runtime.InteropServices.COMException("merge", unchecked((int)0x8004201F)));

        var error = Error(await Tool().MergeSections(SourceId, DestId, Basis(Lmt), Ct), "Internal");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal("0x8004201F", error.GetProperty("hresult").GetString());
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains($"1 new page(s) ({Copy(1)})", message, StringComparison.Ordinal);
        Assert.Contains("delete", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("journal entry", message, StringComparison.Ordinal);
        Assert.DoesNotContain(PageName, error.GetRawText(), StringComparison.Ordinal);
        Assert.Single(gateway.MergeSectionsCalls);
        Assert.Contains((DestId, HierarchyScope.Children), gateway.Calls.Skip(gateway.Calls.Count - 1));
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task MergeFailed_WithNothingCopied_SaysSo()
    {
        Grant();
        gateway.OnMergeSections = null;
        gateway.MergeSectionsFailure = Com.HResultMap.ToException("MergeSections", new System.Runtime.InteropServices.COMException("merge", unchecked((int)0x8004201F)));

        var error = Error(await Tool().MergeSections(SourceId, DestId, Basis(Lmt), Ct), "Internal");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Contains("no new pages", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeFailed_IntoAProtectedSection_GivesTheCountOnly()
    {
        Grant();
        gateway.OnMergeSections = null;
        gateway.OnMergeSectionsCall = _ =>
        {
            CopyPages(SourceId, VaultId, take: 2);
            return Task.CompletedTask;
        };
        gateway.MergeSectionsFailure = Com.HResultMap.ToException("MergeSections", new System.Runtime.InteropServices.COMException("merge", unchecked((int)0x8004201F)));

        var error = Error(await Tool().MergeSections(SourceId, VaultId, Basis(Lmt), Ct), "Internal");

        var message = error.GetProperty("message").GetString()!;
        Assert.Contains("2 new page(s)", message, StringComparison.Ordinal);
        Assert.DoesNotContain(Copy(1), message, StringComparison.Ordinal);
        Assert.DoesNotContain(Copy(2), message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MergeFailed_OnACloudMerge_SettlesBeforeTheReRead()
    {
        Grant();
        Grant(CloudPath, CloudId);
        gateway.OnMergeSections = null;
        gateway.MergeSectionsFailure = Com.HResultMap.ToException("MergeSections", new System.Runtime.InteropServices.COMException("merge", unchecked((int)0x8004201F)));
        var readsAtSettle = -1;
        settleHook = () => readsAtSettle = gateway.Calls.Count;

        Error(await Tool().MergeSections(DriftId, HarborId, null, Ct), "Internal");

        Assert.Equal([TimeSpan.FromSeconds(10)], settles);
        Assert.Equal([(HarborId, HierarchyScope.Children)], gateway.Calls.Skip(readsAtSettle));
    }

    [Theory]
    [InlineData(ErrorCode.Timeout)]
    [InlineData(ErrorCode.ComUnavailable)]
    public async Task AnyOtherOutcomeUnknownFailure_GetsTheSameReRead_AndItsFindings(ErrorCode code)
    {
        Grant();
        gateway.OnMergeSections = null;
        gateway.OnMergeSectionsCall = _ =>
        {
            CopyPages(SourceId, DestId, take: 3);
            return Task.CompletedTask;
        };
        gateway.MergeSectionsFailure = new OneNoteException(code, "MergeSections didn't answer.");

        var error = Error(await Tool().MergeSections(SourceId, DestId, Basis(Lmt), Ct), code.ToString());

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Contains($"3 new page(s) ({Copy(1)}, {Copy(2)}, {Copy(3)})", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelDuringTheCall_IsCancelled_OutcomeUnknown_WithTheReReadsFindings()
    {
        Grant();
        gateway.OnMergeSections = null;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        gateway.OnMergeSectionsCall = async token =>
        {
            CopyPages(SourceId, DestId, take: 1);
            await cancel.CancelAsync();
            token.ThrowIfCancellationRequested();
        };

        var error = Error(await Tool().MergeSections(SourceId, DestId, Basis(Lmt), cancel.Token), "Cancelled");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Contains($"1 new page(s) ({Copy(1)})", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALockedSectionFromCom_IsSectionLocked_NotOutcomeUnknown()
    {
        Grant();
        gateway.OnMergeSections = null;
        gateway.MergeSectionsFailure = Com.HResultMap.ToException("MergeSections", new System.Runtime.InteropServices.COMException("locked", unchecked((int)0x8004201B)));

        var error = Error(await Tool().MergeSections(SourceId, DestId, Basis(Lmt), Ct), "SectionLocked");

        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Contains("journal entry", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    // --- The read-back -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task FewerCopiesThanSourcePages_IsReadBackDiffers_AndTheCopiesAreStillListed()
    {
        Grant();
        gateway.OnMergeSections = (source, destination) => CopyPages(source, destination, take: 2);

        var root = Ok(await Tool().MergeSections(SourceId, DestId, Basis(Lmt), Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.ReadBackDiffers, warning.GetProperty("code").GetString());
        Assert.Equal([Copy(1), Copy(2)], root.GetProperty("meta").GetProperty("pageIds").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task ASourceThatLostAPage_IsReadBackDiffers()
    {
        // Never seen (spike 3.1): the source keeps its ID and every page ID.
        Grant();
        var apply = gateway.OnMergeSections!;
        gateway.OnMergeSections = (source, destination) =>
        {
            apply(source, destination);
            children[SourceId].Remove(S3);
            Publish();
        };

        var root = Ok(await Tool().MergeSections(SourceId, DestId, Basis(Lmt), Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.ReadBackDiffers, warning.GetProperty("code").GetString());
        Assert.Contains(SourceId, warning.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadBackThatFails_IsReadBackFailed_AndTheMergeStands()
    {
        Grant();
        var apply = gateway.OnMergeSections!;
        gateway.OnMergeSections = (source, destination) =>
        {
            apply(source, destination);
            gateway.Hierarchies.Remove((DestId, HierarchyScope.Children));
        };

        var root = Ok(await Tool().MergeSections(SourceId, DestId, Basis(Lmt), Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.ReadBackFailed, warning.GetProperty("code").GetString());
        Assert.False(root.GetProperty("meta").TryGetProperty("pageIds", out _));
        Assert.Equal(SourceId, root.GetProperty("meta").GetProperty("objectId").GetString());
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AReadBackThatFailsAfterACloudSettle_KeepsSettled()
    {
        Grant();
        Grant(CloudPath, CloudId);
        settleHook = () => gateway.Hierarchies.Remove((HarborId, HierarchyScope.Children));

        var root = Ok(await Tool().MergeSections(DriftId, HarborId, null, Ct));

        var codes = root.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ToList();
        Assert.Equal([ToolWarnings.NoBasis, ToolWarnings.Settled, ToolWarnings.ReadBackFailed], codes);
    }

    [Fact]
    public async Task TheSearchCache_DropsNothing()
    {
        Grant();
        var when = DateTimeOffset.Parse(Lmt, System.Globalization.CultureInfo.InvariantCulture);
        cache.Set(S1, when, "text", 1 << 20);

        Ok(await Tool().MergeSections(SourceId, DestId, Basis(Lmt), Ct));

        Assert.True(cache.TryGet(S1, when, out _));
    }

    // --- Helpers ----------------------------------------------------------------------------------------------------

    private Action? settleHook;

    private static string Copy(int n) => $"{{F{n:D7}-0000-4000-8000-000000000001}}{{1}}{{E{n}}}";

    private void Node(string id, string element, string name, string path, string? parent, string extra = "")
    {
        nodes[id] = (element, $"name=\"{name}\" ID=\"{id}\" path=\"{path}\" lastModifiedTime=\"{Lmt}\" {extra}".TrimEnd());
        children.TryAdd(id, []);
        gateway.Parents[id] = parent ?? string.Empty;
        if (parent is not null && !children[parent].Contains(id))
        {
            children[parent].Add(id);
        }
    }

    private void Page(string id, string name, int level, string section, string extra = "")
    {
        nodes[id] = ("Page", $"ID=\"{id}\" name=\"{name}\" lastModifiedTime=\"{Lmt}\" pageLevel=\"{level}\" {extra}".TrimEnd());
        children.TryAdd(id, []);
        gateway.Parents[id] = section;
        children[section].Add(id);
    }

    /// <summary>Adds <paramref name="extra"/> to a node's attributes and republishes.</summary>
    private void Set(string id, string extra)
    {
        nodes[id] = (nodes[id].Element, nodes[id].Attributes + " " + extra);
        Publish();
    }

    private string Attributes(string id) => nodes[id].Attributes;

    /// <summary>Rewrites every node's self, children and sections reads from <see cref="nodes"/> and <see cref="children"/>; pages have no reads of their own here.</summary>
    private void Publish()
    {
        foreach (var (id, (element, attributes)) in nodes)
        {
            if (element == "Page")
            {
                continue;
            }

            gateway.Hierarchies[(id, HierarchyScope.Self)] = $"<?xml version=\"1.0\"?>\n<one:{element} {Ns} {attributes} />";
            var kids = string.Concat(children[id].Select(k => $"<one:{nodes[k].Element} {nodes[k].Attributes} />"));
            gateway.Hierarchies[(id, HierarchyScope.Children)] = $"<?xml version=\"1.0\"?>\n<one:{element} {Ns} {attributes}>{kids}</one:{element}>";
        }
    }

    /// <summary>Makes the fake apply a merge as OneNote does (spike 3.1 Implication 4).</summary>
    private void ApplyMerges() => gateway.OnMergeSections = (source, destination) => CopyPages(source, destination, int.MaxValue);

    /// <summary>Copies the first <paramref name="take"/> live pages of <paramref name="source"/> to the end of <paramref name="destination"/> under new IDs, levels kept.</summary>
    private void CopyPages(string source, string destination, int take)
    {
        foreach (var page in children[source].Where(p => !nodes[p].Attributes.Contains("isInRecycleBin", StringComparison.Ordinal)).Take(take).ToList())
        {
            var id = Copy(++copies);
            nodes[id] = ("Page", nodes[page].Attributes.Replace($"ID=\"{page}\"", $"ID=\"{id}\"", StringComparison.Ordinal));
            children.TryAdd(id, []);
            gateway.Parents[id] = destination;
            children[destination].Add(id);
        }

        Publish();
    }

    private void Grant(string path = APath, string id = AId) =>
        new SettingsWriter(Store, clock).SaveWriteGrant(
            new NotebookGrant(GrantKeys.NotebookKey(path), "A", id, Write: true, PermanentDelete: false, clock.GetUtcNow(), GrantedVia.Cli));

    private static PageBasisArgument Basis(string date) => new(date);

    private static GrantPromptResult Typed(string name) => new(GrantPromptStatus.Accepted, name);

    private IReadOnlyList<JournalEntry> Entries() =>
        !Directory.Exists(JournalFolder)
            ? []
            : [.. Directory.GetFiles(JournalFolder).Where(f => JournalFiles.IsEntry(Path.GetFileName(f))).Select(JournalStore.Read)];

    private static string RawEntryJson(string file)
    {
        using var stream = File.OpenRead(file);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    private void AssertNothingWritten()
    {
        Assert.Empty(gateway.MergeSectionsCalls);
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(Entries());
    }

    private MergeSectionsUseCase UseCase()
    {
        var services = new HierarchyWriteServices(
            gateway,
            new WriteAccess(gateway, Store.Read),
            readAccess,
            new HierarchySnapshot(gateway, new JournalStore(Store.Read), clock));
        return new MergeSectionsUseCase(
            services,
            new WritePipeline(writeLock, gateway, cache, Store.Read, TimeProvider.System, null, new WriteGrantDialog(gateway, new SettingsWriter(Store, clock), clock)),
            (delay, _) =>
            {
                settles.Add(delay);
                settleHook?.Invoke();
                return Task.CompletedTask;
            });
    }

    private static ToolRunner Runner() => new(NullLogger<ToolRunner>.Instance);

    private MergeSectionsTool Tool() => new(Runner(), UseCase());

    private Task<CallToolResult> MergeWithPrompt(IGrantPrompt prompt, string sourceId, string destinationId)
    {
        var useCase = UseCase();
        return Runner().RunAsync(
            MergeSectionsTool.Name,
            useCase,
            (context, token) => useCase.MergeAsync(context, sourceId, destinationId, null, token),
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
