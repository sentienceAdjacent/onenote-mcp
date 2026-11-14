using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 2.1 (D4): exact duplicate keys in <c>settings.json</c>. The store parses with duplicates allowed and a writer's
/// save without, on purpose (<c>SettingsJson.Options</c>). Story 2.6 closed the known gap: a revoke now clears an exact
/// duplicate grant key too, in either grant area (2.3 plan Q7).
/// </summary>
public sealed class SettingsDuplicateKeyTests : IDisposable
{
    private const string Grant = """{"name": "Vault", "notebookKey": "C:\\nb", "idAtGrant": "{S}{1}{B0}", "grantedAt": "2026-11-07T12:00:00.000Z", "grantedVia": "cli"}""";

    private static readonly string TwoIdenticalGrants =
        """{"schemaVersion": 1, "protectedSections": {"C:\\nb\\Vault.one": GRANT, "C:\\nb\\Vault.one": GRANT}}""".Replace("GRANT", Grant, StringComparison.Ordinal);

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();

    private string SettingsPath => temp.Combine("settings.json");

    public void Dispose() => temp.Dispose();

    [Fact]
    public void Store_AnExactDuplicateTopLevelKey_IsOk_AndTheLastOneWins()
    {
        File.WriteAllText(SettingsPath, """{"schemaVersion": 1, "readGrants": {"onceMinutes": 10}, "readGrants": {"onceMinutes": 20}}""");

        var snapshot = new SettingsStore(SettingsPath, temp.Combine("journal")).Read();

        Assert.Equal(SettingsStatus.Ok, snapshot.Status);
        Assert.Equal(20, snapshot.ReadGrants.OnceMinutes);
    }

    [Fact]
    public void Store_AnExactDuplicateGrantKey_CountsLikeACaseOnlyDuplicate()
    {
        File.WriteAllText(SettingsPath, TwoIdenticalGrants);

        var snapshot = new SettingsStore(SettingsPath, temp.Combine("journal")).Read();

        Assert.Equal(SettingsStatus.Invalid, snapshot.Status);
        Assert.True(snapshot.DuplicateGrantKeysOnly);
        Assert.Equal(2, snapshot.ProtectedSectionEntries.Count);
    }

    private const string WriteGrant = """{"name": "A", "idAtGrant": "{NB}{1}{B0}", "write": true, "permanentDelete": false, "grantedAt": "2026-11-10T12:00:00.000Z", "grantedVia": "cli"}""";

    [Theory]
    [InlineData(@"C:\\nb\\A", @"C:\\nb\\A")] // exact
    [InlineData(@"C:\\nb\\A", @"c:\\NB\\a")] // case only
    public void Store_TwoEqualNotebookKeys_AreInvalid_CountedLikeTheSectionDuplicate(string first, string second)
    {
        // Story 2.4 (AD-6, 2.3 plan Q7): the write grant counts as absent, and a revoke (story 2.6) still runs.
        File.WriteAllText(SettingsPath, "{\"schemaVersion\": 1, \"notebooks\": {\"" + first + "\": " + WriteGrant + ", \"" + second + "\": " + WriteGrant + "}}");

        var snapshot = new SettingsStore(SettingsPath, temp.Combine("journal")).Read();

        Assert.Equal(SettingsStatus.Invalid, snapshot.Status);
        Assert.Equal("notebooks: two keys differ only in case", snapshot.Reason);
        Assert.True(snapshot.DuplicateGrantKeysOnly);
        Assert.True(snapshot.DuplicateNotebookKeys);
        Assert.False(snapshot.DuplicateSectionKeys);
        Assert.Equal(2, snapshot.NotebookEntries.Count);
        Assert.Empty(snapshot.Notebooks);
        Assert.Equal(ErrorCode.SettingsInvalid, Assert.Throws<OneNoteException>(snapshot.EnsureWritable).Code);
    }

    [Fact]
    public void Store_DuplicatesInBothGrantAreas_NameBoth()
    {
        File.WriteAllText(
            SettingsPath,
            """{"schemaVersion": 1, "notebooks": {"C:\\nb": @W@, "c:\\NB": @W@}, "protectedSections": {"C:\\nb\\Vault.one": @G@, "C:\\NB\\Vault.one": @G@}}"""
                .Replace("@W@", WriteGrant, StringComparison.Ordinal)
                .Replace("@G@", Grant, StringComparison.Ordinal));

        var snapshot = new SettingsStore(SettingsPath, temp.Combine("journal")).Read();

        Assert.Equal("notebooks and protectedSections: two keys differ only in case", snapshot.Reason);
        Assert.True(snapshot.DuplicateNotebookKeys && snapshot.DuplicateSectionKeys && snapshot.DuplicateGrantKeysOnly);
    }

    [Fact]
    public void Writer_SaveWriteGrant_OnADuplicateKeyFile_Refuses_AndLeavesIt()
    {
        var json = "{\"schemaVersion\": 1, \"notebooks\": {\"C:\\\\nb\": " + WriteGrant + ", \"c:\\\\NB\": " + WriteGrant + "}}";
        File.WriteAllText(SettingsPath, json);
        var writer = new SettingsWriter(new SettingsStore(SettingsPath, temp.Combine("journal")), clock);

        var ex = Assert.Throws<OneNoteException>(() => writer.SaveWriteGrant(new NotebookGrant(@"C:\nb", "A", "{NB}", true, false, DateTimeOffset.UnixEpoch, "cli")));

        Assert.Equal(ErrorCode.SettingsInvalid, ex.Code);
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Writer_AnExactDuplicateSectionKey_IsRevoked_BothEntriesGone_AndTheFileIsValid()
    {
        // Story 2.6 (2.3 plan Q7): the revoke clears an exact duplicate, which story 2.1 pinned as a gap.
        File.WriteAllText(SettingsPath, TwoIdenticalGrants);
        var writer = new SettingsWriter(new SettingsStore(SettingsPath, temp.Combine("journal")), clock);

        Assert.Equal(2, writer.RevokeReadGrant(@"c:\NB\vault.one"));

        var read = new SettingsStore(SettingsPath, temp.Combine("journal")).Read();
        Assert.Equal(SettingsStatus.Ok, read.Status);
        Assert.Empty(read.ProtectedSectionEntries);
    }

    [Theory]
    [InlineData(@"C:\\nb\\A", @"C:\\nb\\A")] // exact
    [InlineData(@"C:\\nb\\A", @"c:\\NB\\a")] // case only
    public void Writer_AnEqualNotebookKey_IsRevoked_BothEntriesGone_AndTheFileIsValid(string first, string second)
    {
        File.WriteAllText(SettingsPath, "{\"schemaVersion\": 1, \"notebooks\": {\"" + first + "\": " + WriteGrant + ", \"" + second + "\": " + WriteGrant + ", \"C:\\\\nb\\\\B\": " + WriteGrant + "}}");
        var writer = new SettingsWriter(new SettingsStore(SettingsPath, temp.Combine("journal")), clock);

        Assert.Equal(2, writer.RevokeWriteGrant(@"C:\nb\A"));

        var read = new SettingsStore(SettingsPath, temp.Combine("journal")).Read();
        Assert.Equal(SettingsStatus.Ok, read.Status);
        Assert.Equal([@"C:\nb\B"], read.Notebooks.Keys);
    }

    [Fact]
    public void Writer_Revoke_KeepsEverythingElse_DuplicatesInTheOtherAreaAndUnknownMembersIncluded()
    {
        // Duplicates in both areas: revoking the notebook leaves the section duplicate (and the file invalid for it), so
        // the user can revoke that next; then the file is valid. Members the writer doesn't know survive both.
        File.WriteAllText(
            SettingsPath,
            """{"schemaVersion": 1, "future": {"x": [1, 2]}, "notebooks": {"C:\\nb": @W@, "C:\\nb": @W@}, "protectedSections": {"C:\\nb\\Vault.one": @G@, "C:\\nb\\Vault.one": @G@}}"""
                .Replace("@W@", WriteGrant, StringComparison.Ordinal)
                .Replace("@G@", Grant, StringComparison.Ordinal));
        var writer = new SettingsWriter(new SettingsStore(SettingsPath, temp.Combine("journal")), clock);

        Assert.Equal(2, writer.RevokeWriteGrant(@"C:\nb"));
        var middle = new SettingsStore(SettingsPath, temp.Combine("journal")).Read();
        Assert.True(middle.DuplicateSectionKeys);
        Assert.False(middle.DuplicateNotebookKeys);
        Assert.Equal(2, middle.ProtectedSectionEntries.Count);

        Assert.Equal(2, writer.RevokeReadGrant(@"C:\nb\Vault.one"));
        var read = new SettingsStore(SettingsPath, temp.Combine("journal")).Read();
        Assert.Equal(SettingsStatus.Ok, read.Status);
        Assert.Empty(read.NotebookEntries);
        Assert.Empty(read.ProtectedSectionEntries);
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal("[1,2]", document.RootElement.GetProperty("future").GetProperty("x").GetRawText().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void Writer_RevokeWriteGrant_TakesThePermanentDeleteGrantWithIt()
    {
        // Story 5.6 plan Q2 b: revoking writes revokes permanent deletes too; no entry is left behind.
        var both = WriteGrant.Replace("\"permanentDelete\": false", "\"permanentDelete\": true", StringComparison.Ordinal);
        File.WriteAllText(SettingsPath, "{\"schemaVersion\": 1, \"notebooks\": {\"C:\\\\nb\\\\A\": " + both + ", \"C:\\\\nb\\\\A\": " + both + ", \"c:\\\\NB\\\\a\": " + WriteGrant + "}}");
        var writer = new SettingsWriter(new SettingsStore(SettingsPath, temp.Combine("journal")), clock);

        Assert.Equal(3, writer.RevokeWriteGrant(@"C:\nb\A"));

        var read = new SettingsStore(SettingsPath, temp.Combine("journal")).Read();
        Assert.Equal(SettingsStatus.Ok, read.Status);
        Assert.Empty(read.NotebookEntries);
    }

    [Fact]
    public void Writer_Revoke_WithNoMatch_LeavesTheFileByteForByte()
    {
        var json = TwoIdenticalGrants;
        File.WriteAllText(SettingsPath, json);
        var writer = new SettingsWriter(new SettingsStore(SettingsPath, temp.Combine("journal")), clock);

        Assert.Equal(0, writer.RevokeReadGrant(@"C:\nb\Other.one"));

        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Writer_Revoke_OnAFileInvalidForAnotherReason_IsRefused_AndLeavesIt()
    {
        var json = """{"schemaVersion": 1, "journal": {"retentionDays": 0}, "notebooks": {"C:\\nb": @W@}}""".Replace("@W@", WriteGrant, StringComparison.Ordinal);
        File.WriteAllText(SettingsPath, json);
        var writer = new SettingsWriter(new SettingsStore(SettingsPath, temp.Combine("journal")), clock);

        Assert.Equal(ErrorCode.SettingsInvalid, Assert.Throws<OneNoteException>(() => writer.RevokeWriteGrant(@"C:\nb")).Code);

        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }
}
