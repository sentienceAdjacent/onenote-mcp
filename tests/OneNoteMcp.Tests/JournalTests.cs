using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Server.Journal;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 2.8: the journal's file names and folder resolver (decision Q1), the entry format (AD-10) and its store (gzip,
/// temp then rename, DPAPI for protected sections), and the pruner (retention, size cap, leftovers, vanishing files).
/// Everything runs in a temp folder; nothing touches the real journal.
/// </summary>
public sealed class JournalTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);

    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private string Folder => temp.Combine("journal");

    private string SettingsPath => temp.Combine("settings.json");

    private SettingsStore Store => new(SettingsPath, Folder);

    // --- Names and the folder resolver -------------------------------------------------------------------------------

    [Fact]
    public void EntryName_IsTimestampPidGuid_AndMatchesTheEntryPattern()
    {
        var id = Guid.Parse("0123456789abcdef0123456789abcdef");
        var name = JournalFiles.EntryName(new DateTime(2026, 11, 7, 10, 11, 12, 123, DateTimeKind.Utc), 4242, id);

        Assert.Equal("20261107T101112123-4242-0123456789abcdef0123456789abcdef.json.gz", name);
        Assert.True(JournalFiles.IsEntry(name));
        Assert.False(JournalFiles.IsTemp(name));
        Assert.True(JournalFiles.IsTemp(JournalFiles.TempName(name)));
        Assert.False(JournalFiles.IsEntry(JournalFiles.TempName(name)));
        Assert.True(JournalFiles.TryParseTimestamp(JournalFiles.TempName(name), out var at));
        Assert.Equal(new DateTime(2026, 11, 7, 10, 11, 12, 123, DateTimeKind.Utc), at);
        Assert.Equal(DateTimeKind.Utc, at.Kind);
    }

    [Theory]
    [InlineData("20261107T101112124-77-01234567-89ab-cdef-0123-456789abcdef.json.gz", true)]
    [InlineData("20261107T101112123-4242-0123456789abcdef0123456789abcdef.json", false)]
    [InlineData("x20261107T101112123-4242-0123456789abcdef0123456789abcdef.json.gz", false)]
    [InlineData("notes.json.gz", false)]
    [InlineData("20261399T101112123-4242-0123456789abcdef0123456789abcdef.json.gz", true)]
    public void EntryPattern_MatchesOnlyJournalNames(string name, bool entry)
    {
        Assert.Equal(entry, JournalFiles.IsEntry(name));
    }

    [Fact]
    public void TryParseTimestamp_ImpossibleDate_IsFalse()
    {
        Assert.False(JournalFiles.TryParseTimestamp("20261399T101112123-4242-0123456789abcdef0123456789abcdef.json.gz", out _));
        Assert.False(JournalFiles.TryParseTimestamp("settings.json", out _));
    }

    [Fact]
    public void DefaultFolder_JournalOverrideWins_ThenBesideTheSettingsOverride_ThenLocalAppData()
    {
        var journalDir = temp.Combine("j");
        var settingsFile = temp.Combine(Path.Combine("s", "settings.json"));

        Assert.Equal(journalDir, JournalFiles.DefaultFolder(Env((JournalFiles.DirectoryOverrideVariable, journalDir), (SettingsStore.PathOverrideVariable, settingsFile))));
        Assert.Equal(temp.Combine(Path.Combine("s", "journal")), JournalFiles.DefaultFolder(Env((SettingsStore.PathOverrideVariable, settingsFile))));
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneNoteMcp", "journal"),
            JournalFiles.DefaultFolder(Env((JournalFiles.DirectoryOverrideVariable, "  "))));
    }

    [Fact]
    public void SettingsStore_FromTheEnvironment_UsesTheResolver_AndJournalPathInSettingsWins()
    {
        var settingsFile = temp.Combine("settings.json");
        var env = Env((SettingsStore.PathOverrideVariable, settingsFile));
        var store = SettingsStore.FromEnvironment(env);

        Assert.Equal(temp.Combine("journal"), store.Read().Journal.Path);

        var elsewhere = temp.Combine("elsewhere");
        File.WriteAllText(settingsFile, JsonSerializer.Serialize(new { schemaVersion = 1, journal = new { path = elsewhere } }));
        Assert.Equal(elsewhere, store.Read().Journal.Path);

        // An invalid file falls back to the resolved default, not %LOCALAPPDATA%.
        File.WriteAllText(settingsFile, "{");
        Assert.Equal(temp.Combine("journal"), store.Read().Journal.Path);
    }

    [Fact]
    public void SettingsStore_FromTheEnvironment_JournalOverrideWinsOverTheSettingsBesideFolder_ButNotOverJournalPath()
    {
        var settingsFile = temp.Combine("settings.json");
        var journalDir = temp.Combine("override");
        var store = SettingsStore.FromEnvironment(Env((SettingsStore.PathOverrideVariable, settingsFile), (JournalFiles.DirectoryOverrideVariable, journalDir)));

        Assert.Equal(journalDir, store.Read().Journal.Path);

        var elsewhere = temp.Combine("elsewhere");
        File.WriteAllText(settingsFile, JsonSerializer.Serialize(new { schemaVersion = 1, journal = new { path = elsewhere } }));
        Assert.Equal(elsewhere, store.Read().Journal.Path);
    }

    // --- The store ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Write_ThenRead_RoundTrips_OneGzipFile_NoTempLeft()
    {
        var entry = Entry();
        var written = new JournalStore(Store.Read).Write(entry);

        var files = Directory.GetFiles(Folder);
        var only = Assert.Single(files);
        Assert.Equal(written.Path, only);
        Assert.Equal(JournalFiles.EntryName(entry.TakenAtUtc, Environment.ProcessId, entry.EntryId), Path.GetFileName(only));
        Assert.Equal(new FileInfo(only).Length, written.Bytes);
        Assert.False(written.Protected);

        var read = JournalStore.Read(only);
        Assert.Equal(entry.EntryId, read.EntryId);
        Assert.Equal(entry.Xml, read.Xml);
        Assert.Equal(entry.Title, read.Title);
        Assert.Equal(entry.TakenAtUtc, read.TakenAtUtc);
        Assert.Equal(entry.MissingBinary, read.MissingBinary);
        Assert.Equal("PageWrite", read.OperationClass);
        Assert.Null(read.SectionKey);
        Assert.Null(read.Protected);
    }

    // --- Hierarchy snapshots (story 5.4) --------------------------------------------------------------------------

    private const string HierarchyNs = "xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\"";

    [Fact]
    public async Task HierarchySnapshot_OfASectionInAGroup_IsAHierarchyEntry_PlacedByTheWalk()
    {
        var gateway = new FakeGateway();
        gateway.Parents["{S}"] = "{G}";
        gateway.Parents["{G}"] = "{N}";
        gateway.Parents["{N}"] = string.Empty;
        gateway.Hierarchies[("{G}", HierarchyScope.Self)] = $"<one:SectionGroup {HierarchyNs} name=\"Group\" ID=\"{{G}}\" />";
        gateway.Hierarchies[("{N}", HierarchyScope.Self)] = $"<one:Notebook {HierarchyNs} name=\"N\" nickname=\"Shown\" ID=\"{{N}}\" path=\"C:\\n\\N\\\" />";
        var xml = $"<one:Section {HierarchyNs} name=\"Ideas\" ID=\"{{S}}\" path=\"C:\\n\\N\\Group\\Ideas.one\" />";

        var written = await new HierarchySnapshot(gateway, new JournalStore(Store.Read), new FakeClock())
            .TakeAsync(new HierarchySnapshotRequest("c", "rename_hierarchy_object", OperationClass.HierarchyWrite, "{S}", xml), TestContext.Current.CancellationToken);

        var entry = JournalStore.Read(written.Path);
        Assert.False(written.Protected);
        Assert.Equal(JournalEntry.HierarchyKind, entry.SnapshotKind);
        Assert.Equal("HierarchyWrite", entry.OperationClass);
        Assert.Equal("{S}", entry.ObjectId);
        Assert.Equal("{N}", entry.NotebookId);
        Assert.Equal(@"C:\n\N", entry.NotebookPath);
        Assert.Equal("Shown", entry.NotebookName);
        Assert.Equal("{G}", entry.ParentId);
        Assert.Equal("Group", entry.ParentNamePath);
        Assert.Equal("Ideas", entry.Title);
        Assert.Equal(xml, entry.Xml);
        Assert.True(entry.BinaryComplete);
    }

    [Fact]
    public async Task HierarchySnapshot_RootedAtAProtectedSection_IsProtected_ByItsPreWriteSectionKey()
    {
        var gateway = new FakeGateway();
        gateway.Parents["{S}"] = "{N}";
        gateway.Parents["{N}"] = string.Empty;
        gateway.Hierarchies[("{N}", HierarchyScope.Self)] = $"<one:Notebook {HierarchyNs} name=\"N\" ID=\"{{N}}\" path=\"C:\\n\\N\" />";
        var xml = $"<one:Section {HierarchyNs} name=\"Vault\" ID=\"{{S}}\" path=\"C:\\n\\N\\Vault.one\" encrypted=\"true\"><one:Page ID=\"{{P}}\" name=\"Secret title\" /></one:Section>";

        var written = await new HierarchySnapshot(gateway, new JournalStore(Store.Read), new FakeClock())
            .TakeAsync(new HierarchySnapshotRequest("c", "reorder_hierarchy_children", OperationClass.HierarchyWrite, "{S}", xml), TestContext.Current.CancellationToken);

        Assert.True(written.Protected);
        Assert.DoesNotContain("Secret title", Raw(written.Path).ToJsonString(), StringComparison.Ordinal);
        var entry = JournalStore.Read(written.Path);
        Assert.Equal(@"C:\n\N\Vault.one", entry.SectionKey);
        Assert.Equal(xml, entry.Xml);
    }

    [Fact]
    public async Task HierarchySnapshot_OfUnreadableXml_OrAnUnplacedNode_IsJournalFailed()
    {
        var gateway = new FakeGateway();
        gateway.Parents["{S}"] = string.Empty;
        var snapshot = new HierarchySnapshot(gateway, new JournalStore(Store.Read), new FakeClock());

        var unreadable = await Assert.ThrowsAsync<OneNoteException>(() => snapshot.TakeAsync(
            new HierarchySnapshotRequest("c", "x", OperationClass.HierarchyWrite, "{S}", "<one:Section"), TestContext.Current.CancellationToken));
        var unplaced = await Assert.ThrowsAsync<OneNoteException>(() => snapshot.TakeAsync(
            new HierarchySnapshotRequest("c", "x", OperationClass.HierarchyWrite, "{S}", $"<one:Section {HierarchyNs} name=\"S\" ID=\"{{S}}\" />"), TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCode.JournalFailed, unreadable.Code);
        Assert.Equal(ErrorCode.JournalFailed, unplaced.Code);
        Assert.False(Directory.Exists(Folder) && Directory.GetFiles(Folder).Length > 0);
    }

    [Fact]
    public void Write_TheFile_IsAd10sFormat_NullsLeftOut_DatesUtcIso()
    {
        var written = new JournalStore(Store.Read).Write(Entry() with { MissingBinary = [new MissingBinary("image", "{O}{1}{B0}", null)], BinaryComplete = false });
        var json = Raw(written.Path);

        Assert.Equal(1, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal("page", json["snapshotKind"]!.GetValue<string>());
        Assert.Equal("2026-11-12T09:30:00.123Z", json["takenAtUtc"]!.GetValue<string>());
        Assert.True(Guid.TryParse(json["entryId"]!.GetValue<string>(), out _));
        Assert.False(json.ContainsKey("sectionKey"));
        Assert.False(json.ContainsKey("protected"));
        Assert.False(json.ContainsKey("backupFile"));
        Assert.False(json["binaryComplete"]!.GetValue<bool>());
        var missing = Assert.Single(json["missingBinary"]!.AsArray())!.AsObject();
        Assert.Equal("image", missing["kind"]!.GetValue<string>());
        Assert.False(missing.ContainsKey("callbackId"));
        foreach (var name in new[] { "correlationId", "operation", "operationClass", "objectId", "notebookId", "notebookPath", "notebookName", "parentId", "parentNamePath", "title", "xml" })
        {
            Assert.True(json.ContainsKey(name), name);
        }
    }

    [Fact]
    public void Read_IgnoresMembersItDoesntKnow()
    {
        var written = new JournalStore(Store.Read).Write(Entry());
        var json = Raw(written.Path);
        json["recycleBinPageId"] = "{RB}{1}{E9}";
        json["future"] = new JsonObject { ["nested"] = 1 };
        WriteRaw(written.Path, json);

        var read = JournalStore.Read(written.Path);

        Assert.Equal(Entry().Xml, read.Xml);
        Assert.Equal(Entry().ObjectId, read.ObjectId);
    }

    [Fact]
    public void Write_Protected_EncryptsTitleAndXml_KeepsTheRestReadable()
    {
        var entry = Entry() with { SectionKey = @"C:\notebooks\A\Vault.one" };
        var written = new JournalStore(Store.Read).Write(entry);

        Assert.True(written.Protected);
        var json = Raw(written.Path);
        Assert.False(json.ContainsKey("title"));
        Assert.False(json.ContainsKey("xml"));
        Assert.Equal(@"C:\notebooks\A\Vault.one", json["sectionKey"]!.GetValue<string>());
        Assert.Equal(entry.ParentNamePath, json["parentNamePath"]!.GetValue<string>());
        var text = Decompressed(written.Path);
        Assert.DoesNotContain("SECRET-TEXT", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret title", text, StringComparison.Ordinal);

        var read = JournalStore.Read(written.Path);
        Assert.Equal(entry.Title, read.Title);
        Assert.Equal(entry.Xml, read.Xml);
        Assert.Null(read.Protected);
    }

    [Fact]
    public void Read_ProtectedBlobMovedIntoAnotherEntry_DoesntDecrypt()
    {
        var store = new JournalStore(Store.Read);
        var a = store.Write(Entry() with { SectionKey = "k" });
        var b = store.Write(Entry() with { SectionKey = "k", TakenAtUtc = Now.AddMinutes(-1) });
        var blob = Raw(a.Path)["protected"]!.GetValue<string>();
        var json = Raw(b.Path);
        json["protected"] = blob;
        WriteRaw(b.Path, json);

        var ex = Assert.Throws<OneNoteException>(() => JournalStore.Read(b.Path));

        Assert.Equal(ErrorCode.JournalFailed, ex.Code);
    }

    [Fact]
    public void Write_FolderIsAFile_IsJournalFailed_MessageHasNoPath()
    {
        File.WriteAllText(Folder, "a file where the folder should be");
        var entry = Entry();

        var ex = Assert.Throws<OneNoteException>(() => new JournalStore(Store.Read).Write(entry));

        Assert.Equal(ErrorCode.JournalFailed, ex.Code);
        Assert.False(ex.OutcomeUnknown);
        Assert.Contains(entry.EntryId.ToString("D"), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(temp.Path, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_ReportsEachEntryToItsCallback()
    {
        var seen = new List<JournalWritten>();
        var written = new JournalStore(Store.Read, seen.Add).Write(Entry());

        Assert.Equal(written, Assert.Single(seen));
        Assert.Equal("corr-1", written.CorrelationId);
    }

    [Fact]
    public void Write_ACallbackThatThrows_IsNotJournalFailed_TheEntryIsStored()
    {
        var store = new JournalStore(Store.Read, _ => throw new IOException("log sink broke"));

        var ex = Assert.Throws<IOException>(() => store.Write(Entry()));

        Assert.Equal("log sink broke", ex.Message);
        Assert.Single(Directory.GetFiles(Folder));
    }

    [Fact]
    public void Write_RereadsTheSettingsAtEveryWrite()
    {
        var store = new JournalStore(Store.Read);
        store.Write(Entry());
        var moved = temp.Combine("moved");
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { schemaVersion = 1, journal = new { path = moved } }));

        var second = store.Write(Entry());

        Assert.Equal(moved, Path.GetDirectoryName(second.Path));
        Assert.Single(Directory.GetFiles(Folder));
    }

    // --- The pruner --------------------------------------------------------------------------------------------------

    [Fact]
    public void Prune_Retention_RemovesTheEntryPastIt_KeepsTheOther()
    {
        var old = Seed(Now.AddDays(-31), 10);
        var recent = Seed(Now.AddDays(-29), 10);

        var result = Pruner(retentionDays: 30).Prune();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.Equal(1, result.RemovedByAge);
        Assert.Equal(1, result.Kept);
    }

    [Fact]
    public void Prune_HugeRetention_KeepsEverything_NoOverflow()
    {
        var ancient = Seed(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), 10);

        var result = Pruner(retentionDays: int.MaxValue).Prune();

        Assert.True(File.Exists(ancient));
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public void Prune_SizeCap_RemovesOldestUntilUnder_KeepsTheNewest()
    {
        const int KB600 = 600 * 1024;
        var oldest = Seed(Now.AddHours(-3), KB600);
        var middle = Seed(Now.AddHours(-2), KB600);
        var newest = Seed(Now.AddHours(-1), KB600);

        var result = Pruner(maxSizeMB: 1).Prune();

        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(middle));
        Assert.True(File.Exists(newest));
        Assert.Equal(2, result.RemovedBySize);
        Assert.Equal(KB600, result.KeptBytes);
    }

    [Fact]
    public void Prune_SizeCap_NeverRemovesTheNewest_EvenWhenItAloneIsOver()
    {
        var older = Seed(Now.AddHours(-2), 1536 * 1024);
        var newest = Seed(Now.AddHours(-1), 1536 * 1024);

        Pruner(maxSizeMB: 1).Prune();

        Assert.False(File.Exists(older));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void Prune_TempLeftovers_OlderThanADayGo_YoungerStay()
    {
        var stale = Seed(Now.AddDays(-2), 5, temporary: true);
        var fresh = Seed(Now.AddHours(-1), 5, temporary: true);

        var result = Pruner().Prune();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.Equal(1, result.RemovedTemp);
    }

    [Fact]
    public void Prune_TouchesOnlyJournalNamedFiles()
    {
        Directory.CreateDirectory(Folder);
        var foreign = Path.Combine(Folder, "notes.txt");
        File.WriteAllText(foreign, "x");
        File.SetLastWriteTimeUtc(foreign, Now.AddYears(-1));
        var nested = Path.Combine(Folder, "sub");
        Directory.CreateDirectory(nested);
        var nestedEntry = Path.Combine(nested, JournalFiles.EntryName(Now.AddDays(-90), 1, Guid.NewGuid()));
        File.WriteAllText(nestedEntry, "x");

        Pruner(retentionDays: 1).Prune();

        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(nestedEntry));
    }

    [Fact]
    public void Prune_AnEntryVanishingMidPrune_IsIgnored()
    {
        var a = Seed(Now.AddDays(-40), 10);
        var b = Seed(Now.AddDays(-35), 10);
        var pruner = Pruner();
        pruner.BeforeRemove = path =>
        {
            if (path == a)
            {
                File.Delete(a); // another process got there first
            }
        };

        var result = pruner.Prune();

        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.Equal(1, result.RemovedByAge);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public void Prune_AFileInUse_IsCountedAndLeft_TheRestStillPruned()
    {
        var held = Seed(Now.AddDays(-40), 10);
        var other = Seed(Now.AddDays(-35), 10);

        JournalPruneResult result;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = Pruner().Prune();
        }

        Assert.True(File.Exists(held));
        Assert.False(File.Exists(other));
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public void Prune_NoFolder_IsAnEmptyJournal()
    {
        var result = Pruner().Prune();

        Assert.Equal(new JournalPruneResult(0, 0, 0, 0, 0, 0, 0), result);
    }

    [Fact]
    public void Prune_RereadsTheSettingsAtEveryPrune()
    {
        var entry = Seed(Now.AddDays(-10), 10);
        var pruner = new JournalPruner(Store.Read, new FixedClock(Now));
        pruner.Prune();
        Assert.True(File.Exists(entry));

        WriteJournalSettings(retentionDays: 5);
        pruner.Prune();

        Assert.False(File.Exists(entry));
    }

    [Fact]
    public void Prune_UsesTheSettingsOverrideFolder_DecisionQ1()
    {
        var settingsFile = temp.Combine(Path.Combine("profile", "settings.json"));
        var env = Env((SettingsStore.PathOverrideVariable, settingsFile));
        var store = SettingsStore.FromEnvironment(env);
        var folder = temp.Combine(Path.Combine("profile", "journal"));
        Directory.CreateDirectory(folder);
        var old = Path.Combine(folder, JournalFiles.EntryName(Now.AddDays(-60), 1, Guid.NewGuid()));
        File.WriteAllText(old, "x");

        new JournalPruner(store.Read, new FixedClock(Now)).Prune();

        Assert.False(File.Exists(old));
    }

    [Fact]
    public void Prune_JournalOverrideWinsOverTheSettingsBesideFolder_DecisionQ1()
    {
        var settingsFile = temp.Combine(Path.Combine("profile", "settings.json"));
        var journalDir = temp.Combine("override");
        var store = SettingsStore.FromEnvironment(Env((SettingsStore.PathOverrideVariable, settingsFile), (JournalFiles.DirectoryOverrideVariable, journalDir)));
        var beside = temp.Combine(Path.Combine("profile", "journal"));
        Directory.CreateDirectory(beside);
        Directory.CreateDirectory(journalDir);
        var besideOld = Path.Combine(beside, JournalFiles.EntryName(Now.AddDays(-60), 1, Guid.NewGuid()));
        var overrideOld = Path.Combine(journalDir, JournalFiles.EntryName(Now.AddDays(-60), 1, Guid.NewGuid()));
        File.WriteAllText(besideOld, "x");
        File.WriteAllText(overrideOld, "x");

        new JournalPruner(store.Read, new FixedClock(Now)).Prune();

        Assert.False(File.Exists(overrideOld));
        Assert.True(File.Exists(besideOld));
    }

    [Theory]
    [InlineData("{", "settingsInvalid")]
    [InlineData("{\"schemaVersion\": 99}", "settingsNewerSchema")]
    public void Prune_SettingsInvalidOrNewer_RemovesNothing_SaysSkipped(string settingsJson, string reason)
    {
        var old = Seed(Now.AddDays(-400), 10);
        var staleTemp = Seed(Now.AddDays(-400), 10, temporary: true);
        File.WriteAllText(SettingsPath, settingsJson);

        var result = new JournalPruner(Store.Read, new FixedClock(Now)).Prune();

        Assert.True(File.Exists(old));
        Assert.True(File.Exists(staleTemp));
        Assert.Equal(reason, result.SkippedReason);
    }

    [Fact]
    public void Prune_SettingsAbsent_PrunesOnTheDefaults()
    {
        var old = Seed(Now.AddDays(-31), 10);

        var result = new JournalPruner(Store.Read, new FixedClock(Now)).Prune();

        Assert.False(File.Exists(old));
        Assert.Null(result.SkippedReason);
    }

    // --- What a snapshot lacks (Xml) ---------------------------------------------------------------------------------

    [Fact]
    public void MissingBinaries_SeveralImagesInOneOE_SomeMissing_TheLastOnesAreListed()
    {
        static string PageXml(string images) =>
            $"<one:Page xmlns:one=\"{OneNoteXml.Namespace2013}\" ID=\"p\"><one:Outline objectID=\"{{O}}{{1}}{{B0}}\"><one:OEChildren>"
            + $"<one:OE objectID=\"{{O}}{{2}}{{B0}}\">{images}</one:OE></one:OEChildren></one:Outline>"
            + "<one:Image objectID=\"{I}{9}{B0}\"><one:CallbackID callbackID=\"{I}{10}{B0}\" /></one:Image></one:Page>";
        static string Image(int n) => $"<one:Image><one:CallbackID callbackID=\"{{C}}{{{n}}}{{B0}}\" /></one:Image>";
        var basic = PageXml(Image(1) + Image(2) + Image(3));
        var binary = PageXml("<one:Image><one:Data>AA==</one:Data></one:Image>");

        var missing = ImageCount.MissingBinaries(basic, binary);

        Assert.Equal(
            [new MissingBinary("image", "{O}{2}{B0}", "{C}{2}{B0}"), new MissingBinary("image", "{O}{2}{B0}", "{C}{3}{B0}")],
            missing);
    }

    [Fact]
    public void MissingBinaries_LocalPage_None()
    {
        Assert.Empty(ImageCount.MissingBinaries(
            FakeGateway.ReadFixture("mcp-test/page/image-Basic.xml"),
            FakeGateway.ReadFixture("mcp-test/page/image-BinaryData.xml")));
    }

    [Fact]
    public void MissingBinaries_NotWellFormed_Throws()
    {
        Assert.Throws<OneNoteXmlException>(() => ImageCount.MissingBinaries("<x", "<y/>"));
    }

    // --- The prune service -------------------------------------------------------------------------------------------

    [Fact]
    public async Task PruneService_PrunesAtStartup_ThenWhenTheDailyTimerFires()
    {
        var clock = new ManualTimers(Now);
        var first = Seed(Now.AddDays(-31), 10);
        using var service = new JournalPruneService(new JournalPruner(Store.Read, clock), clock, NullLogger<JournalPruneService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await clock.WaitForTimerAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(first));
        Assert.Equal(JournalPruneService.Interval, clock.LastDue);

        var second = Seed(Now.AddDays(-31), 10);
        clock.FireAll();
        await clock.WaitForTimerAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(second));

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    // --- Story 5.5: the one-time recycleBinId rewrite ----------------------------------------------------------------

    [Fact]
    public void AddRecycleBinId_RewritesTheEntryOnce_KeepingEveryOtherMember_AndLeavesNoTempFile()
    {
        var entry = Entry();
        var written = new JournalStore(Store.Read).Write(entry);
        var before = Raw(written.Path);
        Assert.False(before.ContainsKey("recycleBinId"));

        Assert.Null(JournalStore.TryAddRecycleBinId(written.Path, entry.EntryId, "{RB}{1}{E9}"));

        var after = Raw(written.Path);
        Assert.Equal("{RB}{1}{E9}", after["recycleBinId"]!.GetValue<string>());
        after.Remove("recycleBinId");
        Assert.True(JsonNode.DeepEquals(before, after));
        Assert.Equal("{RB}{1}{E9}", JournalStore.Read(written.Path).RecycleBinId);
        Assert.Equal([Path.GetFileName(written.Path)], Directory.GetFiles(Folder).Select(Path.GetFileName));
    }

    [Fact]
    public void AddRecycleBinId_OnAProtectedEntry_KeepsTheBlobAsItWas_WithNoTitleOrXml()
    {
        var entry = Entry() with { SectionKey = @"C:\notebooks\A\Vault.one" };
        var written = new JournalStore(Store.Read).Write(entry);
        var blob = Raw(written.Path)["protected"]!.GetValue<string>();

        Assert.Null(JournalStore.TryAddRecycleBinId(written.Path, entry.EntryId, "{RB}{1}{E9}"));

        var raw = Raw(written.Path);
        Assert.Equal(blob, raw["protected"]!.GetValue<string>());
        Assert.False(raw.ContainsKey("title"));
        Assert.False(raw.ContainsKey("xml"));
        Assert.DoesNotContain("SECRET", Decompressed(written.Path), StringComparison.Ordinal);
        var read = JournalStore.Read(written.Path);
        Assert.Equal("{RB}{1}{E9}", read.RecycleBinId);
        Assert.Equal(entry.Xml, read.Xml);
    }

    [Fact]
    public void AddRecycleBinId_AMissingFile_IsReported_NotThrown()
    {
        var why = JournalStore.TryAddRecycleBinId(Path.Combine(Folder, "20261112T093000123-1-0123456789abcdef0123456789abcdef.json.gz"), Guid.NewGuid(), "{RB}");

        Assert.Equal("the entry file is gone", why);
        Assert.False(Directory.Exists(Folder) && Directory.EnumerateFileSystemEntries(Folder).Any());
    }

    [Fact]
    public void AddRecycleBinId_AFileHoldingAnotherEntry_IsReported_AndLeftAsItWas()
    {
        var entry = Entry();
        var written = new JournalStore(Store.Read).Write(entry);
        var bytes = File.ReadAllBytes(written.Path);

        Assert.Equal("the entry file holds another entry", JournalStore.TryAddRecycleBinId(written.Path, Guid.NewGuid(), "{RB}"));

        Assert.Equal(bytes, File.ReadAllBytes(written.Path));
    }

    // --- Helpers -----------------------------------------------------------------------------------------------------

    internal static JournalEntry Entry() => new()
    {
        EntryId = Guid.NewGuid(),
        CorrelationId = "corr-1",
        Operation = "update_page_content",
        OperationClass = nameof(OperationClass.PageWrite),
        ObjectId = "{P}{1}{E1}",
        NotebookId = "{NB}{1}{B0}",
        NotebookPath = @"C:\notebooks\A",
        NotebookName = "A",
        ParentId = "{S}{1}{B0}",
        ParentNamePath = "Group/Section",
        Title = "Secret title",
        TakenAtUtc = new DateTime(2026, 11, 12, 9, 30, 0, 123, DateTimeKind.Utc),
        BinaryComplete = true,
        Xml = "<one:Page xmlns:one=\"" + OneNoteXml.Namespace2013 + "\" name=\"Secret title\"><one:T><![CDATA[SECRET-TEXT & <b>]]></one:T></one:Page>",
    };

    private JournalPruner Pruner(int retentionDays = 30, int maxSizeMB = 2048)
    {
        WriteJournalSettings(retentionDays, maxSizeMB);
        return new JournalPruner(Store.Read, new FixedClock(Now));
    }

    private void WriteJournalSettings(int retentionDays = 30, int maxSizeMB = 2048) =>
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { schemaVersion = 1, journal = new { path = Folder, retentionDays, maxSizeMB } }));

    private string Seed(DateTime takenAtUtc, int bytes, bool temporary = false)
    {
        Directory.CreateDirectory(Folder);
        var name = JournalFiles.EntryName(takenAtUtc, 77, Guid.NewGuid());
        var path = Path.Combine(Folder, temporary ? JournalFiles.TempName(name) : name);
        using var file = new FileStream(path, FileMode.CreateNew);
        file.SetLength(bytes);
        return path;
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    private static string Decompressed(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static JsonObject Raw(string path) => JsonNode.Parse(Decompressed(path))!.AsObject();

    private static void WriteRaw(string path, JsonObject json)
    {
        using var file = new FileStream(path, FileMode.Create);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        gzip.Write(Encoding.UTF8.GetBytes(json.ToJsonString()));
    }

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    /// <summary>A clock whose timers fire only when the test says, and which signals each timer it creates.</summary>
    private sealed class ManualTimers(DateTime utcNow) : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        private TaskCompletionSource created = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan LastDue { get; private set; }

        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            lock (timers)
            {
                timers.Add(timer);
                LastDue = dueTime;
                created.TrySetResult();
            }

            return timer;
        }

        /// <summary>Waits until a timer has been created since the last wait (the service is between prunes).</summary>
        public async Task WaitForTimerAsync(CancellationToken ct)
        {
            Task wait;
            lock (timers)
            {
                wait = created.Task;
            }

            await wait.WaitAsync(TimeSpan.FromSeconds(10), ct);
            lock (timers)
            {
                created = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void FireAll()
        {
            ManualTimer[] due;
            lock (timers)
            {
                due = [.. timers];
                timers.Clear();
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;

            public void Fire()
            {
                if (!disposed)
                {
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => disposed = true;

            public ValueTask DisposeAsync()
            {
                disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
