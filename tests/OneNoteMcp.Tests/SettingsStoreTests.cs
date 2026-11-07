using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>AD-10 read side: the I/O matrix of story 1.4, from Absent to Unreadable, plus one case per validated field.</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string SettingsPath => _dir.Combine("settings.json");

    private SettingsSnapshot ReadWith(string json)
    {
        File.WriteAllText(SettingsPath, json);
        return new SettingsStore(SettingsPath).Read();
    }

    private static void AssertDefaults(SettingsSnapshot s)
    {
        Assert.False(s.AllowSilentActivation);
        Assert.Equal(Timeouts.Default, s.Timeouts);
        Assert.Equal(JournalSettings.Default(), s.Journal);
        Assert.Equal(ResultSettings.Default, s.Results);
        Assert.Equal(ExportRoots.Default().Paths, s.ExportRoots.Paths);
    }

    [Fact]
    public void ResultDefaults_MatchTheSpine()
    {
        // AD-10/AD-20: inlineMaxChars 100,000; ttlMinutes 30; maxStoreMB 256.
        Assert.Equal(new ResultSettings(100_000, 30, 256), ResultSettings.Default);
        Assert.Equal(TimeSpan.FromMinutes(30), ResultSettings.Default.Ttl);
        Assert.Equal(256L * 1024 * 1024, ResultSettings.Default.MaxStoreBytes);
    }

    [Theory]
    [InlineData(20_000, 1, 16)]
    [InlineData(380_000, 1_440, 4_096)]
    [InlineData(150_000, 60, 512)]
    public void Read_Results_AcceptsEveryValueInRange(int inlineMaxChars, int ttlMinutes, int maxStoreMB)
    {
        var s = ReadWith($$$"""{"schemaVersion": 1, "results": {"inlineMaxChars": {{{inlineMaxChars}}}, "ttlMinutes": {{{ttlMinutes}}}, "maxStoreMB": {{{maxStoreMB}}}}}""");

        Assert.Equal(SettingsStatus.Ok, s.Status);
        Assert.Equal(new ResultSettings(inlineMaxChars, ttlMinutes, maxStoreMB), s.Results);
    }

    [Fact]
    public void Read_Results_MissingMembersKeepTheirDefaults()
    {
        var s = ReadWith("""{"schemaVersion": 1, "results": {"ttlMinutes": 5}}""");

        Assert.Equal(SettingsStatus.Ok, s.Status);
        Assert.Equal(ResultSettings.Default with { TtlMinutes = 5 }, s.Results);
    }

    [Fact]
    public void Defaults_MatchTheSpine()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        Assert.Equal(TimeSpan.FromSeconds(30), Timeouts.Default.Call);
        Assert.Equal(TimeSpan.FromSeconds(300), Timeouts.Default.Publish);
        Assert.Equal(TimeSpan.FromSeconds(10), Timeouts.Default.Activation);
        Assert.Equal(TimeSpan.FromSeconds(300), Timeouts.Default.IdleRelease);
        Assert.Equal(new JournalSettings(Path.Combine(local, "OneNoteMcp", "journal"), 30, 2048), JournalSettings.Default());
        Assert.Equal([Path.Combine(documents, "OneNote Exports")], ExportRoots.Default().Paths);
    }

    [Fact]
    public void Read_FileAbsent_ReturnsDefaultsWithStatusAbsent()
    {
        var s = new SettingsStore(SettingsPath).Read();

        Assert.Equal(SettingsStatus.Absent, s.Status);
        Assert.Null(s.Reason);
        Assert.False(s.ReadOnly);
        AssertDefaults(s);
        Assert.False(File.Exists(SettingsPath)); // the store never writes the file
    }

    [Fact]
    public void Read_ValidFileWithSomeFields_UsesThemAndDefaultsTheRest()
    {
        var s = ReadWith("""{"schemaVersion": 1, "allowSilentActivation": true, "timeouts": {"callSeconds": 45}}""");

        Assert.Equal(SettingsStatus.Ok, s.Status);
        Assert.Null(s.Reason);
        Assert.True(s.AllowSilentActivation);
        Assert.Equal(TimeSpan.FromSeconds(45), s.Timeouts.Call);
        Assert.Equal(Timeouts.Default.Publish, s.Timeouts.Publish);
        Assert.Equal(Timeouts.Default.Activation, s.Timeouts.Activation);
        Assert.Equal(Timeouts.Default.IdleRelease, s.Timeouts.IdleRelease);
        Assert.Equal(JournalSettings.Default(), s.Journal);
        Assert.Equal(ExportRoots.Default().Paths, s.ExportRoots.Paths);
    }

    [Fact]
    public void Read_ValidFileWithEveryField_ReadsEveryField()
    {
        var s = ReadWith("""
            {
              "schemaVersion": 1,
              "allowSilentActivation": false,
              "timeouts": {"callSeconds": 1, "publishSeconds": 2, "activationSeconds": 3, "idleReleaseSeconds": 4},
              "journal": {"path": "D:\\journal", "retentionDays": 7, "maxSizeMB": 100},
              "exportRoots": ["D:\\exports", "\\\\server\\share\\out"],
              "notebooks": {}
            }
            """);

        Assert.Equal(SettingsStatus.Ok, s.Status);
        Assert.Equal(new Timeouts(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4)), s.Timeouts);
        Assert.Equal(new JournalSettings(@"D:\journal", 7, 100), s.Journal);
        Assert.Equal([@"D:\exports", @"\\server\share\out"], s.ExportRoots.Paths);
    }

    [Fact]
    public void Read_EmptyExportRoots_IsValid()
    {
        var s = ReadWith("""{"schemaVersion": 1, "exportRoots": []}""");

        Assert.Equal(SettingsStatus.Ok, s.Status);
        Assert.Empty(s.ExportRoots.Paths);
    }

    [Fact]
    public void Read_UnknownKeys_AreIgnored()
    {
        var s = ReadWith("""{"schemaVersion": 1, "futureThing": {"x": [1, 2]}, "timeouts": {"callSeconds": 5, "other": "y"}}""");

        Assert.Equal(SettingsStatus.Ok, s.Status);
        Assert.Equal(TimeSpan.FromSeconds(5), s.Timeouts.Call);
    }

    [Fact]
    public void Read_NotJson_IsInvalidWithoutQuotingTheContent()
    {
        var s = ReadWith("{\"schemaVersion\": 1, secretWord");

        Assert.Equal(SettingsStatus.Invalid, s.Status);
        Assert.StartsWith("settings.json: not valid JSON", s.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("secretWord", s.Reason, StringComparison.Ordinal);
        AssertDefaults(s);
    }

    [Fact]
    public void Read_RootNotAnObject_IsInvalid()
    {
        var s = ReadWith("[1]");

        Assert.Equal(SettingsStatus.Invalid, s.Status);
        Assert.StartsWith("(root):", s.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_MissingSchemaVersion_IsInvalid()
    {
        var s = ReadWith("""{"allowSilentActivation": true}""");

        Assert.Equal(SettingsStatus.Invalid, s.Status);
        Assert.StartsWith("schemaVersion:", s.Reason, StringComparison.Ordinal);
        AssertDefaults(s); // an invalid file's valid-looking fields are not applied
    }

    [Fact]
    public void Read_NewerSchema_IsReadOnlyOnDefaults()
    {
        var s = ReadWith("""{"schemaVersion": 2, "allowSilentActivation": true, "timeouts": "whatever"}""");

        Assert.Equal(SettingsStatus.NewerSchema, s.Status);
        Assert.True(s.ReadOnly);
        AssertDefaults(s);
    }

    [Fact]
    public void Read_SchemaVersionBeyondInt32_IsNewerSchema()
    {
        var s = ReadWith("""{"schemaVersion": 99999999999}""");

        Assert.Equal(SettingsStatus.NewerSchema, s.Status);
    }

    [Fact]
    public void Read_TimeoutAtTheUpperBound_IsValid()
    {
        var s = ReadWith("""{"schemaVersion": 1, "timeouts": {"callSeconds": 2147483}}""");

        Assert.Equal(SettingsStatus.Ok, s.Status);
        Assert.Equal(TimeSpan.FromSeconds(2147483), s.Timeouts.Call);
    }

    [Fact]
    public void Read_FileLockedByAnotherWriter_IsInvalid()
    {
        File.WriteAllText(SettingsPath, """{"schemaVersion": 1}""");
        using var exclusive = new FileStream(SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var s = new SettingsStore(SettingsPath).Read();

        Assert.Equal(SettingsStatus.Invalid, s.Status);
        Assert.StartsWith("settings.json: could not be read", s.Reason, StringComparison.Ordinal);
        AssertDefaults(s);
    }

    [Fact]
    public void Read_PathIsAFolder_IsInvalid()
    {
        Directory.CreateDirectory(SettingsPath);

        var s = new SettingsStore(SettingsPath).Read();

        Assert.Equal(SettingsStatus.Invalid, s.Status);
        Assert.StartsWith("settings.json: could not be read", s.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_AccessDenied_IsInvalid()
    {
        File.WriteAllText(SettingsPath, """{"schemaVersion": 1}""");
        var file = new FileInfo(SettingsPath);
        var acl = file.GetAccessControl();
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(
            me, System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny);
        acl.AddAccessRule(deny);
        file.SetAccessControl(acl);
        try
        {
            var s = new SettingsStore(SettingsPath).Read();

            Assert.Equal(SettingsStatus.Invalid, s.Status);
            Assert.Equal("settings.json: could not be read (UnauthorizedAccessException)", s.Reason);
            AssertDefaults(s);
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            file.SetAccessControl(acl);
        }
    }

    [Fact]
    public void Read_EachCallReadsTheFileAgain()
    {
        var store = new SettingsStore(SettingsPath);
        File.WriteAllText(SettingsPath, """{"schemaVersion": 1, "allowSilentActivation": false}""");
        var first = store.Read();

        File.WriteAllText(SettingsPath, """{"schemaVersion": 1, "allowSilentActivation": true}""");
        var second = store.Read();

        Assert.False(first.AllowSilentActivation);
        Assert.True(second.AllowSilentActivation);
        Assert.NotSame(first, second);
    }

    [Theory]
    [InlineData("""{"schemaVersion": "1"}""", "schemaVersion")]
    [InlineData("""{"schemaVersion": 1.5}""", "schemaVersion")]
    [InlineData("""{"schemaVersion": 0}""", "schemaVersion")]
    [InlineData("""{"schemaVersion": 1, "allowSilentActivation": "yes"}""", "allowSilentActivation")]
    [InlineData("""{"schemaVersion": 1, "allowSilentActivation": null}""", "allowSilentActivation")]
    [InlineData("""{"schemaVersion": 1, "timeouts": 30}""", "timeouts")]
    [InlineData("""{"schemaVersion": 1, "timeouts": {"callSeconds": 0}}""", "timeouts.callSeconds")]
    [InlineData("""{"schemaVersion": 1, "timeouts": {"callSeconds": "30"}}""", "timeouts.callSeconds")]
    [InlineData("""{"schemaVersion": 1, "timeouts": {"publishSeconds": -1}}""", "timeouts.publishSeconds")]
    [InlineData("""{"schemaVersion": 1, "timeouts": {"activationSeconds": 0}}""", "timeouts.activationSeconds")]
    [InlineData("""{"schemaVersion": 1, "timeouts": {"idleReleaseSeconds": 2.5}}""", "timeouts.idleReleaseSeconds")]
    [InlineData("""{"schemaVersion": 1, "timeouts": {"callSeconds": 2147484}}""", "timeouts.callSeconds")]
    [InlineData("""{"schemaVersion": 1, "journal": []}""", "journal")]
    [InlineData("""{"schemaVersion": 1, "journal": {"path": 3}}""", "journal.path")]
    [InlineData("""{"schemaVersion": 1, "journal": {"path": "relative\\journal"}}""", "journal.path")]
    [InlineData("""{"schemaVersion": 1, "journal": {"retentionDays": 0}}""", "journal.retentionDays")]
    [InlineData("""{"schemaVersion": 1, "journal": {"maxSizeMB": 0}}""", "journal.maxSizeMB")]
    [InlineData("""{"schemaVersion": 1, "journal": {"maxSizeMB": 99999999999}}""", "journal.maxSizeMB")]
    [InlineData("""{"schemaVersion": 1, "results": 100000}""", "results")]
    [InlineData("""{"schemaVersion": 1, "results": {"inlineMaxChars": 10}}""", "results.inlineMaxChars")]
    [InlineData("""{"schemaVersion": 1, "results": {"inlineMaxChars": 19999}}""", "results.inlineMaxChars")]
    [InlineData("""{"schemaVersion": 1, "results": {"inlineMaxChars": 380001}}""", "results.inlineMaxChars")]
    [InlineData("""{"schemaVersion": 1, "results": {"inlineMaxChars": 50000.5}}""", "results.inlineMaxChars")]
    [InlineData("""{"schemaVersion": 1, "results": {"inlineMaxChars": "100000"}}""", "results.inlineMaxChars")]
    [InlineData("""{"schemaVersion": 1, "results": {"ttlMinutes": 0}}""", "results.ttlMinutes")]
    [InlineData("""{"schemaVersion": 1, "results": {"ttlMinutes": 1441}}""", "results.ttlMinutes")]
    [InlineData("""{"schemaVersion": 1, "results": {"maxStoreMB": 15}}""", "results.maxStoreMB")]
    [InlineData("""{"schemaVersion": 1, "results": {"maxStoreMB": 4097}}""", "results.maxStoreMB")]
    [InlineData("""{"schemaVersion": 1, "exportRoots": "D:\\exports"}""", "exportRoots")]
    [InlineData("""{"schemaVersion": 1, "exportRoots": ["D:\\ok", 5]}""", "exportRoots[1]")]
    [InlineData("""{"schemaVersion": 1, "exportRoots": ["C:relative"]}""", "exportRoots[0]")]
    [InlineData("""{"schemaVersion": 1, "exportRoots": [""]}""", "exportRoots[0]")]
    public void Read_InvalidField_IsInvalidAndTheReasonNamesTheField(string json, string field)
    {
        var s = ReadWith(json);

        Assert.Equal(SettingsStatus.Invalid, s.Status);
        Assert.StartsWith(field + ":", s.Reason, StringComparison.Ordinal);
        AssertDefaults(s);
    }

    [Fact]
    public void Read_InvalidPath_ReasonNeverQuotesTheValue()
    {
        var s = ReadWith("""{"schemaVersion": 1, "journal": {"path": "secret-folder-name"}}""");

        Assert.Equal(SettingsStatus.Invalid, s.Status);
        Assert.DoesNotContain("secret", s.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureWritable_OkOrAbsent_DoesNotThrow()
    {
        new SettingsStore(SettingsPath).Read().EnsureWritable();
        ReadWith("""{"schemaVersion": 1}""").EnsureWritable();
    }

    [Fact]
    public void EnsureWritable_Invalid_ThrowsSettingsInvalidNamingTheField()
    {
        var s = ReadWith("""{"schemaVersion": 1, "journal": {"retentionDays": 0}}""");

        var ex = Assert.Throws<OneNoteException>(s.EnsureWritable);
        Assert.Equal(ErrorCode.SettingsInvalid, ex.Code);
        Assert.Contains("journal.retentionDays", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureWritable_NewerSchema_ThrowsSettingsInvalidSayingNewerVersion()
    {
        var s = ReadWith("""{"schemaVersion": 2}""");

        var ex = Assert.Throws<OneNoteException>(s.EnsureWritable);
        Assert.Equal(ErrorCode.SettingsInvalid, ex.Code);
        Assert.Contains("newer version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvePath_WithOverride_UsesIt()
    {
        var path = SettingsStore.ResolvePath(name => name == SettingsStore.PathOverrideVariable ? SettingsPath : null);

        Assert.Equal(SettingsPath, path);
    }

    [Fact]
    public void ResolvePath_WithoutOverride_IsUnderAppData()
    {
        var path = SettingsStore.ResolvePath(_ => null);

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OneNoteMcp", "settings.json"),
            path);
    }
}
