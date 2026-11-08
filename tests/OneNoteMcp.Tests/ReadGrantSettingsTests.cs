using System.Text.Json.Nodes;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Settings;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 1.20's settings work (AD-10, AD-21): <c>protectedSections{}</c> as the reader sees it, and
/// <see cref="SettingsWriter"/>'s read-modify-write under <c>Local\OneNoteMcp.Settings</c>.
/// </summary>
public sealed class ReadGrantSettingsTests : IDisposable
{
    private const string Grant = """{"name": "Vault", "notebookKey": "C:\\nb", "idAtGrant": "{S}{1}{B0}", "grantedAt": "2026-11-07T12:00:00.000Z", "grantedVia": "cli"}""";

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();

    private string SettingsPath => temp.Combine("settings.json");

    public void Dispose() => temp.Dispose();

    // --- Reader -------------------------------------------------------------------------------------------------

    [Fact]
    public void Read_ProtectedSections_AreKeyedIgnoringCase_WithTheirFields()
    {
        Write(J("""{"schemaVersion": 1, "protectedSections": {"C:\\nb\\Vault.one": GRANT, "C:\\nb\\B.one": {"name": "B", "notebookKey": "", "idAtGrant": "x", "grantedAt": "2026-11-07T12:00:00Z", "grantedVia": "elicitation", "expiresAt": "2026-11-07T20:00:00Z"}}}"""));

        var snapshot = new SettingsStore(SettingsPath).Read();

        Assert.Equal(SettingsStatus.Ok, snapshot.Status);
        var vault = snapshot.ProtectedSections[@"c:\NB\vault.ONE"];
        Assert.Equal(("Vault", @"C:\nb", "{S}{1}{B0}", "cli", ReadGrantLifetimes.UntilRevoked), (vault.Name, vault.NotebookKey, vault.IdAtGrant, vault.GrantedVia, vault.Lifetime));
        var b = snapshot.ProtectedSections[@"C:\nb\B.one"];
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 20, 0, 0, TimeSpan.Zero), b.ExpiresAt);
        Assert.Equal(ReadGrantLifetimes.Hours, b.Lifetime);
    }

    [Fact]
    public void Read_KeysDifferingOnlyInCase_AreInvalid_GrantsCountAsAbsent_ButTheEntriesAreKeptForRevoke()
    {
        Write(J("""{"schemaVersion": 1, "protectedSections": {"C:\\nb\\Vault.one": GRANT, "c:\\NB\\vault.one": GRANT}}"""));

        var snapshot = new SettingsStore(SettingsPath).Read();

        Assert.Equal(SettingsStatus.Invalid, snapshot.Status);
        Assert.True(snapshot.DuplicateGrantKeysOnly);
        Assert.Empty(snapshot.ProtectedSections);
        Assert.Equal(2, snapshot.ProtectedSectionEntries.Count);
        Assert.DoesNotContain("Vault", snapshot.Reason!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"schemaVersion": 1, "protectedSections": []}""", "protectedSections")]
    [InlineData("""{"schemaVersion": 1, "protectedSections": {"k": 3}}""", "protectedSections")]
    [InlineData("""{"schemaVersion": 1, "protectedSections": {"k": {"name": "n", "notebookKey": "", "idAtGrant": "i", "grantedAt": "2026-11-07T12:00:00Z"}}}""", "grantedVia")]
    [InlineData("""{"schemaVersion": 1, "protectedSections": {"k": {"name": "n", "notebookKey": "", "idAtGrant": "i", "grantedAt": "2026-11-07T12:00:00Z", "grantedVia": "tool"}}}""", "grantedVia")]
    [InlineData("""{"schemaVersion": 1, "protectedSections": {"k": {"name": "n", "notebookKey": "", "idAtGrant": "i", "grantedAt": "yesterday", "grantedVia": "cli"}}}""", "grantedAt")]
    [InlineData("""{"schemaVersion": 1, "protectedSections": {"k": {"name": "n", "notebookKey": "", "idAtGrant": "i", "grantedAt": "2026-11-07T12:00:00Z", "grantedVia": "cli", "expiresAt": 5}}}""", "expiresAt")]
    [InlineData("""{"schemaVersion": 1, "protectedSections": {"k": {"notebookKey": "", "idAtGrant": "i", "grantedAt": "2026-11-07T12:00:00Z", "grantedVia": "cli"}}}""", "name")]
    [InlineData("""{"schemaVersion": 1, "protectedSections": {"": {"name": "n", "notebookKey": "", "idAtGrant": "i", "grantedAt": "2026-11-07T12:00:00Z", "grantedVia": "cli"}}}""", "empty")]
    public void Read_ABadEntry_IsInvalid_NamingTheField_NotTheValue(string json, string field)
    {
        Write(json);

        var snapshot = new SettingsStore(SettingsPath).Read();

        Assert.Equal(SettingsStatus.Invalid, snapshot.Status);
        Assert.False(snapshot.DuplicateGrantKeysOnly);
        Assert.Contains(field, snapshot.Reason!, StringComparison.Ordinal);
        Assert.Empty(snapshot.ProtectedSectionEntries);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData("""{"onceMinutes": 1}""", 1)]
    [InlineData("""{"onceMinutes": 60}""", 60)]
    [InlineData("""{}""", 5)]
    public void Read_ReadGrantsOnceMinutes_InRange(string? readGrants, int expected)
    {
        Write(readGrants is null ? """{"schemaVersion": 1}""" : $$"""{"schemaVersion": 1, "readGrants": {{readGrants}}}""");

        var snapshot = new SettingsStore(SettingsPath).Read();

        Assert.Equal(SettingsStatus.Ok, snapshot.Status);
        Assert.Equal(expected, snapshot.ReadGrants.OnceMinutes);
    }

    [Theory]
    [InlineData("""{"onceMinutes": 0}""")]
    [InlineData("""{"onceMinutes": 61}""")]
    [InlineData("""{"onceMinutes": 2.5}""")]
    [InlineData("""{"onceMinutes": "5"}""")]
    [InlineData("""5""")]
    public void Read_ReadGrantsOnceMinutes_OutOfRange_IsInvalid_DefaultsApply(string readGrants)
    {
        Write($$"""{"schemaVersion": 1, "readGrants": {{readGrants}}}""");

        var snapshot = new SettingsStore(SettingsPath).Read();

        Assert.Equal(SettingsStatus.Invalid, snapshot.Status);
        Assert.Contains("readGrants", snapshot.Reason!, StringComparison.Ordinal);
        Assert.Equal(5, snapshot.ReadGrants.OnceMinutes);
    }

    [Fact]
    public void Read_NewerSchema_HasNoGrants()
    {
        Write(J("""{"schemaVersion": 2, "protectedSections": {"C:\\nb\\Vault.one": GRANT}}"""));

        Assert.Empty(new SettingsStore(SettingsPath).Read().ProtectedSections);
    }

    [Fact]
    public void GrantKeys_TrimTrailingSeparatorsOnly()
    {
        Assert.Equal(@"C:\a\b.one", GrantKeys.SectionKey(@"C:\a\b.one\/"));
        Assert.Equal("https://d.docs.live.net/x/Notebook", GrantKeys.NotebookKey("https://d.docs.live.net/x/Notebook/"));
        Assert.Equal("https://x/a%20b", GrantKeys.SectionKey("https://x/a%20b"));
        Assert.True(GrantKeys.Comparer.Equals(@"C:\A.one", @"c:\a.ONE"));
    }

    [Theory]
    [InlineData(" vault ", "Vault", true)]
    [InlineData("VAULT", " Vault ", true)]
    [InlineData("Vault2", "Vault", false)]
    [InlineData("", "", false)]
    [InlineData(null, "Vault", false)]
    public void NameMatches_IsTheOneMatcher_TrimmedIgnoringCase(string? typed, string name, bool matches) =>
        Assert.Equal(matches, GrantKeys.NameMatches(typed, name));

    [Theory]
    [InlineData(@"C:\x\OneNoteMcp.exe", "\"C:\\x\\OneNoteMcp.exe\"")]
    [InlineData(@"C:\x\onenotemcp.EXE", "\"C:\\x\\onenotemcp.EXE\"")]
    [InlineData(@"C:\x\testhost.exe", "OneNoteMcp")]
    [InlineData(null, "OneNoteMcp")]
    public void GrantCli_NamesTheRunningExeByItsQuotedPath_ElseTheBareName(string? processPath, string expected)
    {
        Assert.Equal(expected, GrantCli.Resolve(processPath));
        Assert.Contains("in PowerShell", GrantCli.Run("--read-section x", GrantCli.Resolve(@"C:\x\OneNoteMcp.exe")), StringComparison.Ordinal);
        Assert.Equal("`OneNoteMcp grant --read-section x` in a terminal", GrantCli.Run("--read-section x", "OneNoteMcp"));
    }

    // --- Writer -------------------------------------------------------------------------------------------------

    [Fact]
    public void Save_WithNoFile_CreatesSchemaVersion1_WithTheGrant_InUtc()
    {
        var writer = new SettingsWriter(new SettingsStore(SettingsPath), clock);
        var expires = new DateTimeOffset(2026, 11, 7, 20, 0, 0, TimeSpan.FromHours(-5));

        writer.SaveReadGrant(new ProtectedSectionGrant(@"C:\nb\Vault.one", "Vault", @"C:\nb", "{S}{1}{B0}", clock.GetUtcNow(), GrantedVia.Cli, expires));

        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal(1, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal("2026-11-08T01:00:00.000Z", json["protectedSections"]![@"C:\nb\Vault.one"]!["expiresAt"]!.GetValue<string>());
        var read = new SettingsStore(SettingsPath).Read();
        Assert.Equal(SettingsStatus.Ok, read.Status);
        Assert.Equal(expires, read.ProtectedSections[@"C:\nb\Vault.one"].ExpiresAt);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void Save_KeepsEveryOtherMember_AndReplacesAKeyDifferingOnlyInCase()
    {
        Write(J("""{"schemaVersion": 1, "results": {"ttlMinutes": 5}, "futureThing": {"a": [1, 2]}, "protectedSections": {"c:\\nb\\vault.one": GRANT}}"""));
        var writer = new SettingsWriter(new SettingsStore(SettingsPath), clock);

        writer.SaveReadGrant(new ProtectedSectionGrant(@"C:\nb\Vault.one", "Vault", @"C:\nb", "{S2}{1}{B0}", clock.GetUtcNow(), GrantedVia.Elicitation));

        var json = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal(5, json["results"]!["ttlMinutes"]!.GetValue<int>());
        Assert.Equal(2, json["futureThing"]!["a"]!.AsArray().Count);
        var sections = json["protectedSections"]!.AsObject();
        Assert.Equal([@"C:\nb\Vault.one"], sections.Select(p => p.Key));
        Assert.Equal("{S2}{1}{B0}", sections[@"C:\nb\Vault.one"]!["idAtGrant"]!.GetValue<string>());
        Assert.False(sections[@"C:\nb\Vault.one"]!.AsObject().ContainsKey("expiresAt"));
    }

    [Fact]
    public void AWrite_DropsExpiredEntries_ButNeverUnexpiredOnes()
    {
        Write(J("""
            {"schemaVersion": 1, "protectedSections": {
              "C:\\nb\\Old.one": {"name": "Old", "notebookKey": "", "idAtGrant": "o", "grantedAt": "2026-11-01T00:00:00Z", "grantedVia": "cli", "expiresAt": "2026-11-06T11:59:59Z"},
              "C:\\nb\\Live.one": {"name": "Live", "notebookKey": "", "idAtGrant": "l", "grantedAt": "2026-11-01T00:00:00Z", "grantedVia": "cli", "expiresAt": "2026-11-06T12:00:01Z"},
              "C:\\nb\\Forever.one": GRANT}}
            """));
        var writer = new SettingsWriter(new SettingsStore(SettingsPath), clock);

        writer.SaveReadGrant(new ProtectedSectionGrant(@"C:\nb\New.one", "New", "", "n", clock.GetUtcNow(), GrantedVia.Cli));

        Assert.Equal(
            [@"C:\nb\Forever.one", @"C:\nb\Live.one", @"C:\nb\New.one"],
            new SettingsStore(SettingsPath).Read().ProtectedSections.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Revoke_RemovesEveryCaseVariant_EvenFromAFileInvalidOnlyForThem()
    {
        Write(J("""{"schemaVersion": 1, "protectedSections": {"C:\\nb\\Vault.one": GRANT, "c:\\NB\\vault.one": GRANT, "C:\\nb\\Other.one": GRANT}}"""));
        var writer = new SettingsWriter(new SettingsStore(SettingsPath), clock);

        Assert.Equal(2, writer.RevokeReadGrant(@"C:\NB\VAULT.ONE"));

        var read = new SettingsStore(SettingsPath).Read();
        Assert.Equal(SettingsStatus.Ok, read.Status);
        Assert.Equal([@"C:\nb\Other.one"], read.ProtectedSections.Keys);
    }

    [Fact]
    public void Save_OnAFileInvalidForDuplicateKeys_IsRefused()
    {
        Write(J("""{"schemaVersion": 1, "protectedSections": {"A": GRANT, "a": GRANT}}"""));
        var before = File.ReadAllText(SettingsPath);
        var writer = new SettingsWriter(new SettingsStore(SettingsPath), clock);

        var ex = Assert.Throws<OneNoteException>(() => writer.SaveReadGrant(new ProtectedSectionGrant("B", "B", "", "b", clock.GetUtcNow(), GrantedVia.Cli)));

        Assert.Equal(ErrorCode.SettingsInvalid, ex.Code);
        Assert.Equal(before, File.ReadAllText(SettingsPath));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"schemaVersion": 2}""")]
    [InlineData("""{"schemaVersion": 1, "timeouts": "x"}""")]
    public void Writes_OnAnInvalidOrNewerFile_AreRefused_AndTheFileIsUntouched(string json)
    {
        Write(json);
        var writer = new SettingsWriter(new SettingsStore(SettingsPath), clock);

        Assert.Equal(ErrorCode.SettingsInvalid, Assert.Throws<OneNoteException>(() => writer.SaveReadGrant(new ProtectedSectionGrant("B", "B", "", "b", clock.GetUtcNow(), GrantedVia.Cli))).Code);
        Assert.Equal(ErrorCode.SettingsInvalid, Assert.Throws<OneNoteException>(() => writer.RevokeReadGrant("B")).Code);
        Assert.Equal(json, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Revoke_OfAnAbsentKey_LeavesTheFileAlone()
    {
        Write("""{"schemaVersion": 1}""");
        var writer = new SettingsWriter(new SettingsStore(SettingsPath), clock);

        Assert.Equal(0, writer.RevokeReadGrant("nothing"));
        Assert.Equal("""{"schemaVersion": 1}""", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task ConcurrentWriters_LoseNoUpdate()
    {
        var store = new SettingsStore(SettingsPath);
        var writers = Enumerable.Range(0, 8).Select(i => Task.Run(
            () => new SettingsWriter(store, clock).SaveReadGrant(new ProtectedSectionGrant($@"C:\nb\S{i}.one", $"S{i}", "", "x", clock.GetUtcNow(), GrantedVia.Cli)),
            TestContext.Current.CancellationToken));

        await Task.WhenAll(writers);

        Assert.Equal(8, store.Read().ProtectedSections.Count);
    }

    [Fact]
    public void Writer_UsesTheSpinesMutexName() => Assert.Equal(@"Local\OneNoteMcp.Settings", SettingsWriter.MutexName);

    private void Write(string json) => File.WriteAllText(SettingsPath, json);

    private static string J(string template) => template.Replace("GRANT", Grant, StringComparison.Ordinal);
}
