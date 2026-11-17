using System.Collections.Concurrent;
using System.IO.Compression;
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
/// Story 3.4: <c>move_hierarchy_object</c> for pages, through the AD-7 write pipeline as one <c>HierarchyWrite</c>, one test
/// or theory per row of the plan's I/O matrix, against <see cref="FakeGateway"/> scripted with a synthetic notebook A
/// (sections Ideas and Plans, Vault (protected), Locked (protected, locked), group Archive, the recycle bin with Deleted
/// Pages and a recycled section) and a second notebook Other (shown as Elsewhere) with section Inbox, a temporary
/// <c>settings.json</c> and its journal. A page in Locked is listed by its parent chain only, as OneNote hides a locked
/// section's pages (3.3 sitting S1). IDs are well-formed OneNote IDs, because the XML the server sends must pass the XSD.
/// </summary>
public sealed class MoveHierarchyObjectToolTests : IDisposable
{
    private const string Lmt = "2026-11-16T10:00:00.000Z";
    private const string OlderLmt = "2026-11-16T09:00:00.000Z";

    private const string NotebookId = "{A0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string NotebookPath = @"C:\notebooks\A\";
    private const string IdeasId = "{A1000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string PlansId = "{A2000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string VaultId = "{A3000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string LockedId = "{A4000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string ArchiveId = "{A5000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string BinId = "{A6000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string DeletedPagesId = "{A7000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string BinnedId = "{A8000000-0000-4000-8000-000000000001}{1}{B0}";

    private const string P1 = "{A1000000-0000-4000-8000-000000000001}{1}{E11}";
    private const string P1a = "{A1000000-0000-4000-8000-000000000001}{1}{E12}";
    private const string P1b = "{A1000000-0000-4000-8000-000000000001}{1}{E13}";
    private const string P2 = "{A1000000-0000-4000-8000-000000000001}{1}{E21}";
    private const string P3 = "{A1000000-0000-4000-8000-000000000001}{1}{E31}";
    private const string Q1 = "{A2000000-0000-4000-8000-000000000001}{1}{E11}";
    private const string Q1a = "{A2000000-0000-4000-8000-000000000001}{1}{E12}";
    private const string Q2 = "{A2000000-0000-4000-8000-000000000001}{1}{E21}";
    private const string V1 = "{A3000000-0000-4000-8000-000000000001}{1}{E11}";
    private const string L1 = "{A4000000-0000-4000-8000-000000000001}{1}{E11}";
    private const string D1 = "{A7000000-0000-4000-8000-000000000001}{1}{E11}";

    /// <summary>The ID the fake gives a moved page (OneNote gives it a new one, spike 3.1 M1).</summary>
    private const string NewId = "{A9000000-0000-4000-8000-000000000001}{1}{E99}";

    private const string OtherId = "{C0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string OtherPath = @"C:\notebooks\Other\";
    private const string InboxId = "{C1000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string O1 = "{C1000000-0000-4000-8000-000000000001}{1}{E11}";

    /// <summary>The page names; P2's appears nowhere else, so a test can show it never reaches a message.</summary>
    private const string P2Name = "Budget Zq7";

    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";

    private static readonly XNamespace One = OneNoteXml.One;

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly FakeGateway gateway = new();
    private readonly ConcurrentQueue<WriteLockHeld> holds = new();
    private readonly WriteLock writeLock;
    private readonly ReadAccess readAccess;
    private readonly PageTextCache cache = PageTextCache.WithDefaults();

    public MoveHierarchyObjectToolTests()
    {
        writeLock = new WriteLock($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", holds.Enqueue);
        readAccess = new ReadAccess(gateway, Store.Read, new SettingsWriter(Store, clock), clock);
        Directory.CreateDirectory(temp.Combine("cfg"));

        Parent(NotebookId, string.Empty);
        foreach (var id in new[] { IdeasId, PlansId, VaultId, LockedId, ArchiveId, BinId })
        {
            Parent(id, NotebookId);
        }

        Parent(DeletedPagesId, BinId);
        Parent(BinnedId, BinId);
        foreach (var id in new[] { P1, P1a, P1b, P2, P3 })
        {
            Parent(id, IdeasId);
        }

        foreach (var id in new[] { Q1, Q1a, Q2 })
        {
            Parent(id, PlansId);
        }

        Parent(V1, VaultId);
        Parent(L1, LockedId);
        Parent(D1, DeletedPagesId);
        Parent(OtherId, string.Empty);
        Parent(InboxId, OtherId);
        Parent(O1, InboxId);

        const string notebook = $"name=\"A\" nickname=\"A\" ID=\"{NotebookId}\" path=\"{NotebookPath}\" lastModifiedTime=\"{Lmt}\"";
        const string ideas = $"name=\"Ideas\" ID=\"{IdeasId}\" path=\"C:\\notebooks\\A\\Ideas.one\" lastModifiedTime=\"{Lmt}\"";
        const string plans = $"name=\"Plans\" ID=\"{PlansId}\" path=\"C:\\notebooks\\A\\Plans.one\" lastModifiedTime=\"{Lmt}\"";
        const string vault = $"name=\"Vault\" ID=\"{VaultId}\" path=\"C:\\notebooks\\A\\Vault.one\" encrypted=\"true\" lastModifiedTime=\"{Lmt}\"";
        const string locked = $"name=\"Locked\" ID=\"{LockedId}\" path=\"C:\\notebooks\\A\\Locked.one\" encrypted=\"true\" locked=\"true\" lastModifiedTime=\"{Lmt}\"";
        const string archive = $"name=\"Archive\" ID=\"{ArchiveId}\" path=\"C:\\notebooks\\A\\Archive\" lastModifiedTime=\"{Lmt}\"";
        const string bin = $"name=\"OneNote_RecycleBin\" ID=\"{BinId}\" path=\"C:\\notebooks\\A\\OneNote_RecycleBin\" isRecycleBin=\"true\" lastModifiedTime=\"{Lmt}\"";
        const string deleted = $"name=\"Deleted Pages\" ID=\"{DeletedPagesId}\" path=\"C:\\notebooks\\A\\OneNote_RecycleBin\\OneNote_DeletedPages.one\" isInRecycleBin=\"true\" isDeletedPages=\"true\" lastModifiedTime=\"{Lmt}\"";
        const string binned = $"name=\"Gone\" ID=\"{BinnedId}\" path=\"C:\\notebooks\\A\\OneNote_RecycleBin\\Gone.one\" isInRecycleBin=\"true\" lastModifiedTime=\"{Lmt}\"";

        Self(NotebookId, $"<one:Notebook {Ns} {notebook} />");
        Self(IdeasId, $"<one:Section {Ns} {ideas} />");
        Self(PlansId, $"<one:Section {Ns} {plans} />");
        Self(VaultId, $"<one:Section {Ns} {vault} />");
        Self(LockedId, $"<one:Section {Ns} {locked} />");
        Self(ArchiveId, $"<one:SectionGroup {Ns} {archive} />");
        Self(BinId, $"<one:SectionGroup {Ns} {bin} />");
        Self(DeletedPagesId, $"<one:Section {Ns} {deleted} />");
        Self(BinnedId, $"<one:Section {Ns} {binned} />");
        Self(P1, $"<one:Page {Ns} ID=\"{P1}\" name=\"P1\" pageLevel=\"1\" />");

        // L1 has no self read: OneNote hides a locked section's pages, while the parent chain still answers (3.3 S1).
        Children(NotebookId, $"<one:Notebook {Ns} {notebook}><one:Section {ideas} /><one:Section {plans} /><one:Section {vault} /><one:Section {locked} /><one:SectionGroup {archive} /><one:SectionGroup {bin} /></one:Notebook>");
        Children(ArchiveId, $"<one:SectionGroup {Ns} {archive} />");
        Children(BinId, $"<one:SectionGroup {Ns} {bin}><one:Section {deleted} /><one:Section {binned} /></one:SectionGroup>");
        Children(IdeasId, $"<one:Section {Ns} {ideas}>{Page(P1, "P1", 1)}{Page(P1a, "P1a", 2)}{Page(P1b, "P1b", 2)}{Page(P2, P2Name, 1)}{Page(P3, "P3", 1)}</one:Section>");
        Children(PlansId, $"<one:Section {Ns} {plans}>{Page(Q1, "Q1", 1)}{Page(Q1a, "Q1a", 2)}{Page(Q2, "Q2", 1)}</one:Section>");
        Children(VaultId, $"<one:Section {Ns} {vault}>{Page(V1, "V1", 1)}</one:Section>");
        Children(LockedId, $"<one:Section {Ns} {locked} />");
        Children(DeletedPagesId, $"<one:Section {Ns} {deleted}>{Page(D1, "D1", 1)}</one:Section>");
        Children(BinnedId, $"<one:Section {Ns} {binned} />");
        Children(P1, $"<one:Page {Ns} ID=\"{P1}\" name=\"P1\" pageLevel=\"1\" />");

        const string other = $"name=\"Other\" nickname=\"Elsewhere\" ID=\"{OtherId}\" path=\"{OtherPath}\" lastModifiedTime=\"{Lmt}\"";
        const string inbox = $"name=\"Inbox\" ID=\"{InboxId}\" path=\"C:\\notebooks\\Other\\Inbox.one\" lastModifiedTime=\"{Lmt}\"";
        Self(OtherId, $"<one:Notebook {Ns} {other} />");
        Self(InboxId, $"<one:Section {Ns} {inbox} />");
        Children(InboxId, $"<one:Section {Ns} {inbox}>{Page(O1, "O1", 1)}</one:Section>");

        // The grant dialog's list of open notebooks (AD-6).
        gateway.Hierarchies[(string.Empty, HierarchyScope.Sections)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebooks {Ns}><one:Notebook {notebook}><one:Section {ideas} /></one:Notebook><one:Notebook {other}><one:Section {inbox} /></one:Notebook></one:Notebooks>";
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The journal folder is the test's own: never the user's real journal under %LOCALAPPDATA%.
    private SettingsStore Store => new(Path.Combine(temp.Combine("cfg"), "settings.json"), temp.Combine("journal"));

    private string JournalFolder => temp.Combine("journal");

    public void Dispose() => temp.Dispose();

    // --- Same notebook, other notebook, subpages ----------------------------------------------------------------------

    [Fact]
    public async Task SameNotebook_MovesThePage_LastByDefault_WithANewId_AndTwoEntries_SourceThenTarget()
    {
        Grant();
        ApplyMoves();
        var entriesAtCall = -1;
        gateway.OnUpdateHierarchyCall = _ =>
        {
            entriesAtCall = EntryFiles().Count;
            return Task.CompletedTask;
        };
        cache.Set(P2, DateTimeOffset.UnixEpoch, "cached text", long.MaxValue);

        var root = Ok(await Tool().MoveHierarchyObject(P2, PlansId, null, Basis(Lmt), Ct));

        var meta = root.GetProperty("meta");
        Assert.Equal(["correlationId", "journalEntryId", "journalEntryIds", "objectId", "parentId"], meta.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(NewId, meta.GetProperty("objectId").GetString());
        Assert.Equal(PlansId, meta.GetProperty("parentId").GetString());
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());

        // One call: the target's XML, every page with its level, the moved page last at level 1 (M1 shape a; 5.4 L6).
        var sent = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal(One + "Section", sent.Name);
        Assert.Equal(PlansId, (string?)sent.Attribute("ID"));
        Assert.Equal([Q1, Q1a, Q2, P2], sent.Elements(One + "Page").Select(p => (string?)p.Attribute("ID")));
        Assert.Equal(["1", "2", "1", "1"], sent.Elements(One + "Page").Select(p => (string?)p.Attribute("pageLevel")));
        Assert.Empty(SchemaValidation.Problems(Assert.Single(gateway.UpdateHierarchyCalls)));

        // AD-8: both entries stored before the call; journalEntryId is the source's, journalEntryIds source then target (Q1).
        Assert.Equal(2, entriesAtCall);
        var ids = meta.GetProperty("journalEntryIds").EnumerateArray().Select(e => Guid.Parse(e.GetString()!)).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids[0], Guid.Parse(meta.GetProperty("journalEntryId").GetString()!));
        var source = JournalStore.Read(JournalFiles.Find(JournalFolder, ids[0])!);
        var target = JournalStore.Read(JournalFiles.Find(JournalFolder, ids[1])!);
        Assert.Equal((IdeasId, PlansId), (source.ObjectId, target.ObjectId));
        Assert.All(new[] { source, target }, e =>
        {
            Assert.Equal(JournalEntry.HierarchyKind, e.SnapshotKind);
            Assert.Equal(MovePageWrite.OperationName, e.Operation);
            Assert.Equal(nameof(OperationClass.HierarchyWrite), e.OperationClass);
            Assert.Null(e.SectionKey);
        });
        Assert.Contains(P2, source.Xml, StringComparison.Ordinal);

        // AD-7, AD-12: the old page ID left the search cache; the lock is free.
        Assert.False(cache.TryGet(P2, DateTimeOffset.UnixEpoch, out _));
        Assert.Equal(meta.GetProperty("correlationId").GetString(), Assert.Single(holds).CorrelationId);
        Assert.DoesNotContain(P2Name, root.GetRawText(), StringComparison.Ordinal);
        await AssertLockFreeAsync();
    }

    [Theory]
    [InlineData(1, new[] { "P2", "Q1", "Q1a", "Q2" })]
    [InlineData(3, new[] { "Q1", "Q1a", "P2", "Q2" })]
    [InlineData(4, new[] { "Q1", "Q1a", "Q2", "P2" })]
    public async Task APosition_PutsThePageBeforeThePageNowThere(int position, string[] order)
    {
        Grant();
        var ids = new Dictionary<string, string> { ["P2"] = P2, ["Q1"] = Q1, ["Q1a"] = Q1a, ["Q2"] = Q2 };

        Ok(await Tool().MoveHierarchyObject(P2, PlansId, position, Basis(Lmt), Ct));

        var sent = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal(order.Select(o => ids[o]), sent.Elements(One + "Page").Select(p => (string?)p.Attribute("ID")));
    }

    [Fact]
    public async Task OtherNotebook_BothGranted_MovesThePage_WithANewId()
    {
        Grant();
        Grant(OtherPath, OtherId);
        ApplyMoves();

        var root = Ok(await Tool().MoveHierarchyObject(P2, InboxId, 1, Basis(Lmt), Ct));

        Assert.Equal(NewId, root.GetProperty("meta").GetProperty("objectId").GetString());
        Assert.Equal(InboxId, root.GetProperty("meta").GetProperty("parentId").GetString());
        var sent = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal([P2, O1], sent.Elements(One + "Page").Select(p => (string?)p.Attribute("ID")));
        Assert.Equal(2, EntryFiles().Count);
        var target = JournalStore.Read(JournalFiles.Find(JournalFolder, Guid.Parse(root.GetProperty("meta").GetProperty("journalEntryIds")[1].GetString()!))!);
        Assert.Equal(OtherId, target.NotebookId);
    }

    [Fact]
    public async Task APageWithSubpages_MovesAlone_AndWarnsSubpagesLeftBehind()
    {
        Grant();
        ApplyMoves();

        var root = Ok(await Tool().MoveHierarchyObject(P1, PlansId, null, Basis(Lmt), Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.SubpagesLeftBehind, warning.GetProperty("code").GetString());
        Assert.Contains("2 subpage(s)", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains(IdeasId, warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(NewId, root.GetProperty("meta").GetProperty("objectId").GetString());

        // Only the page itself is sent; its subpages aren't (OneNote leaves them in the source).
        var sent = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.DoesNotContain(sent.Elements(One + "Page"), p => (string?)p.Attribute("ID") is P1a or P1b);
    }

    [Fact]
    public async Task ASubpage_MovesAtLevel1()
    {
        // 3.4 Q4: a subpage may move; it lands at level 1.
        Grant();

        Ok(await Tool().MoveHierarchyObject(P1a, PlansId, null, Basis(Lmt), Ct));

        var moved = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls)).Elements(One + "Page").Last();
        Assert.Equal(P1a, (string?)moved.Attribute("ID"));
        Assert.Equal("1", (string?)moved.Attribute("pageLevel"));
    }

    // --- Grants -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATargetNotGranted_WithoutElicitation_IsWriteNotGranted_NothingSent()
    {
        Grant();

        var error = Error(await Tool().MoveHierarchyObject(P2, InboxId, null, Basis(Lmt), Ct), "WriteNotGranted");

        Assert.Contains(OtherId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task NeitherNotebookGranted_TheDialogAsksOncePerNotebook_PageFirst()
    {
        // 3.4 Q3: the pipeline raises the grant dialog at most once per notebook key, so a move can ask twice.
        var prompt = new FakePrompt(Typed("A"), Typed("Elsewhere"));

        Ok(await MoveWithPrompt(prompt, P2, InboxId));

        Assert.Equal(["write", "write"], prompt.Order);
        Assert.Single(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task ADeclinedDialog_IsWriteNotGranted_AskedOnce()
    {
        var prompt = new FakePrompt(new GrantPromptResult(GrantPromptStatus.Declined));

        Error(await MoveWithPrompt(prompt, P2, PlansId), "WriteNotGranted");

        Assert.Single(prompt.WriteQuestions);
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task ANotebookChangedUnderTheLock_IsWriteNotGranted_Retryable_NothingSent()
    {
        // AD-7: step 1 granted A; by step 3 the page's section sits in Other (also granted), so the grant step 1 checked
        // no longer covers it.
        Grant();
        Grant(OtherPath, OtherId);
        var pending = true;
        gateway.OnGetHierarchy = (id, scope) =>
        {
            // Step 1's last read is the target's; the lock comes next.
            if (pending && id == PlansId && scope == HierarchyScope.Children)
            {
                pending = false;
                Parent(IdeasId, OtherId);
            }
        };

        var error = Error(await Tool().MoveHierarchyObject(P2, PlansId, null, Basis(Lmt), Ct), "WriteNotGranted");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(MovePageWrite.NotebookChangedUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ATargetNotebookChangedUnderTheLock_IsWriteNotGranted_Retryable_NothingSent()
    {
        // AD-7, the target half: step 1 granted the target in A; by step 3 the target section sits in Other (also granted).
        Grant();
        Grant(OtherPath, OtherId);
        var pending = true;
        gateway.OnGetHierarchy = (id, scope) =>
        {
            // Step 1's last read is the target's; the lock comes next.
            if (pending && id == PlansId && scope == HierarchyScope.Children)
            {
                pending = false;
                Parent(PlansId, OtherId);
            }
        };

        var error = Error(await Tool().MoveHierarchyObject(P2, PlansId, null, Basis(Lmt), Ct), "WriteNotGranted");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(MovePageWrite.NotebookChangedUserAction, error.GetProperty("userAction").GetString());
        Assert.StartsWith($"Target {PlansId}", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
        await AssertLockFreeAsync();
    }

    // --- Protected, locked, recycle bin -------------------------------------------------------------------------------

    [Theory]
    [InlineData(V1)]
    [InlineData(L1)]
    public async Task AProtectedSource_LockedOrNot_IsNotSupported_NothingSent(string pageId)
    {
        Grant();

        var error = Error(await Tool().MoveHierarchyObject(pageId, PlansId, null, null, Ct), "NotSupported");

        Assert.Equal(MovePageWrite.ProtectedUserAction, error.GetProperty("userAction").GetString());
        Assert.DoesNotContain("Vault", error.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(holds);
    }

    [Fact]
    public async Task ALockedTarget_IsSectionLocked_NothingSent()
    {
        Grant();

        Error(await Tool().MoveHierarchyObject(P2, LockedId, null, Basis(Lmt), Ct), "SectionLocked");

        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task AnUnlockedProtectedTarget_NeedsOnlyTheWriteGrant_AndItsEntryIsEncrypted()
    {
        Grant();
        ApplyMoves();

        var root = Ok(await Tool().MoveHierarchyObject(P2, VaultId, null, Basis(Lmt), Ct));

        Assert.Equal(NewId, root.GetProperty("meta").GetProperty("objectId").GetString());
        var targetId = Guid.Parse(root.GetProperty("meta").GetProperty("journalEntryIds")[1].GetString()!);
        var file = JournalFiles.Find(JournalFolder, targetId)!;
        Assert.Equal(GrantKeys.SectionKey(@"C:\notebooks\A\Vault.one"), JournalStore.Read(file).SectionKey);
        Assert.DoesNotContain(V1, RawEntryJson(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(D1, PlansId)]
    [InlineData(P2, BinId)]
    [InlineData(P2, BinnedId)]
    [InlineData(P2, DeletedPagesId)]
    public async Task TheRecycleBin_IsObjectNotFound_NothingSent(string pageId, string parentId)
    {
        Grant();

        Error(await Tool().MoveHierarchyObject(pageId, parentId, null, null, Ct), "ObjectNotFound");

        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(holds);
    }

    // --- Wrong kinds, same section, positions, stale -----------------------------------------------------------------

    [Theory]
    [InlineData(IdeasId)]
    [InlineData(ArchiveId)]
    public async Task ASectionOrGroup_IsNotSupported_PagesOnly_BeforeAnyGrant(string objectId)
    {
        var prompt = new FakePrompt(Typed("A"));

        var error = Error(await MoveWithPrompt(prompt, objectId, PlansId), "NotSupported");

        Assert.Equal(MovePageWrite.PagesOnlyUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(prompt.Order);
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task ANotebookAsObjectId_IsInvalidArgument()
    {
        Grant();

        Error(await Tool().MoveHierarchyObject(NotebookId, PlansId, null, null, Ct), "InvalidArgument");

        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Theory]
    [InlineData(ArchiveId)]
    [InlineData(NotebookId)]
    [InlineData(P1)]
    public async Task ATargetThatIsntASection_IsInvalidArgument(string parentId)
    {
        Grant();

        var error = Error(await Tool().MoveHierarchyObject(P2, parentId, null, Basis(Lmt), Ct), "InvalidArgument");

        Assert.Equal(MovePageWrite.TargetUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(holds);
    }

    [Fact]
    public async Task ThePagesOwnSection_IsInvalidArgument_PointingToReorder()
    {
        Grant();

        var error = Error(await Tool().MoveHierarchyObject(P2, IdeasId, null, Basis(Lmt), Ct), "InvalidArgument");

        Assert.Contains("reorder_hierarchy_children", error.GetProperty("userAction").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(5)]
    [InlineData(2)]
    public async Task ABadPosition_IsInvalidArgument(int position)
    {
        // Plans has 3 pages: 0 and below, past count + 1 (5), and 2 (Q1a, a subpage) are refused.
        Grant();

        var error = Error(await Tool().MoveHierarchyObject(P2, PlansId, position, Basis(Lmt), Ct), "InvalidArgument");

        Assert.Equal(MovePageWrite.PositionUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(holds);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(2)]
    public async Task APositionError_NeverCarriesThePageCount(int position)
    {
        // 3.4 security review (Phill, 2026-11-17): a position refusal says only that it is out of range or at a subpage.
        Grant();

        var error = Error(await Tool().MoveHierarchyObject(P2, PlansId, position, Basis(Lmt), Ct), "InvalidArgument");

        var message = error.GetProperty("message").GetString()!;
        Assert.DoesNotContain("page(s)", message, StringComparison.Ordinal);
        Assert.DoesNotContain("has 3", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    public async Task AProtectedTarget_WithAPosition_WithoutAReadGrant_IsReadNotGranted_NothingSent(int position)
    {
        // 3.4 security review (Phill, 2026-11-17): a position's errors would show the protected section's page count and
        // where its subpages sit, so without a read grant only a missing position (last) is accepted.
        Grant();

        var error = Error(await Tool().MoveHierarchyObject(P2, VaultId, position, Basis(Lmt), Ct), "ReadNotGranted");

        var message = error.GetProperty("message").GetString()!;
        Assert.Contains(VaultId, message, StringComparison.Ordinal);
        Assert.DoesNotContain("page(s)", message, StringComparison.Ordinal);
        Assert.Equal(MovePageWrite.ProtectedPositionUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(holds);
    }

    [Fact]
    public async Task AProtectedTarget_WithAPosition_AndAReadGrant_MovesThePage()
    {
        Grant();
        ReadGrant();
        ApplyMoves();

        var root = Ok(await Tool().MoveHierarchyObject(P2, VaultId, 1, Basis(Lmt), Ct));

        Assert.Equal(NewId, root.GetProperty("meta").GetProperty("objectId").GetString());
        Assert.Single(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task AProtectedTarget_WithAnOutOfRangePosition_AndAReadGrant_IsInvalidArgument()
    {
        Grant();
        ReadGrant();

        Error(await Tool().MoveHierarchyObject(P2, VaultId, 9, Basis(Lmt), Ct), "InvalidArgument");

        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task AStaleBasis_IsStaleWrite_NothingSent()
    {
        Grant();

        Error(await Tool().MoveHierarchyObject(P2, PlansId, null, Basis(OlderLmt), Ct), "StaleWrite");

        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
    }

    [Fact]
    public async Task NoBasis_MovesWithNoBasis()
    {
        Grant();

        var root = Ok(await Tool().MoveHierarchyObject(P2, PlansId, null, null, Ct));

        Assert.Contains(root.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == ToolWarnings.NoBasis);
    }

    [Fact]
    public async Task ABasisWithAHash_IsInvalidArgument_BeforeAnyRead()
    {
        Grant();

        Error(await Tool().MoveHierarchyObject(P2, PlansId, null, new PageBasisArgument(Lmt, "abc"), Ct), "InvalidArgument");

        Assert.Empty(gateway.Calls);
    }

    [Theory]
    [InlineData("", PlansId)]
    [InlineData(P2, "")]
    public async Task AnEmptyId_IsInvalidArgument_BeforeAnyDialogOrRead(string objectId, string parentId)
    {
        var prompt = new FakePrompt(Typed("A"));

        Error(await MoveWithPrompt(prompt, objectId, parentId), "InvalidArgument");

        Assert.Empty(prompt.Order);
        Assert.Empty(gateway.Calls);
    }

    // --- COM failures and the read-back ----------------------------------------------------------------------------

    [Fact]
    public async Task APartialCopy_IsObjectNotFound_OutcomeUnknown_AndSaysWhatTheReReadFound()
    {
        Grant();
        gateway.UpdateHierarchyFailure = Com.HResultMap.ToException("UpdateHierarchy", new System.Runtime.InteropServices.COMException("refused", unchecked((int)0x80042004)));
        gateway.OnUpdateHierarchyCall = _ =>
        {
            // OneNote left a copy in the target and kept the page in the source (spike 3.1 M3, one run).
            Children(PlansId, $"<one:Section {Ns} ID=\"{PlansId}\" name=\"Plans\">{Page(Q1, "Q1", 1)}{Page(Q1a, "Q1a", 2)}{Page(Q2, "Q2", 1)}{Page(NewId, P2Name, 1)}</one:Section>");
            return Task.CompletedTask;
        };

        var error = Error(await Tool().MoveHierarchyObject(P2, PlansId, null, Basis(Lmt), Ct), "ObjectNotFound");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Equal("0x80042004", error.GetProperty("hresult").GetString());
        var message = error.GetProperty("message").GetString()!;
        Assert.Contains($"page {P2} is still in section {IdeasId}", message, StringComparison.Ordinal);
        Assert.Contains($"1 new page(s) with its name ({NewId})", message, StringComparison.Ordinal);
        Assert.Contains("journal entries", message, StringComparison.Ordinal);
        Assert.DoesNotContain(P2Name, error.GetRawText(), StringComparison.Ordinal);

        // Both sections were re-read after the call, source then target, before the error was thrown.
        Assert.Equal([(IdeasId, HierarchyScope.Children), (PlansId, HierarchyScope.Children)], gateway.Calls.TakeLast(2));
        Assert.False(cache.TryGet(P2, DateTimeOffset.UnixEpoch, out _));
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AModalDialog_IsModalDialogOpen_Retryable()
    {
        Grant();
        gateway.UpdateHierarchyFailure = Com.HResultMap.ToException("UpdateHierarchy", new System.Runtime.InteropServices.COMException("modal", unchecked((int)0x80042030)));

        var error = Error(await Tool().MoveHierarchyObject(P2, PlansId, null, Basis(Lmt), Ct), "ModalDialogOpen");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Contains("journal entries", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadBackWithNoNewPage_IsReadBackDiffers_AndTheOldIdIsReported()
    {
        // The fake doesn't apply the move: the target gained nothing and the page is still in the source.
        Grant();

        var root = Ok(await Tool().MoveHierarchyObject(P2, PlansId, null, Basis(Lmt), Ct));

        var codes = root.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ToList();
        Assert.Equal([ToolWarnings.ReadBackDiffers, ToolWarnings.ReadBackDiffers], codes);
        Assert.Equal(P2, root.GetProperty("meta").GetProperty("objectId").GetString());
    }

    [Fact]
    public async Task AReadBackThatFails_IsReadBackFailed_AndTheMoveStands()
    {
        Grant();
        gateway.OnUpdateHierarchy = _ => gateway.Hierarchies.Remove((PlansId, HierarchyScope.Children));

        var root = Ok(await Tool().MoveHierarchyObject(P2, PlansId, null, Basis(Lmt), Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.ReadBackFailed, warning.GetProperty("code").GetString());
        Assert.False(root.GetProperty("meta").TryGetProperty("objectId", out _));
        Assert.Single(gateway.UpdateHierarchyCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task SeveralNewPages_TheSentNameWins()
    {
        Grant();
        gateway.OnUpdateHierarchy = _ =>
        {
            const string stranger = "{A9000000-0000-4000-8000-000000000001}{1}{E98}";
            Children(PlansId, $"<one:Section {Ns} ID=\"{PlansId}\" name=\"Plans\">{Page(stranger, "Someone else's", 1)}{Page(Q1, "Q1", 1)}{Page(Q1a, "Q1a", 2)}{Page(Q2, "Q2", 1)}{Page(NewId, P2Name, 1)}</one:Section>");
            Children(IdeasId, $"<one:Section {Ns} ID=\"{IdeasId}\" name=\"Ideas\">{Page(P1, "P1", 1)}</one:Section>");
        };

        var root = Ok(await Tool().MoveHierarchyObject(P2, PlansId, null, Basis(Lmt), Ct));

        Assert.Equal(NewId, root.GetProperty("meta").GetProperty("objectId").GetString());
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public void TheUseCase_IsAHierarchyWrite() =>
        Assert.Equal(OperationClass.HierarchyWrite, UseCase().Class);

    // --- Helpers ----------------------------------------------------------------------------------------------------

    private static string Page(string id, string name, int level) =>
        $"<one:Page ID=\"{id}\" name=\"{name}\" pageLevel=\"{level}\" lastModifiedTime=\"{Lmt}\" />";

    private void Parent(string id, string parent) => gateway.Parents[id] = parent;

    private void Self(string id, string xml) =>
        gateway.Hierarchies[(id, HierarchyScope.Self)] = "<?xml version=\"1.0\"?>\n" + xml;

    private void Children(string id, string xml) =>
        gateway.Hierarchies[(id, HierarchyScope.Children)] = "<?xml version=\"1.0\"?>\n" + xml;

    /// <summary>
    /// Makes the fake apply each move as OneNote does (spike 3.1 M1, M1d): the page leaves its section, its subpages stay
    /// there at level 1, and it appears in the target where it was sent, under <see cref="NewId"/>.
    /// </summary>
    private void ApplyMoves() => gateway.OnUpdateHierarchy = sent =>
    {
        var root = XElement.Parse(sent);
        var targetKey = ((string)root.Attribute("ID")!, HierarchyScope.Children);
        var movedId = root.Elements().Select(e => (string)e.Attribute("ID")!)
            .Single(id => !XDocument.Parse(gateway.Hierarchies[targetKey]).Root!.Elements().Any(k => (string?)k.Attribute("ID") == id));
        var sourceKey = gateway.Hierarchies.Keys.Single(k => k.Scope == HierarchyScope.Children && k != targetKey
            && XDocument.Parse(gateway.Hierarchies[k]).Root!.Elements(One + "Page").Any(p => (string?)p.Attribute("ID") == movedId));

        var source = XDocument.Parse(gateway.Hierarchies[sourceKey]);
        var pages = source.Root!.Elements(One + "Page").ToList();
        var page = pages.Single(p => (string?)p.Attribute("ID") == movedId);
        var level = (int)page.Attribute("pageLevel")!;
        foreach (var sub in pages.SkipWhile(p => p != page).Skip(1).TakeWhile(p => (int)p.Attribute("pageLevel")! > level))
        {
            sub.SetAttributeValue("pageLevel", 1);
        }

        page.Remove();
        gateway.Hierarchies[sourceKey] = source.ToString();

        var target = XDocument.Parse(gateway.Hierarchies[targetKey]);
        var kids = target.Root!.Elements().ToList();
        var ordered = root.Elements().Select(e => (string)e.Attribute("ID")!).Select(id => id == movedId
            ? new XElement(page)
            :new XElement(kids.Single(k => (string?)k.Attribute("ID") == id))).ToList();
        var copy = ordered.Single(e => (string?)e.Attribute("ID") == movedId);
        copy.SetAttributeValue("ID", NewId);
        copy.SetAttributeValue("pageLevel", 1);
        target.Root.ReplaceNodes(ordered);
        gateway.Hierarchies[targetKey] = target.ToString();
    };

    private void Grant(string path = NotebookPath, string id = NotebookId) =>
        new SettingsWriter(Store, clock).SaveWriteGrant(
            new NotebookGrant(GrantKeys.NotebookKey(path), "A", id, Write: true, PermanentDelete: false, clock.GetUtcNow(), GrantedVia.Cli));

    private void ReadGrant() =>
        new SettingsWriter(Store, clock).SaveReadGrant(new ProtectedSectionGrant(
            GrantKeys.SectionKey(@"C:\notebooks\A\Vault.one"), "Vault", GrantKeys.NotebookKey(NotebookPath), VaultId, clock.GetUtcNow(), GrantedVia.Cli));

    private static PageBasisArgument Basis(string date) => new(date);

    private static GrantPromptResult Typed(string name) => new(GrantPromptStatus.Accepted, name);

    private IReadOnlyList<string> EntryFiles() =>
        Directory.Exists(JournalFolder)
            ? [.. Directory.GetFiles(JournalFolder).Where(f => JournalFiles.IsEntry(Path.GetFileName(f)))]
            : [];

    private static string RawEntryJson(string file)
    {
        using var stream = File.OpenRead(file);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
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
            new WritePipeline(writeLock, gateway, cache, Store.Read, TimeProvider.System, null, new WriteGrantDialog(gateway, new SettingsWriter(Store, clock), clock)));
    }

    private static ToolRunner Runner() => new(NullLogger<ToolRunner>.Instance);

    private MoveHierarchyObjectTool Tool() => new(Runner(), UseCase());

    /// <summary>A move through the runner with <paramref name="prompt"/> as the call's grant dialog.</summary>
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
