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
