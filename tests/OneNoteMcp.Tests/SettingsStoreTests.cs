using OneNoteMcp.Core;
using OneNoteMcp.Core.Settings;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>AD-10 read side: the I/O matrix of story 1.4, from Absent to Unreadable, plus one case per validated field.</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly TempDirectory dir = new();

    public void Dispose() => dir.Dispose();

    private string SettingsPath => dir.Combine("settings.json");

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
        Assert.Equal(SearchSettings.Default, s.Search);
        Assert.Equal(ExportRoots.Default().Paths, s.ExportRoots.Paths);
    }

    [Fact]
    public void SearchDefaults_MatchTheSpine()
    {
        // AD-10/AD-12: cacheMaxMB 0-1,024, default 64.
        Assert.Equal(new SearchSettings(64), SearchSettings.Default);
        Assert.Equal(64L * 1024 * 1024, SearchSettings.Default.CacheMaxBytes);
        Assert.Equal(0, SearchSettings.CacheMaxMBMin);
        Assert.Equal(1_024, SearchSettings.CacheMaxMBMax);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(1_024)]
    public void Read_Search_AcceptsEveryValueInRange(int cacheMaxMB)
    {
        var s = ReadWith($$$"""{"schemaVersion": 1, "search": {"cacheMaxMB": {{{cacheMaxMB}}}}}""");

        Assert.Equal(SettingsStatus.Ok, s.Status);
        Assert.Equal(new SearchSettings(cacheMaxMB), s.Search);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1025")]
    [InlineData("1.5")]
    [InlineData("\"64\"")]
    [InlineData("null")]
    public void Read_Search_OutOfRangeOrNotAWholeNumber_IsInvalid_DefaultsUsed(string value)
    {
        var s = ReadWith("{\"schemaVersion\": 1, \"timeouts\": {\"callSeconds\": 5}, \"search\": {\"cacheMaxMB\": " + value + "}}");

        Assert.Equal(SettingsStatus.Invalid, s.Status);
        Assert.Equal("search.cacheMaxMB: must be a whole number from 0 to 1024", s.Reason);
        AssertDefaults(s); // the valid timeouts aren't applied either
        Assert.Throws<OneNoteException>(s.EnsureWritable);
    }

    [Fact]
    public void Read_Search_NotAnObject_IsInvalid()
    {
        var s = ReadWith("""{"schemaVersion": 1, "search": 64}""");

        Assert.Equal(SettingsStatus.Invalid, s.Status);
        Assert.Equal("search: must be an object", s.Reason);
    }

    [Fact]
    public void Read_Search_MissingMemberKeepsItsDefault()
    {
        var s = ReadWith("""{"schemaVersion": 1, "search": {}}""");

        Assert.Equal(SettingsStatus.Ok, s.Status);
        Assert.Equal(SearchSettings.Default, s.Search);
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
        // With no override set (the test process sets one; TestEnvironment).
        Assert.Equal(new JournalSettings(Path.Combine(local, "OneNoteMcp", "journal"), 30, 2048), JournalSettings.Default(OneNoteMcp.Core.Journal.JournalFiles.DefaultFolder(_ => null)));
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
    public void EnsureWritable_NewerSchema_IsNotRetryable_AndSaysToUpdateNotToEdit()
    {
        // Story 2.4 (Q7): a retry in this server can't succeed, and editing the file is the wrong fix.
        var s = ReadWith("""{"schemaVersion": 2}""");

        var ex = Assert.Throws<OneNoteException>(s.EnsureWritable);
        var error = Core.Results.ToolErrors.From(ex, OperationClass.Create);

        Assert.False(error.Retryable);
        Assert.Equal(SettingsSnapshot.NewerSchemaUserAction, error.UserAction);
    }

    [Fact]
    public void EnsureWritable_Invalid_KeepsTheFixTheFileAdvice_AndIsRetryable()
    {
        var s = ReadWith("""{"schemaVersion": 1, "journal": {"retentionDays": 0}}""");

        var error = Core.Results.ToolErrors.From(Assert.Throws<OneNoteException>(s.EnsureWritable), OperationClass.Create);

        Assert.True(error.Retryable);
        Assert.Equal(Core.Results.ToolErrors.UserAction(ErrorCode.SettingsInvalid), error.UserAction);
    }

    // --- notebooks{}: write grants (story 2.4, AD-6) -------------------------------------------------------------------

    private const string WriteGrant = """{"name": "Notebook A", "idAtGrant": "{NB}{1}{B0}", "write": true, "permanentDelete": false, "grantedAt": "2026-11-10T12:00:00.000Z", "grantedVia": "cli"}""";

    [Theory]
    [InlineData("cli")]
    [InlineData("elicitation")]
    [InlineData("created")]
    public void Read_Notebooks_EveryFieldIsRead(string via)
    {
        var s = ReadWith("""{"schemaVersion": 1, "notebooks": {"C:\\nb\\A": ENTRY}}""".Replace("ENTRY", WriteGrant.Replace("\"cli\"", $"\"{via}\"", StringComparison.Ordinal), StringComparison.Ordinal));

        Assert.Equal(SettingsStatus.Ok, s.Status);
        var grant = Assert.Single(s.Notebooks.Values);
        Assert.Equal(new NotebookGrant(@"C:\nb\A", "Notebook A", "{NB}{1}{B0}", true, false, new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero), via), grant);
        Assert.Same(grant, s.Notebooks[@"c:\NB\a"]); // keys compare ignoring case
        Assert.Equal([grant], s.NotebookEntries);
    }

    [Fact]
    public void Read_NoNotebooks_IsOk_AndGrantsNothing()
    {
        var s = ReadWith("""{"schemaVersion": 1}""");

        Assert.Empty(s.Notebooks);
        Assert.Empty(s.NotebookEntries);
    }

    [Theory]
    [InlineData("\"notebooks\": []", "notebooks: must be an object")]
    [InlineData("\"notebooks\": {\"C:\\\\nb\": 1}", "notebooks entry: must be an object")]
    [InlineData("\"notebooks\": {\" \": ENTRY}", "notebooks: a key is empty")]
    public void Read_Notebooks_BadShape_IsInvalid(string member, string reason)
    {
        var s = ReadWith("{\"schemaVersion\": 1, " + member.Replace("ENTRY", WriteGrant, StringComparison.Ordinal) + "}");

        Assert.Equal(SettingsStatus.Invalid, s.Status);
        Assert.Equal(reason, s.Reason);
        Assert.Empty(s.Notebooks);
    }

    [Theory]
    [InlineData("name", "1", "notebooks entry name: must be a string")]
    [InlineData("idAtGrant", "null", "notebooks entry idAtGrant: must be a string")]
    [InlineData("write", "\"true\"", "notebooks entry write: must be true or false")]
    [InlineData("permanentDelete", "0", "notebooks entry permanentDelete: must be true or false")]
    [InlineData("grantedAt", "\"yesterday\"", "notebooks entry grantedAt: must be an ISO-8601 date-time")]
    [InlineData("grantedVia", "\"model\"", "notebooks entry grantedVia: must be elicitation, cli or created")]
    public void Read_Notebooks_BadField_IsInvalid_NamingTheField_NeverTheValue(string field, string value, string reason)
    {
        var entry = System.Text.Json.Nodes.JsonNode.Parse(WriteGrant)!.AsObject();
        entry[field] = System.Text.Json.Nodes.JsonNode.Parse(value);

        var s = ReadWith("""{"schemaVersion": 1, "notebooks": {"C:\\nb\\A": ENTRY}}""".Replace("ENTRY", entry.ToJsonString(), StringComparison.Ordinal));

        Assert.Equal(SettingsStatus.Invalid, s.Status);
        Assert.Equal(reason, s.Reason);
        Assert.Throws<OneNoteException>(s.EnsureWritable);
    }

    [Theory]
    [InlineData("write")]
    [InlineData("permanentDelete")]
    [InlineData("grantedAt")]
    public void Read_Notebooks_MissingField_IsInvalid(string field)
    {
        var entry = System.Text.Json.Nodes.JsonNode.Parse(WriteGrant)!.AsObject();
        entry.Remove(field);

        var s = ReadWith("""{"schemaVersion": 1, "notebooks": {"C:\\nb\\A": ENTRY}}""".Replace("ENTRY", entry.ToJsonString(), StringComparison.Ordinal));

        Assert.Equal(SettingsStatus.Invalid, s.Status);
        Assert.Contains($"notebooks entry {field}", s.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_Notebooks_InANewerFile_CountAsAbsent()
    {
        var s = ReadWith("""{"schemaVersion": 2, "notebooks": {"C:\\nb\\A": ENTRY}}""".Replace("ENTRY", WriteGrant, StringComparison.Ordinal));

        Assert.Equal(SettingsStatus.NewerSchema, s.Status);
        Assert.Empty(s.Notebooks);
    }

    [Fact]
    public void Writer_SaveWriteGrant_OnNoFile_CreatesIt_WithEveryField()
    {
        var store = new SettingsStore(SettingsPath);
        var at = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        new SettingsWriter(store, new FakeClock()).SaveWriteGrant(new NotebookGrant(@"C:\nb\A", "Notebook A", "{NB}", true, false, at, Core.Access.GrantedVia.Cli));

        var s = store.Read();
        Assert.Equal(SettingsStatus.Ok, s.Status);
        Assert.Equal(new NotebookGrant(@"C:\nb\A", "Notebook A", "{NB}", true, false, at, "cli"), Assert.Single(s.Notebooks.Values));
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal(1, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal(
            ["name", "idAtGrant", "write", "permanentDelete", "grantedAt", "grantedVia"],
            json["notebooks"]![@"C:\nb\A"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void Writer_SaveWriteGrant_AgainKeepsPermanentDelete_ReplacesACaseVariantKey_AndKeepsEverythingElse()
    {
        var other = """{"name": "Vault", "notebookKey": "C:\\nb\\A", "idAtGrant": "{S}", "grantedAt": "2026-11-07T12:00:00.000Z", "grantedVia": "cli"}""";
        File.WriteAllText(SettingsPath, ("""{"schemaVersion": 1, "timeouts": {"callSeconds": 7}, "custom": [1, 2], "protectedSections": {"C:\\nb\\A\\Vault.one": OTHER}, """
            + """ "notebooks": {"c:\\NB\\a": {"name": "Old", "idAtGrant": "{OLD}", "write": true, "permanentDelete": true, "grantedAt": "2026-11-01T00:00:00.000Z", "grantedVia": "cli"}}}""")
            .Replace("OTHER", other, StringComparison.Ordinal));
        var store = new SettingsStore(SettingsPath);
        var at = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        new SettingsWriter(store, new FakeClock()).SaveWriteGrant(new NotebookGrant(@"C:\nb\A", "Notebook A", "{NB}", true, false, at, "cli"));

        var s = store.Read();
        Assert.Equal(SettingsStatus.Ok, s.Status);
        var grant = Assert.Single(s.NotebookEntries);
        Assert.Equal(@"C:\nb\A", grant.Key);
        Assert.Equal("{NB}", grant.IdAtGrant);
        Assert.True(grant.PermanentDelete); // kept
        Assert.Single(s.ProtectedSections);
        Assert.Equal(TimeSpan.FromSeconds(7), s.Timeouts.Call);
        Assert.Equal("[1,2]", System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsPath))!["custom"]!.ToJsonString());
    }

    [Fact]
    public void Writer_SaveWriteGrant_OnANewerOrInvalidFile_Refuses_AndLeavesIt()
    {
        foreach (var json in new[] { """{"schemaVersion": 2}""", """{"schemaVersion": 1, "notebooks": []}""" })
        {
            File.WriteAllText(SettingsPath, json);
            var writer = new SettingsWriter(new SettingsStore(SettingsPath), new FakeClock());

            var ex = Assert.Throws<OneNoteException>(() => writer.SaveWriteGrant(new NotebookGrant(@"C:\nb\A", "A", "{NB}", true, false, DateTimeOffset.UnixEpoch, "cli")));

            Assert.Equal(ErrorCode.SettingsInvalid, ex.Code);
            Assert.Equal(json, File.ReadAllText(SettingsPath));
        }
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
