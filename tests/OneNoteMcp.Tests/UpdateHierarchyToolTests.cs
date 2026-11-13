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
/// Story 5.4: <c>rename_hierarchy_object</c> and <c>reorder_hierarchy_children</c> through the AD-7 write pipeline as a
/// <c>HierarchyWrite</c>, one test or theory per row of the plan's I/O matrix, against <see cref="FakeGateway"/> scripted
/// with a synthetic notebook A (sections Ideas, Vault (protected), Locked (protected, locked), group Archive, the recycle
/// bin), a OneDrive notebook Cloud and an ungranted notebook Other, a temporary <c>settings.json</c> and its journal.
/// IDs are well-formed OneNote IDs, because the XML the server sends must pass the 2013 XSD. Each test has a write lock
/// of its own, and checks it is free again after every outcome.
/// </summary>
public sealed class UpdateHierarchyToolTests : IDisposable
{
    private const string Lmt = "2026-11-13T10:00:00.000Z";
    private const string OlderLmt = "2026-11-13T09:00:00.000Z";

    private const string NotebookId = "{A0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string NotebookPath = @"C:\notebooks\A\";
    private const string IdeasId = "{A1000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string VaultId = "{A2000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string LockedId = "{A3000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string ArchiveId = "{A4000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string BinId = "{A5000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string PlansId = "{A6000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string NotesId = "{A7000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string SubId = "{A8000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string DeletedPagesId = "{A9000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string BinnedId = "{AA000000-0000-4000-8000-000000000001}{1}{B0}";

    private const string P1 = "{A1000000-0000-4000-8000-000000000001}{1}{E11}";
    private const string P1a = "{A1000000-0000-4000-8000-000000000001}{1}{E12}";
    private const string P1b = "{A1000000-0000-4000-8000-000000000001}{1}{E13}";
    private const string P2 = "{A1000000-0000-4000-8000-000000000001}{1}{E21}";
    private const string P2a = "{A1000000-0000-4000-8000-000000000001}{1}{E22}";
    private const string P3 = "{A1000000-0000-4000-8000-000000000001}{1}{E31}";
    private const string V1 = "{A2000000-0000-4000-8000-000000000001}{1}{E11}";
    private const string V2 = "{A2000000-0000-4000-8000-000000000001}{1}{E21}";

    private const string CloudId = "{B0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string CloudPath = "https://d.docs.live.net/0123abcd/Cloud/";
    private const string CloudSectionId = "{B1000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string CloudGroupId = "{B2000000-0000-4000-8000-000000000001}{1}{B0}";

    private const string OtherId = "{C0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string OtherPath = @"C:\notebooks\Other\";
    private const string OtherSectionId = "{C1000000-0000-4000-8000-000000000001}{1}{B0}";

    private const string UnknownId = "{D0000000-0000-4000-8000-00000000DEAD}{1}{B0}";

    /// <summary>A name that appears nowhere else, so a test can show it never reaches a message.</summary>
    private const string Fresh = "Plans Zq7";

    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";

    private static readonly XNamespace One = OneNoteXml.One;

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly FakeGateway gateway = new();
    private readonly ConcurrentQueue<WriteLockHeld> holds = new();
    private readonly WriteLock writeLock;
    private readonly PageTextCache cache = PageTextCache.WithDefaults();

    public UpdateHierarchyToolTests()
    {
        writeLock = new WriteLock($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", holds.Enqueue);
        Directory.CreateDirectory(temp.Combine("cfg"));

        Parent(NotebookId, string.Empty);
        foreach (var id in new[] { IdeasId, VaultId, LockedId, ArchiveId, BinId })
        {
            Parent(id, NotebookId);
        }

        foreach (var id in new[] { PlansId, NotesId, SubId })
        {
            Parent(id, ArchiveId);
        }

        Parent(DeletedPagesId, BinId);
        Parent(BinnedId, BinId);
        foreach (var id in new[] { P1, P1a, P1b, P2, P2a, P3 })
        {
            Parent(id, IdeasId);
        }

        Parent(V1, VaultId);
        Parent(V2, VaultId);
        Parent(CloudId, string.Empty);
        Parent(CloudSectionId, CloudId);
        Parent(CloudGroupId, CloudId);
        Parent(OtherId, string.Empty);
        Parent(OtherSectionId, OtherId);

        const string notebook = $"name=\"A\" nickname=\"A\" ID=\"{NotebookId}\" path=\"{NotebookPath}\" lastModifiedTime=\"{Lmt}\"";
        const string ideas = $"name=\"Ideas\" ID=\"{IdeasId}\" path=\"C:\\notebooks\\A\\Ideas.one\" lastModifiedTime=\"{Lmt}\"";
        const string vault = $"name=\"Vault\" ID=\"{VaultId}\" path=\"C:\\notebooks\\A\\Vault.one\" encrypted=\"true\" lastModifiedTime=\"{Lmt}\"";
        const string locked = $"name=\"Locked\" ID=\"{LockedId}\" path=\"C:\\notebooks\\A\\Locked.one\" encrypted=\"true\" locked=\"true\" lastModifiedTime=\"{Lmt}\"";
        const string archive = $"name=\"Archive\" ID=\"{ArchiveId}\" path=\"C:\\notebooks\\A\\Archive\" lastModifiedTime=\"{Lmt}\"";
        const string bin = $"name=\"OneNote_RecycleBin\" ID=\"{BinId}\" path=\"C:\\notebooks\\A\\OneNote_RecycleBin\" isRecycleBin=\"true\" lastModifiedTime=\"{Lmt}\"";
        const string plans = $"name=\"Plans\" ID=\"{PlansId}\" path=\"C:\\notebooks\\A\\Archive\\Plans.one\" lastModifiedTime=\"{Lmt}\"";
        const string notes = $"name=\"Notes\" ID=\"{NotesId}\" path=\"C:\\notebooks\\A\\Archive\\Notes.one\" lastModifiedTime=\"{Lmt}\"";
        const string sub = $"name=\"Sub\" ID=\"{SubId}\" path=\"C:\\notebooks\\A\\Archive\\Sub\" lastModifiedTime=\"{Lmt}\"";
        const string deleted = $"name=\"Deleted Pages\" ID=\"{DeletedPagesId}\" path=\"C:\\notebooks\\A\\OneNote_RecycleBin\\OneNote_DeletedPages.one\" isInRecycleBin=\"true\" isDeletedPages=\"true\" lastModifiedTime=\"{Lmt}\"";
        const string binned = $"name=\"Gone\" ID=\"{BinnedId}\" path=\"C:\\notebooks\\A\\OneNote_RecycleBin\\Gone.one\" isInRecycleBin=\"true\" lastModifiedTime=\"{Lmt}\"";

        Self(NotebookId, $"<one:Notebook {Ns} {notebook} />");
        Self(IdeasId, $"<one:Section {Ns} {ideas} />");
        Self(VaultId, $"<one:Section {Ns} {vault} />");
        Self(LockedId, $"<one:Section {Ns} {locked} />");
        Self(ArchiveId, $"<one:SectionGroup {Ns} {archive} />");
        Self(BinId, $"<one:SectionGroup {Ns} {bin} />");
        Self(PlansId, $"<one:Section {Ns} {plans} />");
        Self(NotesId, $"<one:Section {Ns} {notes} />");
        Self(SubId, $"<one:SectionGroup {Ns} {sub} />");
        Self(DeletedPagesId, $"<one:Section {Ns} {deleted} />");
        Self(BinnedId, $"<one:Section {Ns} {binned} />");
        Self(P1, $"<one:Page {Ns} ID=\"{P1}\" name=\"P1\" pageLevel=\"1\" />");

        Children(NotebookId, $"<one:Notebook {Ns} {notebook}><one:Section {ideas} /><one:Section {vault} /><one:Section {locked} /><one:SectionGroup {archive} /><one:SectionGroup {bin} /></one:Notebook>");
        Children(ArchiveId, $"<one:SectionGroup {Ns} {archive}><one:Section {plans} /><one:Section {notes} /><one:SectionGroup {sub} /></one:SectionGroup>");
        Children(BinId, $"<one:SectionGroup {Ns} {bin}><one:Section {deleted} /><one:Section {binned} /></one:SectionGroup>");
        Children(DeletedPagesId, $"<one:Section {Ns} {deleted} />");
        Children(LockedId, $"<one:Section {Ns} {locked} />");
        Children(IdeasId, $"<one:Section {Ns} {ideas}>{Page(P1, 1)}{Page(P1a, 2)}{Page(P1b, 2)}{Page(P2, 1)}{Page(P2a, 2)}{Page(P3, 1)}</one:Section>");
        Children(VaultId, $"<one:Section {Ns} {vault}>{Page(V1, 1)}{Page(V2, 1)}</one:Section>");
        Children(P1, $"<one:Page {Ns} ID=\"{P1}\" name=\"P1\" pageLevel=\"1\" />");

        const string cloud = $"name=\"Cloud\" ID=\"{CloudId}\" path=\"{CloudPath}\" lastModifiedTime=\"{Lmt}\"";
        const string cloudSection = $"name=\"Web\" ID=\"{CloudSectionId}\" path=\"{CloudPath}Web.one\" lastModifiedTime=\"{Lmt}\"";
        const string cloudGroup = $"name=\"Shared\" ID=\"{CloudGroupId}\" path=\"{CloudPath}Shared\" lastModifiedTime=\"{Lmt}\"";
        Self(CloudId, $"<one:Notebook {Ns} {cloud} />");
        Self(CloudSectionId, $"<one:Section {Ns} {cloudSection} />");
        Self(CloudGroupId, $"<one:SectionGroup {Ns} {cloudGroup} />");
        Children(CloudId, $"<one:Notebook {Ns} {cloud}><one:Section {cloudSection} /><one:SectionGroup {cloudGroup} /></one:Notebook>");

        const string other = $"name=\"Other\" nickname=\"Elsewhere\" ID=\"{OtherId}\" path=\"{OtherPath}\" lastModifiedTime=\"{Lmt}\"";
        Self(OtherId, $"<one:Notebook {Ns} {other} />");
        Self(OtherSectionId, $"<one:Section {Ns} name=\"Inbox\" ID=\"{OtherSectionId}\" path=\"C:\\notebooks\\Other\\Inbox.one\" lastModifiedTime=\"{Lmt}\" />");

        gateway.Hierarchies[(string.Empty, HierarchyScope.Notebooks)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebooks {Ns}><one:Notebook {notebook} /><one:Notebook {cloud} /><one:Notebook {other} /></one:Notebooks>";
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The journal folder is the test's own: never the user's real journal under %LOCALAPPDATA%.
    private SettingsStore Store => new(Path.Combine(temp.Combine("cfg"), "settings.json"), temp.Combine("journal"));

    private string JournalFolder => temp.Combine("journal");

    public void Dispose() => temp.Dispose();

    // --- Renames ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RenameSection_Granted_SendsTheMinimalXml_ReadsBack_AndJournalsTheOldName()
    {
        Grant();
        ApplyWrites();
        var entriesAtCall = -1;
        gateway.OnUpdateHierarchyCall = _ =>
        {
            entriesAtCall = EntryFiles().Count;
            return Task.CompletedTask;
        };

        var root = Ok(await RenameTool().RenameHierarchyObject(IdeasId, "Plans", Basis(Lmt), Ct));

        var meta = root.GetProperty("meta");
        Assert.Equal(["correlationId", "journalEntryId", "objectId"], meta.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(IdeasId, meta.GetProperty("objectId").GetString());
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());

        var sent = Assert.Single(gateway.UpdateHierarchyCalls);
        var element = XElement.Parse(sent);
        Assert.Equal(One + "Section", element.Name);
        Assert.Equal(["ID", "name"], element.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name.LocalName));
        Assert.Equal(IdeasId, (string?)element.Attribute("ID"));
        Assert.Equal("Plans", (string?)element.Attribute("name"));
        Assert.Empty(SchemaValidation.Problems(sent));

        // AD-8: the entry was stored before the call, holds the old name, and is a hierarchy entry.
        Assert.Equal(1, entriesAtCall);
        var entry = Entry();
        Assert.Equal(meta.GetProperty("journalEntryId").GetString(), entry.EntryId.ToString("D"));
        Assert.Equal(JournalEntry.HierarchyKind, entry.SnapshotKind);
        Assert.Equal(RenameHierarchyWrite.OperationName, entry.Operation);
        Assert.Equal(nameof(OperationClass.HierarchyWrite), entry.OperationClass);
        Assert.Equal(IdeasId, entry.ObjectId);
        Assert.Equal(NotebookId, entry.NotebookId);
        Assert.Equal(GrantKeys.NotebookKey(NotebookPath), entry.NotebookPath);
        Assert.Equal(NotebookId, entry.ParentId);
        Assert.Equal("Ideas", entry.Title);
        Assert.Contains("name=\"Ideas\"", entry.Xml, StringComparison.Ordinal);
        Assert.Null(entry.SectionKey);
        Assert.Equal(meta.GetProperty("correlationId").GetString(), Assert.Single(holds).CorrelationId);
        await AssertLockFreeAsync();
    }

    [Theory]
    [InlineData("Plans.one", "Plans")]
    [InlineData("Plans.ONE", "Plans")]
    [InlineData("Plans.txt", "Plans.txt")]
    public async Task RenameSection_DropsOneTrailingDotOne(string name, string sent)
    {
        Grant();

        Ok(await RenameTool().RenameHierarchyObject(IdeasId, name, Basis(Lmt), Ct));

        Assert.Equal(sent, (string?)XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls)).Attribute("name"));
    }

    [Fact]
    public async Task RenameSectionGroup_SendsName()
    {
        Grant();

        Ok(await RenameTool().RenameHierarchyObject(ArchiveId, "Old Things", Basis(Lmt), Ct));

        var element = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal(One + "SectionGroup", element.Name);
        Assert.Equal("Old Things", (string?)element.Attribute("name"));
    }

    [Fact]
    public async Task RenameNotebook_SendsNickname_NeverName_AndJournalsTheNotebook()
    {
        Grant();
        ApplyWrites();

        var root = Ok(await RenameTool().RenameHierarchyObject(NotebookId, "Trips", Basis(Lmt), Ct));

        var element = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal(One + "Notebook", element.Name);
        Assert.Equal("Trips", (string?)element.Attribute("nickname"));
        Assert.Null(element.Attribute("name"));
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        var entry = Entry();
        Assert.Equal(NotebookId, entry.ObjectId);
        Assert.Equal(string.Empty, entry.ParentId);
        Assert.Equal("A", entry.Title);

        // The folder and the grant are untouched: the path is the grant's key.
        Assert.True(Store.Read().Notebooks.ContainsKey(GrantKeys.NotebookKey(NotebookPath)));
    }

    [Fact]
    public async Task RenameNotebook_ToAnotherOpenNotebooksNameOrNickname_IsInvalidArgument_NameNotQuoted()
    {
        Grant();

        foreach (var name in new[] { "OTHER", "ELSEWHERE", "cLOUD" }) // another's name, its nickname, ignoring case
        {
            var error = Error(await RenameTool().RenameHierarchyObject(NotebookId, name, Basis(Lmt), Ct), "InvalidArgument");
            Assert.DoesNotContain(name, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
        await AssertLockFreeAsync();
    }

    [Theory]
    [InlineData("plans")] // a section, ignoring case
    [InlineData("SUB")] // a section group: either kind
    public async Task Rename_ToASiblingsName_IsInvalidArgument_BeforeCom_NameNotQuoted(string name)
    {
        Grant();

        var error = Error(await RenameTool().RenameHierarchyObject(NotesId, name, Basis(Lmt), Ct), "InvalidArgument");

        Assert.DoesNotContain(name, error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(NotesId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task Rename_CaseOnly_OfItsOwnName_IsAllowed()
    {
        Grant();

        Ok(await RenameTool().RenameHierarchyObject(PlansId, "PLANS", Basis(Lmt), Ct));

        Assert.Equal("PLANS", (string?)XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls)).Attribute("name"));
    }

    [Theory]
    [InlineData(IdeasId, "Ideas")]
    [InlineData(IdeasId, "Ideas.one")]
    [InlineData(NotebookId, "A")]
    public async Task Rename_ToTheSameExactName_IsOkUnchanged_NoComNoEntry(string id, string name)
    {
        Grant();

        var root = Ok(await RenameTool().RenameHierarchyObject(id, name, null, Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.Unchanged, warning.GetProperty("code").GetString());
        Assert.False(root.GetProperty("meta").TryGetProperty("journalEntryId", out _));
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
        await AssertLockFreeAsync();
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("x?")]
    [InlineData("x*")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData(" x")]
    [InlineData("x.")]
    [InlineData("")]
    [InlineData("OneNote_RecycleBin")]
    [InlineData("onenote_deletedpages.one")]
    [InlineData("a\u202Eb")] // 5.3's name hygiene, inherited through SectionFileNames.NameProblem: a text-direction character
    [InlineData("a\u200Bb")] // an invisible character
    [InlineData("a\u2028b")] // a line separator
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")] // 129 characters
    public async Task Rename_ABadName_IsInvalidArgument_BeforeTheLock_NothingRead(string name)
    {
        Grant();

        var error = Error(await RenameTool().RenameHierarchyObject(IdeasId, name, Basis(Lmt), Ct), "InvalidArgument");

        if (name.Length > 2) // ".." is the rule's own phrase ("contains '..'"), not the name quoted
        {
            Assert.DoesNotContain(name, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        Assert.Empty(gateway.Calls);
        Assert.Empty(holds);
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Theory]
    [InlineData("x .one")] // the stem ends with a space
    [InlineData("CON.one")] // the stem is a device name
    public async Task RenameSection_ABadStemAfterDotOne_IsInvalidArgument_BeforeCom(string name)
    {
        Grant();

        Error(await RenameTool().RenameHierarchyObject(IdeasId, name, Basis(Lmt), Ct), "InvalidArgument");

        Assert.Empty(gateway.UpdateHierarchyCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task RenameAPage_IsInvalidArgument_PointingToUpdatePageContent()
    {
        Grant();

        var error = Error(await RenameTool().RenameHierarchyObject(P1, "New title", Basis(Lmt), Ct), "InvalidArgument");

        Assert.Contains("update_page_content", error.GetProperty("userAction").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.UpdateHierarchyCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task RenameAGroup_InAOneDriveNotebook_IsNotSupported_WithAUserAction_ButASectionThereIsRenamed()
    {
        Grant(CloudPath, CloudId);

        var error = Error(await RenameTool().RenameHierarchyObject(CloudGroupId, "Team", Basis(Lmt), Ct), "NotSupported");

        Assert.Contains("in OneNote", error.GetProperty("userAction").GetString(), StringComparison.Ordinal);
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Empty(gateway.UpdateHierarchyCalls);

        Ok(await RenameTool().RenameHierarchyObject(CloudSectionId, "Team", Basis(Lmt), Ct));
        Assert.Single(gateway.UpdateHierarchyCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task RenameAProtectedSection_NeedsOnlyTheWriteGrant_AndItsEntryIsProtected()
    {
        Grant();

        var root = Ok(await RenameTool().RenameHierarchyObject(VaultId, "Safe", Basis(Lmt), Ct));

        var file = Assert.Single(EntryFiles());
        // On disk the title and XML are DPAPI-encrypted (the sectionKey, a path, is the marker, as for a page entry).
        var raw = RawEntryJson(file);
        Assert.DoesNotContain("\"title\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"xml\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("name=", raw, StringComparison.Ordinal);
        Assert.Contains("\"protected\"", raw, StringComparison.Ordinal);
        var entry = JournalStore.Read(file);
        Assert.Equal(GrantKeys.SectionKey(@"C:\notebooks\A\Vault.one"), entry.SectionKey);
        Assert.Equal("Vault", entry.Title);
        Assert.Equal(root.GetProperty("meta").GetProperty("journalEntryId").GetString(), entry.EntryId.ToString("D"));
    }

    // --- Reorders ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReorderSections_InAGroup_SendsTheParentWithIdOnlyChildren_AndIsOkWithNoWarnings()
    {
        Grant();
        ApplyWrites();

        var root = Ok(await ReorderTool().ReorderHierarchyChildren(ArchiveId, [NotesId, PlansId, SubId], Basis(Lmt), Ct));

        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        Assert.Equal(ArchiveId, root.GetProperty("meta").GetProperty("objectId").GetString());
        var sent = Assert.Single(gateway.UpdateHierarchyCalls);
        var element = XElement.Parse(sent);
        Assert.Equal(One + "SectionGroup", element.Name);
        Assert.Equal(ArchiveId, (string?)element.Attribute("ID"));
        Assert.Equal([NotesId, PlansId, SubId], element.Elements().Select(e => (string?)e.Attribute("ID")));
        Assert.All(element.Elements(), e => Assert.Equal(["ID"], e.Attributes().Select(a => a.Name.LocalName)));
        Assert.Empty(SchemaValidation.Problems(sent));

        var entry = Entry();
        Assert.Equal(ReorderHierarchyWrite.OperationName, entry.Operation);
        Assert.Equal(ArchiveId, entry.ObjectId);
        Assert.Contains(PlansId, entry.Xml, StringComparison.Ordinal);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task ReorderANotebook_NeverListsOrSendsTheRecycleBin()
    {
        Grant();

        Ok(await ReorderTool().ReorderHierarchyChildren(NotebookId, [VaultId, IdeasId, LockedId, ArchiveId], Basis(Lmt), Ct));

        var element = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal(One + "Notebook", element.Name);
        Assert.Equal([VaultId, IdeasId, LockedId, ArchiveId], element.Elements().Select(e => (string?)e.Attribute("ID")));
    }

    [Fact]
    public async Task ReorderPages_WithWholeSubpageBlocks_IsOk_IdOnlyPages_LevelsNotSent()
    {
        Grant();
        ApplyWrites();

        var root = Ok(await ReorderTool().ReorderHierarchyChildren(IdeasId, [P3, P2, P2a, P1, P1b, P1a], Basis(Lmt), Ct));

        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
        var element = XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls));
        Assert.Equal(One + "Section", element.Name);
        Assert.Equal([P3, P2, P2a, P1, P1b, P1a], element.Elements().Select(e => (string?)e.Attribute("ID")));
        Assert.All(element.Elements(), e =>
        {
            Assert.Equal(One + "Page", e.Name);
            Assert.Null(e.Attribute("pageLevel"));
        });
    }

    [Fact]
    public async Task Reorder_TheSameOrder_IsOkUnchanged_NoComNoEntry()
    {
        Grant();

        var root = Ok(await ReorderTool().ReorderHierarchyChildren(ArchiveId, [PlansId, NotesId, SubId], null, Ct));

        Assert.Equal(ToolWarnings.Unchanged, Assert.Single(root.GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
    }

    public static TheoryData<string, string[]> WrongLists() => new()
    {
        { "missing", [PlansId, NotesId] },
        { "foreign", [PlansId, NotesId, SubId, IdeasId] },
        { "unknown", [PlansId, NotesId, SubId, UnknownId] },
        { "the recycle bin", [PlansId, NotesId, SubId, BinId] },
        { "a group before a section", [PlansId, SubId, NotesId] },
    };

    [Theory]
    [MemberData(nameof(WrongLists))]
    public async Task Reorder_AWrongList_IsInvalidArgument_BeforeCom(string why, string[] childIds)
    {
        Grant();

        var error = Error(await ReorderTool().ReorderHierarchyChildren(ArchiveId, childIds, Basis(Lmt), Ct), "InvalidArgument");

        Assert.False(string.IsNullOrEmpty(error.GetProperty("userAction").GetString()), why);
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
        await AssertLockFreeAsync();
    }

    public static TheoryData<string[]> DuplicateOrEmpty() => new()
    {
        new[] { PlansId, NotesId, NotesId, SubId },
        new[] { PlansId, string.Empty, SubId },
    };

    [Theory]
    [MemberData(nameof(DuplicateOrEmpty))]
    public async Task Reorder_DuplicateOrEmptyIds_AreInvalidArgument_BeforeTheLock(string[] childIds)
    {
        Grant();

        var error = Error(await ReorderTool().ReorderHierarchyChildren(ArchiveId, childIds, Basis(Lmt), Ct), "InvalidArgument");

        Assert.DoesNotContain(NotesId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(gateway.Calls);
        Assert.Empty(holds);
    }

    [Fact]
    public async Task Reorder_AnEmptyList_IsUnchangedForAnEmptyParent_AndMissingChildrenOtherwise()
    {
        Grant();
        Children(SubId, $"<one:SectionGroup {Ns} name=\"Sub\" ID=\"{SubId}\" lastModifiedTime=\"{Lmt}\" />");

        var root = Ok(await ReorderTool().ReorderHierarchyChildren(SubId, [], Basis(Lmt), Ct));
        Assert.Equal(ToolWarnings.Unchanged, Assert.Single(root.GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());

        var error = Error(await ReorderTool().ReorderHierarchyChildren(ArchiveId, [], Basis(Lmt), Ct), "InvalidArgument");
        Assert.Contains("missing from childIds", error.GetProperty("message").GetString(), StringComparison.Ordinal);

        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
        await AssertLockFreeAsync();
    }

    public static TheoryData<string, string[]> SplitBlocks() => new()
    {
        { "a subpage before its parent", [P1a, P1, P1b, P2, P2a, P3] },
        { "a subpage under another parent", [P1, P1b, P2, P1a, P2a, P3] },
        { "a subpage split from its block", [P1, P1a, P2, P2a, P1b, P3] },
        { "a subpage first", [P2a, P1, P1a, P1b, P2, P3] },
    };

    [Theory]
    [MemberData(nameof(SplitBlocks))]
    public async Task ReorderPages_ASubpageSplitFromItsParent_IsInvalidArgument(string why, string[] childIds)
    {
        Grant();

        var error = Error(await ReorderTool().ReorderHierarchyChildren(IdeasId, childIds, Basis(Lmt), Ct), "InvalidArgument");

        Assert.True(error.GetProperty("message").GetString()!.Contains("subpage", StringComparison.Ordinal), why);
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task ReorderPages_SubpagesReorderedWithinTheirOwnBlock_IsOk()
    {
        Grant();

        Ok(await ReorderTool().ReorderHierarchyChildren(IdeasId, [P1, P1b, P1a, P2, P2a, P3], Basis(Lmt), Ct));

        Assert.Single(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task ReorderPages_InAProtectedSection_WithoutAReadGrant_NeedsOnlyTheWriteGrant_AndRefusalsCarryCountsOnly()
    {
        Grant();

        var error = Error(await ReorderTool().ReorderHierarchyChildren(VaultId, [V2, V1, P1], Basis(Lmt), Ct), "InvalidArgument");

        var message = error.GetProperty("message").GetString()!;
        Assert.DoesNotContain(P1, message, StringComparison.Ordinal);
        Assert.DoesNotContain(V1, message, StringComparison.Ordinal);
        Assert.Contains("1 ID(s)", message, StringComparison.Ordinal);

        var root = Ok(await ReorderTool().ReorderHierarchyChildren(VaultId, [V2, V1], Basis(Lmt), Ct));
        var entry = Entry();
        Assert.Equal(GrantKeys.SectionKey(@"C:\notebooks\A\Vault.one"), entry.SectionKey);
        Assert.DoesNotContain(V1, RawEntryJson(Assert.Single(EntryFiles())), StringComparison.Ordinal);

        // The read-back differs (the fake doesn't apply it): counts only, never a page ID.
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.ReadBackDiffers, warning.GetProperty("code").GetString());
        Assert.DoesNotContain(V1, warning.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(V2, warning.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReorderPages_InAProtectedSection_WithAReadGrant_NamesTheIds()
    {
        Grant();
        new SettingsWriter(Store, clock).SaveReadGrant(new ProtectedSectionGrant(
            GrantKeys.SectionKey(@"C:\notebooks\A\Vault.one"), "Vault", GrantKeys.NotebookKey(NotebookPath), VaultId, clock.GetUtcNow(), GrantedVia.Cli));

        var error = Error(await ReorderTool().ReorderHierarchyChildren(VaultId, [V2, V1, P1], Basis(Lmt), Ct), "InvalidArgument");

        Assert.Contains(P1, error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReorderAPagesChildren_OrAPage_IsInvalidArgument()
    {
        Grant();

        Error(await ReorderTool().ReorderHierarchyChildren(P1, [P1], Basis(Lmt), Ct), "InvalidArgument");

        Assert.Empty(gateway.UpdateHierarchyCalls);
        await AssertLockFreeAsync();
    }

    // --- Shared refusals ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ungranted_WithoutADialog_IsWriteNotGranted_NoLockNoCom()
    {
        var rename = Error(await RenameTool().RenameHierarchyObject(OtherSectionId, Fresh, Basis(Lmt), Ct), "WriteNotGranted");
        var reorder = Error(await ReorderTool().ReorderHierarchyChildren(OtherId, [OtherSectionId], Basis(Lmt), Ct), "WriteNotGranted");

        Assert.DoesNotContain(Fresh, rename.GetRawText(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(reorder.GetProperty("userAction").GetString()));
        Assert.Empty(holds);
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
    }

    [Fact]
    public async Task StaleBasis_IsStaleWrite_NoSnapshotNoCom()
    {
        Grant();

        var rename = Error(await RenameTool().RenameHierarchyObject(IdeasId, Fresh, Basis(OlderLmt), Ct), "StaleWrite");
        Error(await ReorderTool().ReorderHierarchyChildren(ArchiveId, [NotesId, PlansId, SubId], Basis(OlderLmt), Ct), "StaleWrite");

        Assert.Contains("re-read", rename.GetProperty("userAction").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(rename.TryGetProperty("outcomeUnknown", out _));
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task BasisDate_InAnotherOffset_ForTheSameInstant_Matches()
    {
        Grant();

        Ok(await RenameTool().RenameHierarchyObject(IdeasId, Fresh, Basis("2026-11-13T11:00:00.000+01:00"), Ct));

        Assert.Single(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task NoBasis_RunsWithTheWarningNoBasis()
    {
        Grant();
        ApplyWrites();

        var root = Ok(await RenameTool().RenameHierarchyObject(IdeasId, Fresh, null, Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.NoBasis, warning.GetProperty("code").GetString());
        Assert.Contains(IdeasId, warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Single(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task ABasisWithoutADate_RunsWithTheWarningPartialBasis()
    {
        Grant();
        ApplyWrites();

        var root = Ok(await RenameTool().RenameHierarchyObject(IdeasId, Fresh, new PageBasisArgument(), Ct));

        Assert.Equal(ToolWarnings.PartialBasis, Assert.Single(root.GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(null, "h", false)]
    [InlineData(null, null, true)]
    [InlineData("not a date", null, false)]
    public async Task ABasisCarryingAHash_OrABadDate_IsInvalidArgument_BeforeTheLock(string? date, string? pageHash, bool elementHashes)
    {
        Grant();
        var basis = new PageBasisArgument(date ?? Lmt, pageHash, elementHashes ? new Dictionary<string, string> { [P1] = "h" } : null);

        Error(await RenameTool().RenameHierarchyObject(IdeasId, Fresh, basis, Ct), "InvalidArgument");
        Error(await ReorderTool().ReorderHierarchyChildren(ArchiveId, [NotesId, PlansId, SubId], basis, Ct), "InvalidArgument");

        Assert.Empty(gateway.Calls);
        Assert.Empty(holds);
    }

    [Theory]
    [InlineData(BinId)]
    [InlineData(BinnedId)]
    [InlineData(DeletedPagesId)]
    public async Task TheRecycleBin_OrAnythingInIt_IsObjectNotFound_BeforeCom(string id)
    {
        Grant();

        Error(await RenameTool().RenameHierarchyObject(id, Fresh, Basis(Lmt), Ct), "ObjectNotFound");
        Error(await ReorderTool().ReorderHierarchyChildren(id, [UnknownId], Basis(Lmt), Ct), "ObjectNotFound");

        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AnUnknownId_IsObjectNotFound()
    {
        Grant();

        Error(await RenameTool().RenameHierarchyObject(UnknownId, Fresh, Basis(Lmt), Ct), "ObjectNotFound");

        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task ALockedSection_IsSectionLocked_ForARename_AndAReorderOfItsPages()
    {
        Grant();

        Error(await RenameTool().RenameHierarchyObject(LockedId, Fresh, Basis(Lmt), Ct), "SectionLocked");
        Error(await ReorderTool().ReorderHierarchyChildren(LockedId, [V1], Basis(Lmt), Ct), "SectionLocked");

        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AModalDialog_IsModalDialogOpen_Retryable_WithoutOutcomeUnknown_NamingTheEntry()
    {
        Grant();
        gateway.UpdateHierarchyFailure = new OneNoteException(ErrorCode.ModalDialogOpen, "UpdateHierarchy failed: hrAppInModalUI (0x80042030).", unchecked((int)0x80042030));

        var error = Error(await RenameTool().RenameHierarchyObject(IdeasId, Fresh, Basis(Lmt), Ct), "ModalDialogOpen");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Contains(Entry().EntryId.ToString("D"), error.GetProperty("message").GetString(), StringComparison.Ordinal);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AComTimeout_IsOutcomeUnknown_NotRetryable_NoRetry()
    {
        Grant();
        gateway.UpdateHierarchyFailure = new OneNoteException(ErrorCode.Timeout, "UpdateHierarchy didn't return in time.");

        var error = Error(await ReorderTool().ReorderHierarchyChildren(ArchiveId, [NotesId, PlansId, SubId], Basis(Lmt), Ct), "Timeout");

        Assert.True(error.GetProperty("outcomeUnknown").GetBoolean());
        Assert.False(error.GetProperty("retryable").GetBoolean());
        Assert.Single(gateway.UpdateHierarchyCalls);
        await AssertLockFreeAsync();
    }

    [Theory]
    [InlineData(0x80042017u, "InvalidArgument", false)]
    [InlineData(0x80042024u, "ComBusy", true)]
    [InlineData(0x8004202Eu, "ObjectNotFound", false)]
    public async Task OneNotesOwnRefusal_MapsByTheTriagedRow(uint hresult, string code, bool retryable)
    {
        Grant();
        gateway.UpdateHierarchyFailure = Com.HResultMap.ToException("UpdateHierarchy", new System.Runtime.InteropServices.COMException("refused", unchecked((int)hresult)));

        var error = Error(await RenameTool().RenameHierarchyObject(IdeasId, Fresh, Basis(Lmt), Ct), code);

        Assert.Equal(retryable, error.GetProperty("retryable").GetBoolean());
        Assert.False(error.TryGetProperty("outcomeUnknown", out _));
        Assert.Equal(ToolErrors.FormatHResult(unchecked((int)hresult)), error.GetProperty("hresult").GetString());
    }

    [Fact]
    public async Task AReadBackThatDiffers_IsReadBackDiffers_AndTheWriteStands()
    {
        Grant();

        var root = Ok(await RenameTool().RenameHierarchyObject(IdeasId, Fresh, Basis(Lmt), Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.ReadBackDiffers, warning.GetProperty("code").GetString());
        Assert.Contains(IdeasId, warning.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Fresh, warning.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadBackThatFails_IsReadBackFailed_AndTheWriteStands()
    {
        Grant();
        gateway.OnUpdateHierarchy = _ => gateway.Hierarchies.Remove((IdeasId, HierarchyScope.Self));

        var root = Ok(await RenameTool().RenameHierarchyObject(IdeasId, Fresh, Basis(Lmt), Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.ReadBackFailed, warning.GetProperty("code").GetString());
        Assert.Single(gateway.UpdateHierarchyCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AnAbandonedWriteOutstanding_IsComBusy_NothingReadOrWritten()
    {
        Grant();
        gateway.AbandonedWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Error(await RenameTool().RenameHierarchyObject(IdeasId, Fresh, Basis(Lmt), Ct), "ComBusy");

        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
        gateway.AbandonedWrites.SetResult();
    }

    [Fact]
    public void TheUseCase_IsAHierarchyWrite() =>
        Assert.Equal(OperationClass.HierarchyWrite, UseCase().Class);

    // --- Helpers ----------------------------------------------------------------------------------------------------

    private static string Page(string id, int level) => $"<one:Page ID=\"{id}\" name=\"p\" pageLevel=\"{level}\" lastModifiedTime=\"{Lmt}\" />";

    private void Parent(string id, string parent) => gateway.Parents[id] = parent;

    private void Self(string id, string xml) =>
        gateway.Hierarchies[(id, HierarchyScope.Self)] = "<?xml version=\"1.0\"?>\n" + xml;

    private void Children(string id, string xml) =>
        gateway.Hierarchies[(id, HierarchyScope.Children)] = "<?xml version=\"1.0\"?>\n" + xml;

    /// <summary>Makes the fake apply each <c>UpdateHierarchy</c>, so the read-back sees what was sent.</summary>
    private void ApplyWrites() => gateway.OnUpdateHierarchy = sent =>
    {
        var root = XElement.Parse(sent);
        var id = (string)root.Attribute("ID")!;
        if (root.HasElements)
        {
            var key = (id, HierarchyScope.Children);
            var doc = XDocument.Parse(gateway.Hierarchies[key]);
            var kids = doc.Root!.Elements().ToList();
            var order = root.Elements().Select(e => (string)e.Attribute("ID")!).ToList();
            var listed = order.Select(i => new XElement(kids.Single(k => (string?)k.Attribute("ID") == i)));
            var rest = kids.Where(k => !order.Contains((string)k.Attribute("ID")!)).Select(k => new XElement(k));
            doc.Root.ReplaceNodes([.. listed, .. rest]);
            gateway.Hierarchies[key] = doc.ToString();
        }
        else
        {
            var attribute = root.Attribute("nickname") ?? root.Attribute("name")!;
            var key = (id, HierarchyScope.Self);
            var doc = XDocument.Parse(gateway.Hierarchies[key]);
            doc.Root!.SetAttributeValue(attribute.Name, attribute.Value);
            gateway.Hierarchies[key] = doc.ToString();
        }
    };

    private void Grant(string path = NotebookPath, string id = NotebookId) =>
        new SettingsWriter(Store, clock).SaveWriteGrant(
            new NotebookGrant(GrantKeys.NotebookKey(path), "A", id, Write: true, PermanentDelete: false, clock.GetUtcNow(), GrantedVia.Cli));

    private static PageBasisArgument Basis(string date) => new(date);

    private IReadOnlyList<string> EntryFiles() =>
        Directory.Exists(JournalFolder)
            ? [.. Directory.GetFiles(JournalFolder).Where(f => JournalFiles.IsEntry(Path.GetFileName(f)))]
            : [];

    private JournalEntry Entry() => JournalStore.Read(Assert.Single(EntryFiles()));

    private static string RawEntryJson(string file)
    {
        using var stream = File.OpenRead(file);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    private UpdateHierarchyUseCase UseCase()
    {
        var services = new HierarchyWriteServices(
            gateway,
            new WriteAccess(gateway, Store.Read),
            new ReadAccess(gateway, Store.Read, new SettingsWriter(Store, clock), clock),
            new HierarchySnapshot(gateway, new JournalStore(Store.Read), clock));
        return new UpdateHierarchyUseCase(services, new WritePipeline(writeLock, gateway, cache, Store.Read, TimeProvider.System));
    }

    private static ToolRunner Runner() => new(NullLogger<ToolRunner>.Instance);

    private RenameHierarchyObjectTool RenameTool() => new(Runner(), UseCase());

    private ReorderHierarchyChildrenTool ReorderTool() => new(Runner(), UseCase());

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
