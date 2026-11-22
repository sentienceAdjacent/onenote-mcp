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
/// Story 3.12: <c>move_hierarchy_object</c> for sections and section groups (<see cref="MoveContainerWrite"/>), one test or
/// theory per row of the plan's I/O matrix, against <see cref="FakeGateway"/> scripted with three synthetic notebooks:
/// A (local; sections Ideas, Plans, Vault (protected), Locked (protected, locked); groups Archive (section Old, group Deep
/// with section Deeper), Projects (empty), Secure (protected section Hidden); the recycle bin with section Gone), Other
/// (local; section Inbox, group Shelf holding a section named IDEAS) and Cloud (a OneDrive URL; section Drift, group
/// Hangar). The fake applies a move as OneNote does (spike 3.1 m3/m4): the node keeps its ID and leaves its old parent.
/// </summary>
public sealed class MoveContainerToolTests : IDisposable
{
    private const string Lmt = "2026-11-17T10:00:00.000Z";
    private const string OlderLmt = "2026-11-17T09:00:00.000Z";

    private const string AId = "{A0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string APath = @"C:\notebooks\A\";
    private const string IdeasId = "{A1000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string PlansId = "{A2000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string VaultId = "{A3000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string LockedId = "{A4000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string ArchiveId = "{A5000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string OldId = "{A6000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string DeepId = "{A7000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string DeeperId = "{A8000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string ProjectsId = "{A9000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string SecureId = "{AA000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string HiddenId = "{AB000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string BinId = "{AC000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string GoneId = "{AD000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string PageId = "{A2000000-0000-4000-8000-000000000001}{1}{E11}";

    private const string OtherId = "{C0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string OtherPath = @"C:\notebooks\Other\";
    private const string InboxId = "{C1000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string ShelfId = "{C2000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string ClashId = "{C3000000-0000-4000-8000-000000000001}{1}{B0}";

    private const string CloudId = "{D0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string CloudPath = "https://d.docs.live.net/0123abcd/Cloud/";
    private const string DriftId = "{D1000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string HangarId = "{D2000000-0000-4000-8000-000000000001}{1}{B0}";

    /// <summary>Ideas' name appears nowhere else, so a test can show it never reaches a message.</summary>
    private const string IdeasName = "Ideas Zq7";

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

    /// <summary>Every node's attributes, by ID, and its element name; the fake's XML is rebuilt from them.</summary>
    private readonly Dictionary<string, (string Element, string Attributes)> nodes = new(StringComparer.Ordinal);

    /// <summary>Each container's children, in order.</summary>
    private readonly Dictionary<string, List<string>> children = new(StringComparer.Ordinal);

    public MoveContainerToolTests()
    {
        writeLock = new WriteLock($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", holds.Enqueue);
        readAccess = new ReadAccess(gateway, Store.Read, new SettingsWriter(Store, clock), clock);
        Directory.CreateDirectory(temp.Combine("cfg"));

        Node(AId, "Notebook", "A", APath, null, "nickname=\"A\"");
        Node(IdeasId, "Section", IdeasName, @"C:\notebooks\A\Ideas.one", AId);
        Node(PlansId, "Section", "Plans", @"C:\notebooks\A\Plans.one", AId);
        Node(VaultId, "Section", "Vault", @"C:\notebooks\A\Vault.one", AId, "encrypted=\"true\"");
        Node(LockedId, "Section", "Locked", @"C:\notebooks\A\Locked.one", AId, "encrypted=\"true\" locked=\"true\"");
        Node(ArchiveId, "SectionGroup", "Archive", @"C:\notebooks\A\Archive", AId);
        Node(OldId, "Section", "Old", @"C:\notebooks\A\Archive\Old.one", ArchiveId);
        Node(DeepId, "SectionGroup", "Deep", @"C:\notebooks\A\Archive\Deep", ArchiveId);
        Node(DeeperId, "Section", "Deeper", @"C:\notebooks\A\Archive\Deep\Deeper.one", DeepId);
        Node(ProjectsId, "SectionGroup", "Projects", @"C:\notebooks\A\Projects", AId);
        Node(SecureId, "SectionGroup", "Secure", @"C:\notebooks\A\Secure", AId);
        Node(HiddenId, "Section", "Hidden", @"C:\notebooks\A\Secure\Hidden.one", SecureId, "encrypted=\"true\"");
        Node(BinId, "SectionGroup", "OneNote_RecycleBin", @"C:\notebooks\A\OneNote_RecycleBin", AId, "isRecycleBin=\"true\"");
        Node(PageId, "Page", "P1", string.Empty, PlansId);
        Node(GoneId, "Section", "Gone", @"C:\notebooks\A\OneNote_RecycleBin\Gone.one", BinId, "isInRecycleBin=\"true\"");

        Node(OtherId, "Notebook", "Other", OtherPath, null, "nickname=\"Elsewhere\"");
        Node(InboxId, "Section", "Inbox", @"C:\notebooks\Other\Inbox.one", OtherId);
        Node(ShelfId, "SectionGroup", "Shelf", @"C:\notebooks\Other\Shelf", OtherId);
        Node(ClashId, "Section", IdeasName.ToUpperInvariant(), @"C:\notebooks\Other\Shelf\IDEAS ZQ7.one", ShelfId);

        Node(CloudId, "Notebook", "Cloud", CloudPath, null, "nickname=\"Cloud\"");
        Node(DriftId, "Section", "Drift", CloudPath + "Drift.one", CloudId);
        Node(HangarId, "SectionGroup", "Hangar", CloudPath + "Hangar", CloudId);
        Publish();

        // The grant dialog's list of open notebooks (AD-6).
        gateway.Hierarchies[(string.Empty, HierarchyScope.Sections)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebooks {Ns}><one:Notebook {Attributes(AId)} /><one:Notebook {Attributes(OtherId)} /><one:Notebook {Attributes(CloudId)} /></one:Notebooks>";
        ApplyMoves();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SettingsStore Store => new(Path.Combine(temp.Combine("cfg"), "settings.json"), temp.Combine("journal"));

    private string JournalFolder => temp.Combine("journal");

    public void Dispose() => temp.Dispose();

    // --- Moves that go through ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASectionToAGroup_MovesIt_UnderItsOwnId_LastByDefault_WithTwoEntries_OldParentFirst()
    {
        Grant();

        var root = Ok(await Tool().MoveHierarchyObject(IdeasId, ArchiveId, null, Basis(Lmt), Ct));

        var sent = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal(One + "SectionGroup", sent.Name);
        Assert.Equal(ArchiveId, (string?)sent.Attribute("ID"));
        Assert.Equal([OldId, IdeasId], sent.Elements(One + "Section").Select(e => (string?)e.Attribute("ID")));
        Assert.Empty(sent.Elements(One + "SectionGroup"));
        Assert.Empty(SchemaValidation.Problems(gateway.UpdateHierarchyCalls[0]));

        var meta = root.GetProperty("meta");
        Assert.Equal(IdeasId, meta.GetProperty("objectId").GetString());
        Assert.Equal(ArchiveId, meta.GetProperty("parentId").GetString());
        var entries = meta.GetProperty("journalEntryIds").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(entries[0], meta.GetProperty("journalEntryId").GetString());
        Assert.Equal([AId, ArchiveId], entries.Select(e => Entry(e).ObjectId));
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
        Assert.Empty(settles);
        Assert.DoesNotContain(IdeasName, root.GetRawText(), StringComparison.Ordinal);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AGroupToAnotherGroup_MovesIt()
    {
        Grant();

        var root = Ok(await Tool().MoveHierarchyObject(ArchiveId, ProjectsId, null, Basis(Lmt), Ct));

        var sent = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal(ProjectsId, (string?)sent.Attribute("ID"));
        Assert.Equal([ArchiveId], sent.Elements(One + "SectionGroup").Select(e => (string?)e.Attribute("ID")));
        Assert.Equal(ArchiveId, root.GetProperty("meta").GetProperty("objectId").GetString());
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task ASectionToAnotherLocalNotebooksRoot_AtAPosition_MovesIt_WithNoSettle()
    {
        Grant();
        Grant(OtherPath, OtherId);

        Ok(await Tool().MoveHierarchyObject(IdeasId, OtherId, 1, Basis(Lmt), Ct));

        var sent = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal(One + "Notebook", sent.Name);
        Assert.Equal([IdeasId, InboxId], sent.Elements(One + "Section").Select(e => (string?)e.Attribute("ID")));
        Assert.Empty(settles);
    }

    [Theory]
    [InlineData(IdeasId, CloudId)]
    [InlineData(IdeasId, HangarId)]
    [InlineData(DriftId, AId)]
    [InlineData(DriftId, HangarId)]
    public async Task AMoveTouchingACloudNotebook_SettlesTenSecondsUnderTheLock_ThenReadsBack(string objectId, string parentId)
    {
        // J5, widened by the Fable review (M2): out of, into or within a cloud notebook. Local into a cloud group worked
        // twice in spike 3.1 (Implication 15), so it isn't J1's refusal.
        Grant();
        Grant(CloudPath, CloudId);

        var root = Ok(await Tool().MoveHierarchyObject(objectId, parentId, null, Basis(Lmt), Ct));

        Assert.Equal([CloudSettle.Time], settles);
        Assert.Equal(TimeSpan.FromSeconds(10), CloudSettle.Time);
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.Settled, warning.GetProperty("code").GetString());
        Assert.Equal(CloudSettle.SettledUserAction, warning.GetProperty("userAction").GetString());
        Assert.Equal(objectId, root.GetProperty("meta").GetProperty("objectId").GetString());
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task NeitherNotebookGranted_TheDialogAsksOncePerNotebook()
    {
        var prompt = new FakePrompt(Typed("A"), Typed("Elsewhere"));

        Ok(await MoveWithPrompt(prompt, IdeasId, OtherId));

        Assert.Equal(["write", "write"], prompt.Order);
        Assert.Single(gateway.UpdateHierarchyCalls);
    }

    // --- Refusals ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(VaultId)]
    [InlineData(LockedId)]
    [InlineData(SecureId)]
    public async Task AProtectedSection_OrAGroupHoldingOne_IsNotSupported_LockedOrNot_NothingSent(string objectId)
    {
        // AD-7, AD-21 (Q9, Q14, 3.2 H2): checked first, so a locked section is NotSupported, never SectionLocked (Fable M1).
        Grant();

        var error = Error(await Tool().MoveHierarchyObject(objectId, ProjectsId, null, null, Ct), "NotSupported");

        Assert.Equal(MoveContainerWrite.ProtectedUserAction, error.GetProperty("userAction").GetString());
        AssertNothingWritten();
    }

    [Fact]
    public async Task AProtectedSection_IsRefusedBeforeTheRecycleBinCheck()
    {
        Grant();

        Error(await Tool().MoveHierarchyObject(VaultId, BinId, null, null, Ct), "NotSupported");
    }

    [Theory]
    [InlineData(GoneId, ProjectsId)]
    [InlineData(IdeasId, BinId)]
    public async Task TheRecycleBin_IsObjectNotFound_NothingSent(string objectId, string parentId)
    {
        Grant();

        Error(await Tool().MoveHierarchyObject(objectId, parentId, null, null, Ct), "ObjectNotFound");

        AssertNothingWritten();
    }

    [Theory]
    [InlineData(IdeasId, PlansId)]
    [InlineData(IdeasId, PageId)]
    [InlineData(ArchiveId, ArchiveId)]
    [InlineData(ArchiveId, DeepId)]
    public async Task ANewParentThatIsASectionOrPage_OrTheGroupItself_OrInsideIt_IsInvalidArgument(string objectId, string parentId)
    {
        Grant();

        var error = Error(await Tool().MoveHierarchyObject(objectId, parentId, null, null, Ct), "InvalidArgument");

        Assert.Equal(MoveContainerWrite.TargetUserAction, error.GetProperty("userAction").GetString());
        AssertNothingWritten();
    }

    [Fact]
    public async Task ItsOwnParent_IsInvalidArgument_PointingToReorder()
    {
        Grant();

        var error = Error(await Tool().MoveHierarchyObject(OldId, ArchiveId, null, null, Ct), "InvalidArgument");

        Assert.Equal(MoveContainerWrite.SameParentUserAction, error.GetProperty("userAction").GetString());
        AssertNothingWritten();
    }

    [Theory]
    [InlineData(ArchiveId)]
    [InlineData(ShelfId)]
    public async Task OutOfACloudNotebook_IntoAnotherNotebooksGroup_IsNotSupported_ToTheRootFirst(string parentId)
    {
        // J1 (spike 3.1 Implications 5, 15): cloud to local and cloud to cloud both failed 0x80042004.
        Grant();
        Grant(OtherPath, OtherId);
        Grant(CloudPath, CloudId);

        var error = Error(await Tool().MoveHierarchyObject(DriftId, parentId, null, null, Ct), "NotSupported");

        Assert.Equal(MoveContainerWrite.CloudIntoGroupUserAction, error.GetProperty("userAction").GetString());
        AssertNothingWritten();
        Assert.Empty(settles);
    }

    [Fact]
    public async Task ASameKindSiblingsName_IgnoringCase_IsInvalidArgument()
    {
        // 3.12 Q2: Shelf holds a section whose name differs from Ideas' only in case.
        Grant();
        Grant(OtherPath, OtherId);

        var error = Error(await Tool().MoveHierarchyObject(IdeasId, ShelfId, null, null, Ct), "InvalidArgument");

        Assert.Equal(MoveContainerWrite.NameClashUserAction, error.GetProperty("userAction").GetString());
        Assert.DoesNotContain(IdeasName, error.GetRawText(), StringComparison.OrdinalIgnoreCase);
        AssertNothingWritten();
    }

    [Fact]
    public async Task APositionPastTheEnd_IsInvalidArgument()
    {
        // Archive holds one section (Old), so 1 and 2 are the places a section can go.
        Grant();

        var error = Error(await Tool().MoveHierarchyObject(IdeasId, ArchiveId, 3, null, Ct), "InvalidArgument");

        Assert.Equal(MoveContainerWrite.PositionUserAction, error.GetProperty("userAction").GetString());
        AssertNothingWritten();
    }

    [Fact]
    public async Task APositionBelowOne_IsInvalidArgument_BeforeAnyRead()
    {
        var error = Error(await Tool().MoveHierarchyObject(IdeasId, ArchiveId, 0, null, Ct), "InvalidArgument");

        Assert.Equal(MovePageWrite.AnyPositionUserAction, error.GetProperty("userAction").GetString());
        Assert.DoesNotContain(gateway.Calls, c => c.StartNodeId == ArchiveId);
    }

    [Fact]
    public async Task ACloudMoveCutShortDuringTheSettle_StillWarnsSettled_ThenReadBackFailed()
    {
        // Review fix: the settle started, so the user is still told to check the sync.
        Grant();
        Grant(CloudPath, CloudId);
        settleHook = () => throw new OperationCanceledException();

        var root = Ok(await Tool().MoveHierarchyObject(IdeasId, CloudId, null, Basis(Lmt), Ct));

        var codes = root.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ToList();
        Assert.Equal([ToolWarnings.Settled, ToolWarnings.ReadBackFailed], codes);
    }

    [Fact]
    public async Task AnyOtherFailureOnACloudMove_SettlesToo_AndKeepsItsCode()
    {
        // 3.12 Q4: the next write waits whatever the failure.
        Grant();
        Grant(CloudPath, CloudId);
        gateway.OnUpdateHierarchy = null;
        gateway.UpdateHierarchyFailure = Com.HResultMap.ToException("UpdateHierarchy", new System.Runtime.InteropServices.COMException("modal", unchecked((int)0x80042030)));

        Error(await Tool().MoveHierarchyObject(IdeasId, CloudId, null, null, Ct), "ModalDialogOpen");

        Assert.Equal([CloudSettle.Time], settles);
    }

    [Fact]
    public async Task ASectionLandingOnAReadGrantsKey_IsInvalidArgument()
    {
        // AD-21, as a rename (5.4): the moved section would inherit the read grant stored for its new path.
        Grant();
        ReadGrantAt(@"C:\notebooks\A\Archive\Ideas.one");

        var error = Error(await Tool().MoveHierarchyObject(IdeasId, ArchiveId, null, null, Ct), "InvalidArgument");

        Assert.Equal(MoveContainerWrite.GrantLocationUserAction, error.GetProperty("userAction").GetString());
        AssertNothingWritten();
    }

    [Fact]
    public async Task AGroupWhoseSectionWouldLandOnAReadGrantsKey_IsInvalidArgument_WithACount()
    {
        Grant();
        ReadGrantAt(@"C:\notebooks\A\Projects\Archive\Deep\Deeper.one");

        var error = Error(await Tool().MoveHierarchyObject(ArchiveId, ProjectsId, null, null, Ct), "InvalidArgument");

        Assert.Contains("1 section(s)", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        AssertNothingWritten();
    }

    [Theory]
    [InlineData(CloudPath + "Ideas%20Zq7.one")]
    [InlineData("https://d.docs.live.net/0123abcd/Cloud/ideas%20zq7.one/")]
    public async Task ASectionLandingOnAReadGrantsKey_SpelledPercentEncoded_IsInvalidArgument(string storedKey)
    {
        // 3.12 security review (Phill, 2026-11-17): a local-to-cloud move predicts "…/Ideas Zq7.one", while the stored key
        // may spell the same location "…/Ideas%20Zq7.one"; the check compares them decoded.
        Grant();
        Grant(CloudPath, CloudId);
        Node(IdeasId, "Section", IdeasName, @"C:\notebooks\A\Ideas Zq7.one", AId);
        Publish();
        ReadGrantAt(storedKey);

        var error = Error(await Tool().MoveHierarchyObject(IdeasId, CloudId, null, null, Ct), "InvalidArgument");

        Assert.Equal(MoveContainerWrite.GrantLocationUserAction, error.GetProperty("userAction").GetString());
        AssertNothingWritten();
    }

    [Fact]
    public async Task AGroupWhoseSectionWouldLandOnAPercentEncodedReadGrantsKey_IsInvalidArgument()
    {
        Grant();
        Grant(CloudPath, CloudId);
        Node(DeeperId, "Section", "Deeper", @"C:\notebooks\A\Archive\Deep\Deeper One.one", DeepId);
        Publish();
        ReadGrantAt(CloudPath + "Archive/Deep/Deeper%20One.one");

        var error = Error(await Tool().MoveHierarchyObject(ArchiveId, CloudId, null, null, Ct), "InvalidArgument");

        Assert.Contains("1 section(s)", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ASectionThatLandsWhereAReadGrantIsStored_AfterAll_ReadsBackWithReadGrantAtLocation()
    {
        // 3.12 security review (Phill, 2026-11-17): the prediction can be wrong (OneNote may spell the new path its own
        // way), so after the move the real path is re-read and checked against stored grants. The move stands.
        Grant();
        Grant(OtherPath, OtherId);
        ReadGrantAt(@"C:\notebooks\Other\Ideas Zq7 (1).one");
        RealPathAfterMove(IdeasId, @"C:\notebooks\Other\Ideas Zq7 (1).one");

        var root = Ok(await Tool().MoveHierarchyObject(IdeasId, OtherId, null, Basis(Lmt), Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.ReadGrantAtLocation, warning.GetProperty("code").GetString());
        Assert.Equal(MoveContainerWrite.ReadGrantAtLocationUserAction, warning.GetProperty("userAction").GetString());
        Assert.Contains(IdeasId, warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Zq7", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task AGroupWhoseSectionLandsWhereAPercentEncodedReadGrantIsStored_ReadsBackWithReadGrantAtLocation_WithACount()
    {
        Grant();
        Grant(CloudPath, CloudId);
        ReadGrantAt(CloudPath + "Archive%20Moved/Deep/Deeper.one");
        RealPathAfterMove(DeeperId, CloudPath + "Archive Moved/Deep/Deeper.one");

        var root = Ok(await Tool().MoveHierarchyObject(ArchiveId, CloudId, null, Basis(Lmt), Ct));

        var codes = root.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ToList();
        Assert.Equal([ToolWarnings.Settled, ToolWarnings.ReadGrantAtLocation], codes);
        Assert.Contains("1 section(s)", root.GetProperty("warnings")[1].GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExpiredReadGrantAtTheNewKey_DoesntBlockTheMove()
    {
        Grant();
        ReadGrantAt(@"C:\notebooks\A\Archive\Ideas.one", clock.GetUtcNow().AddHours(-1));

        Ok(await Tool().MoveHierarchyObject(IdeasId, ArchiveId, null, null, Ct));
    }

    [Fact]
    public async Task ANewParentNotGranted_WithoutElicitation_IsWriteNotGranted_NothingSent()
    {
        Grant();

        var error = Error(await Tool().MoveHierarchyObject(IdeasId, OtherId, null, null, Ct), "WriteNotGranted");

        Assert.Contains(OtherId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        AssertNothingWritten();
    }

    [Fact]
    public async Task ANotebookChangedUnderTheLock_IsWriteNotGranted_Retryable_NothingSent()
    {
        // AD-7: step 1 granted A for Ideas; by step 3 Ideas sits in Other (also granted).
        Grant();
        Grant(OtherPath, OtherId);
        var steps = 0;
        gateway.OnGetHierarchy = (id, scope) =>
        {
            // Step 1 reads Ideas at self twice (the page walk's check, then the state); the third is step 3's.
            if (id == IdeasId && scope == HierarchyScope.Self && ++steps == 3)
            {
                gateway.Parents[IdeasId] = OtherId;
            }
        };

        var error = Error(await Tool().MoveHierarchyObject(IdeasId, ArchiveId, null, Basis(Lmt), Ct), "WriteNotGranted");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(MoveContainerWrite.NotebookChangedUserAction, error.GetProperty("userAction").GetString());
        AssertNothingWritten();
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AStaleBasis_IsStaleWrite_NothingSent()
    {
        Grant();

        var error = Error(await Tool().MoveHierarchyObject(IdeasId, ArchiveId, null, Basis(OlderLmt), Ct), "StaleWrite");

        Assert.Equal(MoveContainerWrite.StaleUserAction, error.GetProperty("userAction").GetString());
        AssertNothingWritten();
    }

    [Fact]
    public async Task NoBasis_MovesWithNoBasis()
    {
        Grant();

        var root = Ok(await Tool().MoveHierarchyObject(IdeasId, ArchiveId, null, null, Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.NoBasis, warning.GetProperty("code").GetString());
    }

    // --- COM failures and the read-back ----------------------------------------------------------------------------

    [Fact]
    public async Task APartialCopy_IsObjectNotFound_OutcomeUnknown_AndSaysWhatTheReReadFound()
    {
        const string CopyId = "{AF000000-0000-4000-8000-000000000001}{1}{B0}";
        Grant();
        gateway.OnUpdateHierarchy = null;
        gateway.UpdateHierarchyFailure = Com.HResultMap.ToException("UpdateHierarchy", new System.Runtime.InteropServices.COMException("refused", unchecked((int)0x80042004)));
        gateway.OnUpdateHierarchyCall = _ =>
        {
            // A copy in the target, the section still in place (spike 3.1 m3-local-to-root, one run).
            Node(CopyId, "Section", IdeasName, @"C:\notebooks\A\Archive\" + IdeasName + ".one", ArchiveId);
            Publish();
            return Task.CompletedTask;
        };

        var error = Error(await Tool().MoveHierarchyObject(IdeasId, ArchiveId, null, Basis(Lmt), Ct), "ObjectNotFound");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Equal("0x80042004", error.GetProperty("hresult").GetString());
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains($"section {IdeasId} is still in notebook {AId}", message, StringComparison.Ordinal);
        Assert.Contains($"1 new section(s) with its name ({CopyId})", message, StringComparison.Ordinal);
        Assert.Contains("journal entries", message, StringComparison.Ordinal);
        Assert.DoesNotContain(IdeasName, error.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(settles);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task APartialCopy_OnACloudMove_SettlesBeforeTheReRead()
    {
        // 3.12 Q4.
        Grant();
        Grant(CloudPath, CloudId);
        gateway.OnUpdateHierarchy = null;
        gateway.UpdateHierarchyFailure = Com.HResultMap.ToException("UpdateHierarchy", new System.Runtime.InteropServices.COMException("refused", unchecked((int)0x80042004)));
        var readsBeforeSettle = -1;
        settleHook = () => readsBeforeSettle = gateway.Calls.Count;

        Error(await Tool().MoveHierarchyObject(IdeasId, CloudId, null, null, Ct), "ObjectNotFound");

        Assert.Equal([CloudSettle.Time], settles);
        Assert.Equal([(AId, HierarchyScope.Children), (CloudId, HierarchyScope.Children)], gateway.Calls.Skip(readsBeforeSettle));
    }

    [Fact]
    public async Task AGroupMoveFailingFolderMissing_IsNotSupported_HResultKept_NotOutcomeUnknown()
    {
        // J3: a section group to a cloud notebook's root failed 0x80042018 and moved nothing (spike 3.1 m4, one run).
        Grant();
        Grant(CloudPath, CloudId);
        gateway.OnUpdateHierarchy = null;
        gateway.UpdateHierarchyFailure = Com.HResultMap.ToException("UpdateHierarchy", new System.Runtime.InteropServices.COMException("no folder", unchecked((int)0x80042018)));

        var error = Error(await Tool().MoveHierarchyObject(ProjectsId, CloudId, null, null, Ct), "NotSupported");

        Assert.Equal("0x80042018", error.GetProperty("hresult").GetString());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Equal(MoveContainerWrite.GroupFolderUserAction, error.GetProperty("userAction").GetString());
        Assert.Contains("journal entries", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASectionMoveFailingFolderMissing_KeepsTheMapsCode()
    {
        Grant();
        gateway.OnUpdateHierarchy = null;
        gateway.UpdateHierarchyFailure = Com.HResultMap.ToException("UpdateHierarchy", new System.Runtime.InteropServices.COMException("no folder", unchecked((int)0x80042018)));
        var expected = ((OneNoteException)gateway.UpdateHierarchyFailure).Code.ToString();

        var error = Error(await Tool().MoveHierarchyObject(IdeasId, ArchiveId, null, null, Ct), expected);

        Assert.NotEqual(MoveContainerWrite.GroupFolderUserAction, error.GetProperty("userAction").GetString());
    }

    [Fact]
    public async Task AReadBackThatDoesntFindTheMove_IsReadBackDiffers_Twice()
    {
        Grant();
        gateway.OnUpdateHierarchy = null;

        var root = Ok(await Tool().MoveHierarchyObject(IdeasId, ArchiveId, null, Basis(Lmt), Ct));

        var codes = root.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ToList();
        Assert.Equal([ToolWarnings.ReadBackDiffers, ToolWarnings.ReadBackDiffers], codes);
    }

    [Fact]
    public async Task AReadBackThatFails_IsReadBackFailed_AfterTheSettle_AndTheMoveStands()
    {
        Grant();
        Grant(CloudPath, CloudId);
        settleHook = () => gateway.Hierarchies.Remove((CloudId, HierarchyScope.Children));

        var root = Ok(await Tool().MoveHierarchyObject(IdeasId, CloudId, null, Basis(Lmt), Ct));

        var codes = root.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ToList();
        Assert.Equal([ToolWarnings.Settled, ToolWarnings.ReadBackFailed], codes);
        Assert.Single(gateway.UpdateHierarchyCalls);
        await AssertLockFreeAsync();
    }

    // --- Helpers ----------------------------------------------------------------------------------------------------

    private Action? settleHook;

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

    private string Attributes(string id) => nodes[id].Attributes;

    /// <summary>Rewrites every node's self, children and sections reads from <see cref="nodes"/> and <see cref="children"/>.</summary>
    private void Publish()
    {
        foreach (var (id, (element, attributes)) in nodes)
        {
            gateway.Hierarchies[(id, HierarchyScope.Self)] = $"<?xml version=\"1.0\"?>\n<one:{element} {Ns} {attributes} />";
            var kids = string.Concat(children[id].Select(k => $"<one:{nodes[k].Element} {nodes[k].Attributes} />"));
            gateway.Hierarchies[(id, HierarchyScope.Children)] = $"<?xml version=\"1.0\"?>\n<one:{element} {Ns} {attributes}>{kids}</one:{element}>";
            gateway.Hierarchies[(id, HierarchyScope.Sections)] = $"<?xml version=\"1.0\"?>\n<one:{element} {Ns} {attributes}>{Below(id)}</one:{element}>";
        }
    }

    private string Below(string id) => string.Concat(children[id].Select(k => $"<one:{nodes[k].Element} {nodes[k].Attributes}>{Below(k)}</one:{nodes[k].Element}>"));

    /// <summary>Makes the fake apply a move as OneNote does (spike 3.1 m3/m4): the node keeps its ID and changes parent.</summary>
    private void ApplyMoves() => gateway.OnUpdateHierarchy = sent =>
    {
        var root = XElement.Parse(sent);
        var target = (string)root.Attribute("ID")!;
        var listed = root.Elements().Select(e => (string)e.Attribute("ID")!).ToList();
        var moved = listed.Single(id => !children[target].Contains(id));
        children[gateway.Parents[moved]].Remove(moved);
        var others = children[target].Where(k => !listed.Contains(k)).ToList();
        children[target] = [.. listed, .. others];
        gateway.Parents[moved] = target;
        Publish();
    };

    /// <summary>After the fake applies the move, <paramref name="id"/> reads back at <paramref name="path"/>, as OneNote names it.</summary>
    private void RealPathAfterMove(string id, string path)
    {
        var apply = gateway.OnUpdateHierarchy!;
        gateway.OnUpdateHierarchy = sent =>
        {
            apply(sent);
            var (element, attributes) = nodes[id];
            var before = System.Text.RegularExpressions.Regex.Match(attributes, "path=\"[^\"]*\"").Value;
            nodes[id] = (element, attributes.Replace(before, $"path=\"{path}\"", StringComparison.Ordinal));
            Publish();
        };
    }

    private void Grant(string path = APath, string id = AId) =>
        new SettingsWriter(Store, clock).SaveWriteGrant(
            new NotebookGrant(GrantKeys.NotebookKey(path), "A", id, Write: true, PermanentDelete: false, clock.GetUtcNow(), GrantedVia.Cli));

    private void ReadGrantAt(string sectionPath, DateTimeOffset? expiresAt = null) =>
        new SettingsWriter(Store, clock).SaveReadGrant(new ProtectedSectionGrant(
            GrantKeys.SectionKey(sectionPath), "Earlier", GrantKeys.NotebookKey(APath), GoneId, clock.GetUtcNow(), GrantedVia.Cli, expiresAt));

    private static PageBasisArgument Basis(string date) => new(date);

    private static GrantPromptResult Typed(string name) => new(GrantPromptStatus.Accepted, name);

    private JournalEntry Entry(string entryId) =>
        Directory.GetFiles(JournalFolder)
            .Where(f => JournalFiles.IsEntry(Path.GetFileName(f)))
            .Select(JournalStore.Read)
            .Single(e => e.EntryId == Guid.Parse(entryId));

    private void AssertNothingWritten()
    {
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.True(!Directory.Exists(JournalFolder) || !Directory.GetFiles(JournalFolder).Any(f => JournalFiles.IsEntry(Path.GetFileName(f))));
    }

    private MoveHierarchyObjectUseCase UseCase()
    {
        var services = new HierarchyWriteServices(
            gateway,
            new WriteAccess(gateway, Store.Read),
            readAccess,
            new HierarchySnapshot(gateway, new JournalStore(Store.Read), clock));
        return new MoveHierarchyObjectUseCase(
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

    private MoveHierarchyObjectTool Tool() => new(Runner(), UseCase());

    private Task<CallToolResult> MoveWithPrompt(IGrantPrompt prompt, string objectId, string parentId)
    {
        var useCase = UseCase();
        return Runner().RunAsync(
            MoveHierarchyObjectTool.Name,
            useCase,
            (context, token) => useCase.MoveAsync(context, objectId, parentId, null, null, token),
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
