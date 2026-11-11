using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 2.1 (D4): exact duplicate keys in <c>settings.json</c>, as the store and the writer handle them today. The store
/// parses with duplicates allowed and the writer without, on purpose (<c>SettingsJson.Options</c>). These pin that
/// difference, including its known gap (an exact duplicate grant can't be revoked), so a fix changes them deliberately.
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

        var snapshot = new SettingsStore(SettingsPath).Read();

        Assert.Equal(SettingsStatus.Ok, snapshot.Status);
        Assert.Equal(20, snapshot.ReadGrants.OnceMinutes);
    }

    [Fact]
    public void Store_AnExactDuplicateGrantKey_CountsLikeACaseOnlyDuplicate()
    {
        File.WriteAllText(SettingsPath, TwoIdenticalGrants);

        var snapshot = new SettingsStore(SettingsPath).Read();

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

        var snapshot = new SettingsStore(SettingsPath).Read();

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

        var snapshot = new SettingsStore(SettingsPath).Read();

        Assert.Equal("notebooks and protectedSections: two keys differ only in case", snapshot.Reason);
        Assert.True(snapshot.DuplicateNotebookKeys && snapshot.DuplicateSectionKeys && snapshot.DuplicateGrantKeysOnly);
    }

    [Fact]
    public void Writer_SaveWriteGrant_OnADuplicateKeyFile_Refuses_AndLeavesIt()
    {
        var json = "{\"schemaVersion\": 1, \"notebooks\": {\"C:\\\\nb\": " + WriteGrant + ", \"c:\\\\NB\": " + WriteGrant + "}}";
        File.WriteAllText(SettingsPath, json);
        var writer = new SettingsWriter(new SettingsStore(SettingsPath), clock);

        var ex = Assert.Throws<OneNoteException>(() => writer.SaveWriteGrant(new NotebookGrant(@"C:\nb", "A", "{NB}", true, false, DateTimeOffset.UnixEpoch, "cli")));

        Assert.Equal(ErrorCode.SettingsInvalid, ex.Code);
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Writer_AnExactDuplicateGrantKey_RefusesTheRevoke_AndLeavesTheFile()
    {
        var json = TwoIdenticalGrants;
        File.WriteAllText(SettingsPath, json);
        var writer = new SettingsWriter(new SettingsStore(SettingsPath), clock);

        var ex = Assert.Throws<OneNoteException>(() => writer.RevokeReadGrant(@"C:\nb\Vault.one"));

        Assert.Equal(ErrorCode.SettingsInvalid, ex.Code);
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }
}
