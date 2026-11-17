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

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 3.14: <c>set_page_level</c> through the AD-7 write pipeline as one <c>HierarchyWrite</c>, one test or theory per row
/// of the plan's I/O matrix, against <see cref="FakeGateway"/> scripted with a synthetic notebook A: section Ideas (its
/// pages set per test as "name:level"), Vault (protected), Locked (protected, locked; its page listed by its parent chain
/// only, as OneNote hides a locked section's pages, 3.3 sitting S1), and the recycle bin with Deleted Pages. IDs are
/// well-formed OneNote IDs, because the XML the server sends must pass the XSD.
/// </summary>
public sealed class SetPageLevelToolTests : IDisposable
{
    private const string Lmt = "2026-11-17T10:00:00.000Z";
    private const string OlderLmt = "2026-11-17T09:00:00.000Z";

    private const string NotebookId = "{A0000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string NotebookPath = @"C:\notebooks\A\";
    private const string IdeasId = "{A1000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string VaultId = "{A3000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string LockedId = "{A4000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string BinId = "{A6000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string DeletedPagesId = "{A7000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string V1 = "{A3000000-0000-4000-8000-000000000001}{1}{E11}";
    private const string V2 = "{A3000000-0000-4000-8000-000000000001}{1}{E12}";
    private const string L1 = "{A4000000-0000-4000-8000-000000000001}{1}{E11}";
    private const string D1 = "{A7000000-0000-4000-8000-000000000001}{1}{E11}";

    /// <summary>A page whose parent chain says Ideas, but which Ideas doesn't list.</summary>
    private const string Stray = "{A1000000-0000-4000-8000-000000000001}{1}{E99}";

    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";
    private const string Ideas = $"name=\"Ideas\" ID=\"{IdeasId}\" path=\"C:\\notebooks\\A\\Ideas.one\" lastModifiedTime=\"{Lmt}\"";
    private const string Vault = $"name=\"Vault\" ID=\"{VaultId}\" path=\"C:\\notebooks\\A\\Vault.one\" encrypted=\"true\" lastModifiedTime=\"{Lmt}\"";

    private static readonly XNamespace One = OneNoteXml.One;

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly FakeGateway gateway = new();
    private readonly ConcurrentQueue<WriteLockHeld> holds = new();
    private readonly WriteLock writeLock;
    private readonly ReadAccess readAccess;
    private readonly PageTextCache cache = PageTextCache.WithDefaults();

    public SetPageLevelToolTests()
    {
        writeLock = new WriteLock($@"Local\OneNoteMcp.Test.{Guid.NewGuid():N}", holds.Enqueue);
        readAccess = new ReadAccess(gateway, Store.Read, new SettingsWriter(Store, clock), clock);
        Directory.CreateDirectory(temp.Combine("cfg"));

        Parent(NotebookId, string.Empty);
        foreach (var id in new[] { IdeasId, VaultId, LockedId, BinId })
        {
            Parent(id, NotebookId);
        }

        Parent(DeletedPagesId, BinId);
        Parent(V1, VaultId);
        Parent(V2, VaultId);
        Parent(L1, LockedId);
        Parent(D1, DeletedPagesId);
        Parent(Stray, IdeasId);

        const string notebook = $"name=\"A\" nickname=\"A\" ID=\"{NotebookId}\" path=\"{NotebookPath}\" lastModifiedTime=\"{Lmt}\"";
        const string locked = $"name=\"Locked\" ID=\"{LockedId}\" path=\"C:\\notebooks\\A\\Locked.one\" encrypted=\"true\" locked=\"true\" lastModifiedTime=\"{Lmt}\"";
        const string bin = $"name=\"OneNote_RecycleBin\" ID=\"{BinId}\" path=\"C:\\notebooks\\A\\OneNote_RecycleBin\" isRecycleBin=\"true\" lastModifiedTime=\"{Lmt}\"";
        const string deleted = $"name=\"Deleted Pages\" ID=\"{DeletedPagesId}\" path=\"C:\\notebooks\\A\\OneNote_RecycleBin\\OneNote_DeletedPages.one\" isInRecycleBin=\"true\" isDeletedPages=\"true\" lastModifiedTime=\"{Lmt}\"";

        Self(NotebookId, $"<one:Notebook {Ns} {notebook} />");
        Self(IdeasId, $"<one:Section {Ns} {Ideas} />");
        Self(VaultId, $"<one:Section {Ns} {Vault} />");
        Self(LockedId, $"<one:Section {Ns} {locked} />");
        Self(BinId, $"<one:SectionGroup {Ns} {bin} />");
        Self(DeletedPagesId, $"<one:Section {Ns} {deleted} />");

        Children(VaultId, $"<one:Section {Ns} {Vault}>{Page(V1, 1)}{Page(V2, 1)}</one:Section>");
        Children(LockedId, $"<one:Section {Ns} {locked} />");
        Children(DeletedPagesId, $"<one:Section {Ns} {deleted}>{Page(D1, 1)}</one:Section>");
        IdeasAre("P:1 A:1 B:2 C:1");

        gateway.Hierarchies[(string.Empty, HierarchyScope.Sections)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebooks {Ns}><one:Notebook {notebook}><one:Section {Ideas} /></one:Notebook></one:Notebooks>";
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The journal folder is the test's own: never the user's real journal under %LOCALAPPDATA%.
    private SettingsStore Store => new(Path.Combine(temp.Combine("cfg"), "settings.json"), temp.Combine("journal"));

    private string JournalFolder => temp.Combine("journal");

    public void Dispose() => temp.Dispose();

    // --- Changes ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Indent_SendsEveryPageInOrder_ThePageAt2_TheOthersAsRead_WithOneEntry()
    {
        Grant();
        IdeasAre("P:1 A:1 C:1");
        ApplyLevels();
        var entriesAtCall = -1;
        gateway.OnUpdateHierarchyCall = _ =>
        {
            entriesAtCall = EntryFiles().Count;
            return Task.CompletedTask;
        };
        cache.Set(Id("A"), DateTimeOffset.UnixEpoch, "cached text", long.MaxValue);
        cache.Set(Id("C"), DateTimeOffset.UnixEpoch, "other text", long.MaxValue);

        var root = Ok(await Tool().SetPageLevel(Id("A"), 2, Basis(Lmt), Ct));

        var meta = root.GetProperty("meta");
        Assert.Equal(["correlationId", "journalEntryId", "objectId", "parentId"], meta.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(Id("A"), meta.GetProperty("objectId").GetString());
        Assert.Equal(IdeasId, meta.GetProperty("parentId").GetString());
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());

        // One call: the section, every page in its order, each with its level; only A changes.
        var sent = Assert.Single(gateway.UpdateHierarchyCalls);
        Assert.Equal("P:1 A:2 C:1", Sent(sent));
        Assert.Equal(IdeasId, (string?)XElement.Parse(sent).Attribute("ID"));
        Assert.Empty(SchemaValidation.Problems(sent));
        Assert.DoesNotContain("isSubPage", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("isCollapsed", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("name=", sent, StringComparison.Ordinal);

        // AD-8: one hierarchy entry of the section, stored before the call.
        Assert.Equal(1, entriesAtCall);
        var entry = JournalStore.Read(JournalFiles.Find(JournalFolder, Guid.Parse(meta.GetProperty("journalEntryId").GetString()!))!);
        Assert.Equal(IdeasId, entry.ObjectId);
        Assert.Equal(JournalEntry.HierarchyKind, entry.SnapshotKind);
        Assert.Equal(SetPageLevelWrite.OperationName, entry.Operation);
        Assert.Equal(nameof(OperationClass.HierarchyWrite), entry.OperationClass);

        // AD-7, AD-12: the page left the cache, another page didn't; the lock is free.
        Assert.False(cache.TryGet(Id("A"), DateTimeOffset.UnixEpoch, out _));
        Assert.True(cache.TryGet(Id("C"), DateTimeOffset.UnixEpoch, out _));
        Assert.Equal(meta.GetProperty("correlationId").GetString(), Assert.Single(holds).CorrelationId);
        await AssertLockFreeAsync();
    }

    [Theory]
    [InlineData("P:1 A:1 B:2", "A", 2, "P:1 A:2 B:3")] // Indent with block
    [InlineData("P:1 A:2 B:2", "A", 1, "P:1 A:1 B:2")] // Outdent re-parents: B now under A
    [InlineData("P:1 A:2 B:3 C:1", "A", 1, "P:1 A:1 B:2 C:1")] // Outdent with block
    [InlineData("P:1 A:1 B:2 C:1", "A", 2, "P:1 A:2 B:3 C:1")] // Indent with block, a page after it
    public async Task AChange_MovesThePagesBlockByTheSameAmount(string pages, string page, int level, string expected)
    {
        Grant();
        IdeasAre(pages);
        ApplyLevels();

        var root = Ok(await Tool().SetPageLevel(Id(page), level, Basis(Lmt), Ct));

        Assert.Equal(expected, Sent(Assert.Single(gateway.UpdateHierarchyCalls)));
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task TheCache_DropsThePageAndEveryPageInItsBlock()
    {
        Grant();
        IdeasAre("P:1 A:1 B:2 C:1");
        foreach (var name in new[] { "A", "B", "C" })
        {
            cache.Set(Id(name), DateTimeOffset.UnixEpoch, "text", long.MaxValue);
        }

        Ok(await Tool().SetPageLevel(Id("A"), 2, Basis(Lmt), Ct));

        Assert.False(cache.TryGet(Id("A"), DateTimeOffset.UnixEpoch, out _));
        Assert.False(cache.TryGet(Id("B"), DateTimeOffset.UnixEpoch, out _));
        Assert.True(cache.TryGet(Id("C"), DateTimeOffset.UnixEpoch, out _));
    }

    [Fact]
    public async Task TheSameLevel_IsUnchanged_NoBasisCheck_NoEntry_NoCall()
    {
        Grant();
        IdeasAre("P:1 A:2");

        // A stale basis too: nothing is written, so there is nothing to protect.
        var root = Ok(await Tool().SetPageLevel(Id("A"), 2, Basis(OlderLmt), Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.Unchanged, warning.GetProperty("code").GetString());
        Assert.False(root.GetProperty("meta").TryGetProperty("journalEntryId", out _));
        Assert.Equal(IdeasId, root.GetProperty("meta").GetProperty("parentId").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
    }

    // --- Level refusals ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("P:1 A:2 B:3", "A", 3, "A")] // Block overflows (the gap before A refuses it first)
    [InlineData("P:1 Q:2 A:2 B:3", "A", 3, "B")] // Block overflows: B would be 4
    [InlineData("P:1 A:1", "A", 3, "A")] // Gap
    [InlineData("P:1 A:1", "P", 2, "P")] // First page
    [InlineData("P:1 Q:2 A:3 F:3", "A", 1, "F")] // Gap after block: F
    public async Task ALevelRule_IsInvalidArgument_BeforeTheLock_NothingSent(string pages, string page, int level, string named)
    {
        Grant();
        IdeasAre(pages);

        var error = Error(await Tool().SetPageLevel(Id(page), level, Basis(Lmt), Ct), "InvalidArgument");

        Assert.Equal(SetPageLevelWrite.LevelUserAction, error.GetProperty("userAction").GetString());
        Assert.Contains(Id(named), error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Name-", error.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
        Assert.Empty(holds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public async Task ALevelOutside1To3_IsInvalidArgument_BeforeAnyDialogOrRead(int level)
    {
        var prompt = new FakePrompt(Typed("A"));

        Error(await SetWithPrompt(prompt, Id("A"), level), "InvalidArgument");

        Assert.Empty(prompt.Order);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task AnEmptyPageId_IsInvalidArgument_BeforeAnyDialogOrRead()
    {
        var prompt = new FakePrompt(Typed("A"));

        Error(await SetWithPrompt(prompt, string.Empty, 2), "InvalidArgument");

        Assert.Empty(prompt.Order);
        Assert.Empty(gateway.Calls);
    }

    [Fact]
    public async Task ABasisWithAHash_IsInvalidArgument_BeforeAnyRead()
    {
        Grant();

        Error(await Tool().SetPageLevel(Id("A"), 2, new PageBasisArgument(Lmt, "abc"), Ct), "InvalidArgument");

        Assert.Empty(gateway.Calls);
    }

    // --- Locked, protected, recycle bin, not a page -----------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALockedSection_IsSectionLocked_WithOrWithoutAReadGrant_NothingSent(bool readGrant)
    {
        // Locked comes before ReadNotGranted (3.14 design note), so the answer is the same either way.
        Grant();
        if (readGrant)
        {
            ReadGrant(@"C:\notebooks\A\Locked.one", "Locked", LockedId);
        }

        Error(await Tool().SetPageLevel(L1, 2, null, Ct), "SectionLocked");

        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(holds);
        Assert.DoesNotContain(gateway.Calls, c => c == (L1, HierarchyScope.Self));
    }

    [Fact]
    public async Task AProtectedSection_WithoutAReadGrant_IsReadNotGranted_NeverAsked_NothingSent()
    {
        Grant();
        var prompt = new FakePrompt();

        var error = Error(await SetWithPrompt(prompt, V2, 2), "ReadNotGranted");

        Assert.Equal(SetPageLevelWrite.ProtectedUserAction, error.GetProperty("userAction").GetString());
        Assert.Contains(VaultId, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Vault", error.GetProperty("message").GetString()!.Replace(VaultId, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Empty(prompt.Questions);
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(holds);
    }

    [Fact]
    public async Task AProtectedSection_ReadNotGranted_ComesBeforeTheLevelRules()
    {
        // V1 is the Vault's first page: without a grant the caller learns nothing about where it sits.
        Grant();

        Error(await Tool().SetPageLevel(V1, 2, null, Ct), "ReadNotGranted");
    }

    [Fact]
    public async Task AProtectedSection_WithAReadGrant_ChangesTheLevel_AndItsEntryIsEncrypted()
    {
        Grant();
        ReadGrant(@"C:\notebooks\A\Vault.one", "Vault", VaultId);

        var root = Ok(await Tool().SetPageLevel(V2, 2, null, Ct));

        Assert.Equal("1 2", string.Join(' ', XElement.Parse(Assert.Single(gateway.UpdateHierarchyCalls)).Elements(One + "Page").Select(p => (string?)p.Attribute("pageLevel"))));
        var entry = JournalStore.Read(JournalFiles.Find(JournalFolder, Guid.Parse(root.GetProperty("meta").GetProperty("journalEntryId").GetString()!))!);
        Assert.Equal(GrantKeys.SectionKey(@"C:\notebooks\A\Vault.one"), entry.SectionKey);
    }

    [Fact]
    public async Task APageInDeletedPages_IsObjectNotFound_NothingSent()
    {
        Grant();

        Error(await Tool().SetPageLevel(D1, 2, null, Ct), "ObjectNotFound");

        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(holds);
    }

    [Theory]
    [InlineData(IdeasId)]
    [InlineData(NotebookId)]
    public async Task ASectionOrNotebookId_IsInvalidArgument_BeforeAnyDialog(string id)
    {
        var prompt = new FakePrompt(Typed("A"));

        var error = Error(await SetWithPrompt(prompt, id, 2), "InvalidArgument");

        Assert.Equal(SetPageLevelWrite.PageUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(prompt.Order);
        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task APageTheSectionDoesntList_IsObjectNotFound()
    {
        Grant();

        Error(await Tool().SetPageLevel(Stray, 2, null, Ct), "ObjectNotFound");

        Assert.Empty(gateway.UpdateHierarchyCalls);
    }

    // --- Grants, basis -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoWriteGrant_WithoutElicitation_IsWriteNotGranted_NothingSent()
    {
        Error(await Tool().SetPageLevel(Id("A"), 2, Basis(Lmt), Ct), "WriteNotGranted");

        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(holds);
    }

    [Fact]
    public async Task TheGrantDialog_GrantsAndTheChangeRuns()
    {
        var prompt = new FakePrompt(Typed("A"));

        Ok(await SetWithPrompt(prompt, Id("A"), 2));

        Assert.Equal(["write"], prompt.Order);
        Assert.Single(gateway.UpdateHierarchyCalls);
    }

    [Fact]
    public async Task ANotebookChangedUnderTheLock_IsWriteNotGranted_Retryable_NothingSent()
    {
        // AD-7: step 1 granted A; by step 3 the page's section sits in another notebook.
        const string otherId = "{C0000000-0000-4000-8000-000000000001}{1}{B0}";
        Grant();
        Parent(otherId, string.Empty);
        Self(otherId, $"<one:Notebook {Ns} name=\"Other\" nickname=\"Other\" ID=\"{otherId}\" path=\"C:\\notebooks\\Other\\\" />");
        var pending = true;
        gateway.OnGetHierarchy = (id, scope) =>
        {
            // Step 1's last read is the section's children; the lock comes next.
            if (pending && id == IdeasId && scope == HierarchyScope.Children)
            {
                pending = false;
                Parent(IdeasId, otherId);
            }
        };

        var error = Error(await Tool().SetPageLevel(Id("A"), 2, Basis(Lmt), Ct), "WriteNotGranted");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Equal(SetPageLevelWrite.NotebookChangedUserAction, error.GetProperty("userAction").GetString());
        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AStaleBasis_IsStaleWrite_NothingSent()
    {
        Grant();

        Error(await Tool().SetPageLevel(Id("A"), 2, Basis(OlderLmt), Ct), "StaleWrite");

        Assert.Empty(gateway.UpdateHierarchyCalls);
        Assert.Empty(EntryFiles());
    }

    [Fact]
    public async Task NoBasis_ChangesWithNoBasis()
    {
        Grant();

        var root = Ok(await Tool().SetPageLevel(Id("A"), 2, null, Ct));

        Assert.Contains(root.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == ToolWarnings.NoBasis);
    }

    // --- COM failures and the read-back ------------------------------------------------------------------------------

    [Fact]
    public async Task AModalDialog_IsModalDialogOpen_Retryable_NamingTheEntry_NeverRetried()
    {
        Grant();
        gateway.UpdateHierarchyFailure = Com.HResultMap.ToException("UpdateHierarchy", new System.Runtime.InteropServices.COMException("modal", unchecked((int)0x80042030)));

        var error = Error(await Tool().SetPageLevel(Id("A"), 2, Basis(Lmt), Ct), "ModalDialogOpen");

        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.Contains("journal entry", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Single(gateway.UpdateHierarchyCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public async Task AReadBackAtAnotherLevel_IsReadBackDiffers()
    {
        // The fake doesn't apply the change: A still reads at level 1.
        Grant();

        var root = Ok(await Tool().SetPageLevel(Id("A"), 2, Basis(Lmt), Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.ReadBackDiffers, warning.GetProperty("code").GetString());
        Assert.Contains($"{Id("A")} sent at 2, read at 1", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadBackWithAPageGoneAndAnotherOrder_IsReadBackDiffers_Twice()
    {
        Grant();
        gateway.OnUpdateHierarchy = _ => IdeasAre("P:1 C:1 A:2");

        var root = Ok(await Tool().SetPageLevel(Id("A"), 2, Basis(Lmt), Ct));

        var messages = root.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("message").GetString()!).ToList();
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, m => m.Contains(Id("B"), StringComparison.Ordinal) && m.Contains("without", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("another order", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReadBackThatFails_IsReadBackFailed_AndTheChangeStands()
    {
        Grant();
        gateway.OnUpdateHierarchy = _ => gateway.Hierarchies.Remove((IdeasId, HierarchyScope.Children));

        var root = Ok(await Tool().SetPageLevel(Id("A"), 2, Basis(Lmt), Ct));

        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray());
        Assert.Equal(ToolWarnings.ReadBackFailed, warning.GetProperty("code").GetString());
        Assert.Single(gateway.UpdateHierarchyCalls);
        await AssertLockFreeAsync();
    }

    [Fact]
    public void TheUseCase_IsAHierarchyWrite() =>
        Assert.Equal(OperationClass.HierarchyWrite, UseCase().Class);

    // --- Helpers ----------------------------------------------------------------------------------------------------

    /// <summary>A well-formed page ID in Ideas for the one-letter <paramref name="name"/>.</summary>
    private static string Id(string name) =>
        string.Create(CultureInfo.InvariantCulture, $"{{A1000000-0000-4000-8000-000000000001}}{{1}}{{E{name[0] - 'A' + 11}}}");

    private static string Page(string id, int level) =>
        $"<one:Page ID=\"{id}\" name=\"Name-{id[^4..]}\" pageLevel=\"{level}\" lastModifiedTime=\"{Lmt}\" />";

    /// <summary>Ideas' pages, as "name:level" in order; each page's parent chain points at Ideas.</summary>
    private void IdeasAre(string pages)
    {
        var xml = new System.Text.StringBuilder();
        foreach (var page in pages.Split(' '))
        {
            var (name, level) = (page.Split(':')[0], int.Parse(page.Split(':')[1], CultureInfo.InvariantCulture));
            Parent(Id(name), IdeasId);
            xml.Append(Page(Id(name), level));
        }

        Children(IdeasId, $"<one:Section {Ns} {Ideas}>{xml}</one:Section>");
    }

    /// <summary>The pages sent, as "name:level" in order.</summary>
    private static string Sent(string xml) =>
        string.Join(' ', XElement.Parse(xml).Elements(One + "Page").Select(p =>
            $"{(char)('A' + int.Parse(((string)p.Attribute("ID")!)[^3..^1], CultureInfo.InvariantCulture) - 11)}:{(string?)p.Attribute("pageLevel")}"));

    /// <summary>Makes the fake apply each sent level, as OneNote does, so the read-back sees what was sent.</summary>
    private void ApplyLevels() => gateway.OnUpdateHierarchy = sent =>
    {
        var root = XElement.Parse(sent);
        var key = ((string)root.Attribute("ID")!, HierarchyScope.Children);
        var section = XDocument.Parse(gateway.Hierarchies[key]);
        var pages = section.Root!.Elements(One + "Page").ToDictionary(p => (string)p.Attribute("ID")!);
        section.Root.ReplaceNodes(root.Elements(One + "Page").Select(p =>
        {
            var copy = new XElement(pages[(string)p.Attribute("ID")!]);
            copy.SetAttributeValue("pageLevel", (string?)p.Attribute("pageLevel"));
            return copy;
        }));
        gateway.Hierarchies[key] = section.ToString();
    };

    private void Parent(string id, string parent) => gateway.Parents[id] = parent;

    private void Self(string id, string xml) =>
        gateway.Hierarchies[(id, HierarchyScope.Self)] = "<?xml version=\"1.0\"?>\n" + xml;

    private void Children(string id, string xml) =>
        gateway.Hierarchies[(id, HierarchyScope.Children)] = "<?xml version=\"1.0\"?>\n" + xml;

    private void Grant() =>
        new SettingsWriter(Store, clock).SaveWriteGrant(
            new NotebookGrant(GrantKeys.NotebookKey(NotebookPath), "A", NotebookId, Write: true, PermanentDelete: false, clock.GetUtcNow(), GrantedVia.Cli));

    private void ReadGrant(string path, string name, string id) =>
        new SettingsWriter(Store, clock).SaveReadGrant(new ProtectedSectionGrant(
            GrantKeys.SectionKey(path), name, GrantKeys.NotebookKey(NotebookPath), id, clock.GetUtcNow(), GrantedVia.Cli));

    private static PageBasisArgument Basis(string date) => new(date);

    private static GrantPromptResult Typed(string name) => new(GrantPromptStatus.Accepted, name);

    private IReadOnlyList<string> EntryFiles() =>
        Directory.Exists(JournalFolder)
            ? [.. Directory.GetFiles(JournalFolder).Where(f => JournalFiles.IsEntry(Path.GetFileName(f)))]
            : [];

    private SetPageLevelUseCase UseCase()
    {
        var services = new HierarchyWriteServices(
            gateway,
            new WriteAccess(gateway, Store.Read),
            readAccess,
            new HierarchySnapshot(gateway, new JournalStore(Store.Read), clock));
        return new SetPageLevelUseCase(
            services,
            new WritePipeline(writeLock, gateway, cache, Store.Read, TimeProvider.System, null, new WriteGrantDialog(gateway, new SettingsWriter(Store, clock), clock)));
    }

    private static ToolRunner Runner() => new(NullLogger<ToolRunner>.Instance);

    private SetPageLevelTool Tool() => new(Runner(), UseCase());

    /// <summary>A level change through the runner with <paramref name="prompt"/> as the call's grant dialog.</summary>
    private Task<CallToolResult> SetWithPrompt(IGrantPrompt prompt, string pageId, int level)
    {
        var useCase = UseCase();
        return Runner().RunAsync(
            SetPageLevelTool.Name,
            useCase,
            (context, token) => useCase.SetLevelAsync(context, pageId, level, null, token),
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
