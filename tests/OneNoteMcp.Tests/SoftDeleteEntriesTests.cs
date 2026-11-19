using OneNoteMcp.Core;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 3.5: <see cref="SoftDeleteEntries"/>, the journal side of <c>restore_from_recycle_bin</c>: which entries count,
/// what each recorded, and how a recycled page or section matches them (AD-8; plan Q1, Q5, Q10). Pure over entry lists,
/// except <see cref="SoftDeleteEntries.Load"/>, which reads metadata only and never opens a protected entry's blob.
/// </summary>
public sealed class SoftDeleteEntriesTests : IDisposable
{
    private const string Key = @"C:\notebooks\A";
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";
    private const string Bin = "{B1}";

    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    // --- Which entries count ---------------------------------------------------------------------------------------

    [Fact]
    public void Of_KeepsOnlyThisNotebooksSoftDeletes_KeyIgnoringCase()
    {
        var mine = Page("{P1}", "Lost");
        var otherNotebook = Page("{P2}", "Lost") with { NotebookPath = @"C:\notebooks\B" };
        var notASoftDelete = Page("{P3}", "Lost") with { Operation = "update_page_content" };
        var upper = Page("{P4}", "Lost") with { NotebookPath = Key.ToUpperInvariant() };

        Assert.Equal([mine, upper], SoftDeleteEntries.Of([mine, otherNotebook, notASoftDelete, upper], Key));
    }

    [Fact]
    public void KindOf_TellsPagesSectionsAndGroupsApart_AProtectedSectionByItsSectionKey()
    {
        Assert.Equal(SoftDeleteKind.Page, SoftDeleteEntries.KindOf(Page("{P}", "t")));
        Assert.Equal(SoftDeleteKind.Section, SoftDeleteEntries.KindOf(Section("{S}", "s", @"C:\notebooks\A\s.one")));
        Assert.Equal(SoftDeleteKind.Section, SoftDeleteEntries.KindOf(Protected("{S}", @"C:\notebooks\A\Vault.one")));
        Assert.Equal(SoftDeleteKind.SectionGroup, SoftDeleteEntries.KindOf(Group("{G}", "g")));
        Assert.Equal(SoftDeleteKind.Other, SoftDeleteEntries.KindOf(Page("{P}", "t") with { SnapshotKind = JournalEntry.OnenoteBackupKind }));
        Assert.Equal(SoftDeleteKind.Other, SoftDeleteEntries.KindOf(Section("{S}", "s", "x") with { Xml = "not xml" }));
    }

    [Fact]
    public void ById_NeedsTheKind()
    {
        var page = Page("{P}", "t");
        Assert.Same(page, SoftDeleteEntries.ById([page], page.EntryId, SoftDeleteKind.Page));
        Assert.Null(SoftDeleteEntries.ById([page], page.EntryId, SoftDeleteKind.Section));
        Assert.Null(SoftDeleteEntries.ById([page], Guid.NewGuid(), SoftDeleteKind.Page));
    }

    // --- Pages -------------------------------------------------------------------------------------------------------

    [Fact]
    public void APage_MatchesByRecycleBinId_First_EvenWhenAnotherHasItsName()
    {
        var byId = Page("{OLD1}", "Lost", Bin + "1");
        var sameName = Page("{OLD2}", "Lost");

        var match = SoftDeleteEntries.MatchPage([sameName, byId], Bin + "1", "Lost", [Bin + "1"]);

        Assert.Same(byId, match.Entry);
        Assert.False(match.ByName);
        Assert.Empty(match.Candidates);
    }

    [Fact]
    public void APage_FallsBackToItsName_Ordinal_WhenNoBinIdMatches()
    {
        var stale = Page("{OLD1}", "Lost", "{STALE}");
        var otherCase = Page("{OLD2}", "lost");

        var match = SoftDeleteEntries.MatchPage([stale, otherCase], Bin + "1", "Lost", [Bin + "1"]);

        Assert.Same(stale, match.Entry);
        Assert.True(match.ByName);
    }

    [Fact]
    public void APage_LeavesOutAnEntryOwnedByAnotherBinnedPage()
    {
        // 5.5 L9: three same-named pages; the one whose recycleBinId is another page still in the bin belongs to that page.
        var owned = Page("{OLD1}", "Twin", Bin + "2");
        var free = Page("{OLD2}", "Twin");

        var match = SoftDeleteEntries.MatchPage([owned, free], Bin + "1", "Twin", [Bin + "1", Bin + "2"]);

        Assert.Same(free, match.Entry);
        Assert.True(match.ByName);
    }

    [Fact]
    public void TwoNameMatches_AreCandidates_AndNoEntry()
    {
        var a = Page("{OLD1}", "Twin");
        var b = Page("{OLD2}", "Twin");

        var match = SoftDeleteEntries.MatchPage([a, b], Bin + "1", "Twin", [Bin + "1"]);

        Assert.Null(match.Entry);
        Assert.Equal([a, b], match.Candidates);
    }

    [Fact]
    public void APage_NeverMatchesASectionsEntry_OrAProtectedEntryByName()
    {
        var section = Section("{S}", "Lost", @"C:\notebooks\A\Lost.one");
        var protectedPage = Page("{OLD}", "Lost") with { Title = null, SectionKey = @"C:\notebooks\A\Vault.one" };

        Assert.Same(SoftDeleteMatch.None, SoftDeleteEntries.MatchPage([section, protectedPage], Bin + "1", "Lost", [Bin + "1"]));
    }

    // --- Sections ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ASection_MatchesByItsId_First()
    {
        var own = Section("{S1}", "Other name", @"C:\notebooks\A\x.one");
        var named = Section("{S2}", "Gone", @"C:\notebooks\A\Gone.one");

        var match = SoftDeleteEntries.MatchSection([named, own], "{S1}", "Gone", @"C:\notebooks\A\OneNote_RecycleBin\Gone.one", false, ["{S1}"]);

        Assert.Same(own, match.Entry);
        Assert.False(match.ByName);
    }

    [Fact]
    public void ASectionDeletedTwice_MatchesItsNewestIdEntry_NotCandidates()
    {
        // Review fix 2: deleted, restored and deleted again gives two entries with its ID; the newest is the one.
        var older = Section("{S1}", "Gone", @"C:\notebooks\A\Gone.one") with { TakenAtUtc = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc) };
        var newer = Section("{S1}", "Gone", @"C:\notebooks\A\Gone.one") with { TakenAtUtc = new DateTime(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc) };
        var oldest = Section("{S1}", "Gone", @"C:\notebooks\A\Gone.one") with { TakenAtUtc = new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc) };

        var match = SoftDeleteEntries.MatchSection([older, newer, oldest], "{S1}", "Gone", @"C:\notebooks\A\OneNote_RecycleBin\Gone.one", false, ["{S1}"]);

        Assert.Same(newer, match.Entry);
        Assert.False(match.ByName);
        Assert.Empty(match.Candidates);
    }

    [Fact]
    public void APlainSection_FallsBackToItsName_IgnoringCase_AfterAReopen()
    {
        var entry = Section("{OLD}", "GONE", @"C:\notebooks\A\Gone.one");

        var match = SoftDeleteEntries.MatchSection([entry], "{NEW}", "Gone", @"C:\notebooks\A\OneNote_RecycleBin\Gone.one", false, ["{NEW}"]);

        Assert.Same(entry, match.Entry);
        Assert.True(match.ByName);
    }

    [Fact]
    public void AProtectedSection_FallsBackToTheFileNameInItsSectionKey_NeverAPlainTitle()
    {
        var plain = Section("{OLD1}", "Secret", @"C:\notebooks\A\Secret.one");
        var marked = Protected("{OLD2}", @"C:\notebooks\A\Archive\secret.one");

        var match = SoftDeleteEntries.MatchSection([plain, marked], "{NEW}", "Secret", @"C:\notebooks\A\OneNote_RecycleBin\Secret.one", true, ["{NEW}"]);

        Assert.Same(marked, match.Entry);
        Assert.True(match.ByName);
    }

    [Fact]
    public void ASection_LeavesOutAnotherBinnedSectionsEntry_AndNeverMatchesAGroup()
    {
        var owned = Section("{S2}", "Gone", @"C:\notebooks\A\Gone.one");
        var group = Group("{G}", "Gone");

        var match = SoftDeleteEntries.MatchSection([owned, group], "{S1}", "Gone", @"C:\notebooks\A\OneNote_RecycleBin\Gone.one", false, ["{S1}", "{S2}"]);

        Assert.Same(SoftDeleteMatch.None, match);
    }

    // --- Helpers for the restore -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\notebooks\A\Archive\Secret.one", @"C:\notebooks\A\Archive")]
    [InlineData(@"C:\notebooks\A\Secret.one", @"C:\notebooks\A")]
    [InlineData("https://d.docs.live.net/0123/Cloud/G/Secret.one", "https://d.docs.live.net/0123/Cloud/G")]
    public void FolderOf_IsTheSectionKeyWithoutItsFileName(string key, string folder) =>
        Assert.Equal(folder, SoftDeleteEntries.FolderOf(key));

    [Fact]
    public void OldPath_IsAPlainSectionEntrysRootPath()
    {
        Assert.Equal(@"C:\notebooks\A\Gone.one", SoftDeleteEntries.OldPath(Section("{S}", "Gone", @"C:\notebooks\A\Gone.one")));
        Assert.Null(SoftDeleteEntries.OldPath(Page("{P}", "t")));
        Assert.Null(SoftDeleteEntries.OldPath(Protected("{S}", @"C:\notebooks\A\Vault.one")));
    }

    [Fact]
    public void Describe_GivesEntryIdsTimesAndParents_NeverATitle()
    {
        var entry = Page("{OLD}", "Secret title Zq9") with { ParentId = "{PARENT}", TakenAtUtc = new DateTime(2026, 11, 17, 8, 30, 5, DateTimeKind.Utc) };

        var text = SoftDeleteEntries.Describe([entry]);

        Assert.Equal($"entry {entry.EntryId:D}, deleted 2026-11-17T08:30:05Z, from parent {{PARENT}}", text);
        Assert.DoesNotContain("Zq9", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_MarksAnEntryAnEarlierRestoreUsed_AsAlreadyRestored_AndOnlyThatOne()
    {
        // Backlog 16 (Phill, 2026-11-19): a used entry stays a candidate, marked.
        var used = Page("{OLD}", "Dup") with { ParentId = "{P1}", TakenAtUtc = new DateTime(2026, 11, 17, 8, 30, 5, DateTimeKind.Utc) };
        var fresh = Page("{OLD}", "Dup") with { ParentId = "{P2}", TakenAtUtc = new DateTime(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc) };

        var text = SoftDeleteEntries.Describe([used, fresh], new HashSet<Guid> { used.EntryId });

        Assert.Equal(
            $"entry {used.EntryId:D}, deleted 2026-11-17T08:30:05Z, from parent {{P1}}, already restored; entry {fresh.EntryId:D}, deleted 2026-11-17T09:00:00Z, from parent {{P2}}",
            text);
    }

    [Fact]
    public void Used_IsTheSoftDeleteEntriesThatRestoreEntriesName_OnlyFromRestores()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        JournalEntry[] all =
        [
            new() { Operation = RestoreFromRecycleBinWrite.OperationName, SnapshotKind = JournalEntry.HierarchyKind, RestoredFrom = a },
            new() { Operation = RestoreFromRecycleBinWrite.OperationName, SnapshotKind = JournalEntry.HierarchyKind },
            new() { Operation = "rename_hierarchy_object", SnapshotKind = JournalEntry.HierarchyKind, RestoredFrom = b },
            Page("{OLD}", "Dup"),
        ];

        Assert.Equal([a], SoftDeleteEntries.Used(all));
    }

    // --- Load ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Load_ReadsMetadataOnly_AProtectedEntryKeepsItsBlob_AndUnreadableFilesAreSkipped()
    {
        var folder = temp.Combine("journal");
        var store = new JournalStore(() => throw new InvalidOperationException("settings aren't read by WriteTo"));
        var plain = Page("{P}", "Lost") with { Xml = "<one:Page/>" };
        var secret = Protected("{S}", @"C:\notebooks\A\Vault.one") with { Title = "Vault title", Xml = $"<one:Section {Ns} name=\"Vault\" />" };
        store.WriteTo(folder, plain);
        store.WriteTo(folder, secret);
        File.WriteAllText(Path.Combine(folder, JournalFiles.EntryName(DateTime.UtcNow, 1, Guid.NewGuid())), "not gzip");

        var loaded = SoftDeleteEntries.Load(folder);

        Assert.Equal(2, loaded.Count);
        var readPlain = Assert.Single(loaded, e => e.EntryId == plain.EntryId);
        Assert.Equal("Lost", readPlain.Title);
        var readSecret = Assert.Single(loaded, e => e.EntryId == secret.EntryId);
        Assert.Null(readSecret.Title);
        Assert.Null(readSecret.Xml);
        Assert.NotNull(readSecret.Protected);
        Assert.Equal(@"C:\notebooks\A\Vault.one", readSecret.SectionKey);
    }

    [Fact]
    public void Load_OfAMissingFolder_IsEmpty() =>
        Assert.Empty(SoftDeleteEntries.Load(temp.Combine("nowhere")));

    // --- Entries -----------------------------------------------------------------------------------------------------

    private static JournalEntry Base(string objectId) => new()
    {
        EntryId = Guid.NewGuid(),
        Operation = SoftDeleteEntries.Operation,
        OperationClass = "SoftDelete",
        ObjectId = objectId,
        NotebookPath = Key,
        ParentId = "{PARENT}",
        TakenAtUtc = DateTime.UtcNow,
        BinaryComplete = true,
    };

    private static JournalEntry Page(string objectId, string title, string? recycleBinId = null) =>
        Base(objectId) with { SnapshotKind = JournalEntry.PageKind, Title = title, RecycleBinId = recycleBinId };

    private static JournalEntry Section(string objectId, string name, string path) =>
        Base(objectId) with { SnapshotKind = JournalEntry.HierarchyKind, Title = name, Xml = $"<one:Section {Ns} name=\"{name}\" ID=\"{objectId}\" path=\"{path}\" />" };

    private static JournalEntry Group(string objectId, string name) =>
        Base(objectId) with { SnapshotKind = JournalEntry.HierarchyKind, Title = name, Xml = $"<one:SectionGroup {Ns} name=\"{name}\" ID=\"{objectId}\" />" };

    private static JournalEntry Protected(string objectId, string sectionKey) =>
        Base(objectId) with { SnapshotKind = JournalEntry.HierarchyKind, SectionKey = sectionKey };
}
