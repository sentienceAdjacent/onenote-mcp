using System.Text.Json;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Server.Cli;
using OneNoteMcp.Server.Grants;
using Xunit;
using V = OneNoteMcp.Tests.VaultGateway;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 1.20: the <c>OneNoteMcp grant</c> CLI (AD-6, AD-21) over the scripted <see cref="VaultGateway"/>, and the
/// elicitation adapter's form and answer mapping (there is no in-process MCP client).
/// </summary>
public sealed class GrantCommandTests : IDisposable
{
    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly VaultGateway gateway = new();
    private readonly StringWriter output = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SettingsStore Store => new(temp.Combine("settings.json"), temp.Combine("journal"));

    public void Dispose()
    {
        output.Dispose();
        temp.Dispose();
    }

    [Fact]
    public async Task NotInteractive_Refuses_AndWritesNothing()
    {
        var code = await Command(string.Empty, interactive: false).RunAsync(["--read-section", "Vault"], Ct);

        Assert.Equal(1, code);
        Assert.Contains("interactive terminal", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Store.Path));
        Assert.Empty(gateway.Calls);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--revoke")]
    public async Task BadUsage_IsExitCode2(string arg) =>
        Assert.Equal(2, await Command(string.Empty).RunAsync([arg], Ct));

    [Fact]
    public async Task Grant_Hours_AmbiguousName_ListsBoth_UserPicks_SavedWithExpiry()
    {
        // Two open sections are named Vault: the user picks the one in Group, then 3 hours.
        var code = await Command("2\n1\n3\n").RunAsync(["--read-section", " vault "], Ct);

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("1) Notebook A > Vault", text, StringComparison.Ordinal);
        Assert.Contains("2) Notebook A > Group > Vault", text, StringComparison.Ordinal);
        var grant = Assert.Single(Store.Read().ProtectedSections.Values);
        Assert.Equal((GrantKeys.SectionKey(V.GroupVaultPath), V.GroupVaultId, GrantedVia.Cli, V.NotebookPath), (grant.Key, grant.IdAtGrant, grant.GrantedVia, grant.NotebookKey));
        Assert.Equal(clock.GetUtcNow().AddHours(3), grant.ExpiresAt);
    }

    [Fact]
    public async Task Grant_UntilRevoked_NameTyped_DefaultHoursNotAsked()
    {
        var code = await Command("Vault\n1\n2\n").RunAsync(["--read-section"], Ct);

        Assert.Equal(0, code);
        var grant = Assert.Single(Store.Read().ProtectedSections.Values);
        Assert.Null(grant.ExpiresAt);
        Assert.Equal(GrantKeys.SectionKey(V.VaultPath), grant.Key);
    }

    [Fact]
    public async Task Grant_UntilRevoked_TheSuggestedRevokeCommand_NeverHoldsTheSectionsName()
    {
        // A shared notebook's section can be named $(...), which PowerShell would expand inside double quotes.
        gateway.NameVault("$(evil)");

        Assert.Equal(0, await Command("2\n").RunAsync(["--read-section", "$(evil)"], Ct));

        var command = output.ToString().Split("with: ")[1].Trim();
        Assert.Equal("OneNoteMcp grant --revoke --read-section \"<section name>\"", command);
    }

    [Fact]
    public async Task Grant_HoursBlank_IsTheDefault8()
    {
        await Command("1\n1\n\n").RunAsync(["--read-section", "Vault"], Ct);

        Assert.Equal(clock.GetUtcNow().AddHours(8), Assert.Single(Store.Read().ProtectedSections.Values).ExpiresAt);
    }

    [Theory]
    [InlineData("Open\n")]
    [InlineData("Nope\n")]
    public async Task Grant_NoProtectedSectionOfThatName_ChangesNothing(string input)
    {
        Assert.Equal(1, await Command(input).RunAsync(["--read-section"], Ct));
        Assert.False(File.Exists(Store.Path));
    }

    [Theory]
    [InlineData("3\n")]
    [InlineData("1\n9\n")]
    [InlineData("1\n1\n200\n")]
    public async Task Grant_ABadChoice_ChangesNothing(string input)
    {
        Assert.Equal(1, await Command(input).RunAsync(["--read-section", "Vault"], Ct));
        Assert.False(File.Exists(Store.Path));
    }

    [Fact]
    public async Task Grant_SectionWithNoPath_IsRefused_NothingWritten()
    {
        gateway.MoveVault(string.Empty);

        Assert.Equal(1, await Command("1\n2\n").RunAsync(["--read-section", "Vault"], Ct));
        Assert.Contains("no path", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Store.Path));
    }

    [Fact]
    public async Task Revoke_RemovesTheGrant_AndANotOpenGrantCanBeRevokedByItsStoredName()
    {
        var writer = new SettingsWriter(Store, clock);
        writer.SaveReadGrant(new ProtectedSectionGrant(GrantKeys.SectionKey(V.VaultPath), "Vault", V.NotebookPath, V.VaultId, clock.GetUtcNow(), GrantedVia.Cli));
        writer.SaveReadGrant(new ProtectedSectionGrant(@"C:\gone\Old.one", "Old", "", "{X}", clock.GetUtcNow(), GrantedVia.Cli));

        Assert.Equal(0, await Command(string.Empty).RunAsync(["--revoke", "--read-section", "vault"], Ct));
        Assert.Equal(0, await Command(string.Empty).RunAsync(["--revoke", "--read-section", "Old"], Ct));

        Assert.Empty(Store.Read().ProtectedSections);
    }

    [Fact]
    public async Task Revoke_RunsOnAFileInvalidOnlyForCaseDuplicateKeys()
    {
        var key = GrantKeys.SectionKey(V.VaultPath);
        var entry = """{"name": "Vault", "notebookKey": "", "idAtGrant": "i", "grantedAt": "2026-11-07T12:00:00Z", "grantedVia": "cli"}""";
        File.WriteAllText(Store.Path, """{"schemaVersion": 1, "protectedSections": {""" + JsonSerializer.Serialize(key) + ": " + entry + ", " + JsonSerializer.Serialize(key.ToUpperInvariant()) + ": " + entry + "}}");
        Assert.True(Store.Read().DuplicateGrantKeysOnly);

        Assert.Equal(0, await Command(string.Empty).RunAsync(["--revoke", "--read-section", "Vault"], Ct));

        var read = Store.Read();
        Assert.Equal(SettingsStatus.Ok, read.Status);
        Assert.Empty(read.ProtectedSections);
    }

    [Fact]
    public async Task Revoke_WithNoMatchingGrant_ChangesNothing() =>
        Assert.Equal(1, await Command(string.Empty).RunAsync(["--revoke", "--read-section", "Vault"], Ct));

    [Fact]
    public async Task Grant_OnAnInvalidSettingsFile_FailsAndSaysWhy()
    {
        File.WriteAllText(Store.Path, "{ nope");

        Assert.Equal(1, await Command("1\n2\n").RunAsync(["--read-section", "Vault"], Ct));
        Assert.Contains("Nothing changed", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("{ nope", File.ReadAllText(Store.Path));
    }

    // --- grant --write (story 2.4, AD-6) ----------------------------------------------------------------------------

    private const string NotebooksXml =
        "<?xml version=\"1.0\"?>\n<one:Notebooks xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\">"
        + "<one:Notebook name=\"Notebook A\" nickname=\"Notebook A\" ID=\"{NA}{1}{B0}\" path=\"C:\\notebooks\\A\"><one:Section name=\"S\" ID=\"{S}{1}{B0}\" path=\"C:\\notebooks\\A\\S.one\" /></one:Notebook>"
        + "<one:Notebook name=\"folder-b\" nickname=\"Notebook B\" ID=\"{NB1}{1}{B0}\" path=\"https://d.docs.live.net/abc/Documents/folder-b/\" />"
        + "<one:Notebook name=\"Notebook B\" ID=\"{NB2}{1}{B0}\" path=\"C:\\notebooks\\B\" />"
        + "<one:Notebook name=\"Notebook C\" ID=\"{NC}{1}{B0}\" path=\"\" />"
        + "</one:Notebooks>";

    private int notebookReads;

    [Fact]
    public async Task Write_NotInteractive_Refuses_ReadsNothing_WritesNothing()
    {
        var code = await WriteCommand("yes\n", interactive: false).RunAsync(["--write", "Notebook A"], Ct);

        Assert.Equal(1, code);
        Assert.Contains("interactive terminal", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, notebookReads);
        Assert.False(File.Exists(Store.Path));
    }

    [Fact]
    public async Task Write_SingleMatch_ShowsNameAndPath_GrantsOnYes()
    {
        var code = await WriteCommand(" YES \n").RunAsync(["--write", " notebook a "], Ct);

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("Notebook: Notebook A", text, StringComparison.Ordinal);
        Assert.Contains(@"Path:     C:\notebooks\A", text, StringComparison.Ordinal);
        var grant = Assert.Single(Store.Read().Notebooks.Values);
        Assert.Equal(new NotebookGrant(@"C:\notebooks\A", "Notebook A", "{NA}{1}{B0}", true, false, clock.GetUtcNow(), GrantedVia.Cli), grant);
    }

    [Theory]
    [InlineData("no\n")]
    [InlineData("y\n")]
    [InlineData("\n")]
    [InlineData("")]
    public async Task Write_AnythingButYes_ChangesNothing(string input)
    {
        Assert.Equal(1, await WriteCommand(input).RunAsync(["--write", "Notebook A"], Ct));
        Assert.Contains("Nothing changed", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Store.Path));
    }

    [Fact]
    public async Task Write_AmbiguousName_ListsEachWithItsPath_UserPicks_KeyedByThePickedPath()
    {
        // "Notebook B" is one notebook's nickname and another's name: both match the displayed name.
        var code = await WriteCommand("1\nyes\n").RunAsync(["--write", "Notebook B"], Ct);

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("Several notebooks match:", text, StringComparison.Ordinal);
        Assert.Contains("1) Notebook B (https://d.docs.live.net/abc/Documents/folder-b/)", text, StringComparison.Ordinal);
        Assert.Contains(@"2) Notebook B (C:\notebooks\B)", text, StringComparison.Ordinal);
        var grant = Assert.Single(Store.Read().Notebooks.Values);
        Assert.Equal("https://d.docs.live.net/abc/Documents/folder-b", grant.Key); // NotebookKey trims the trailing slash
        Assert.Equal("{NB1}{1}{B0}", grant.IdAtGrant);
    }

    [Theory]
    [InlineData("3\n")]
    [InlineData("x\n")]
    public async Task Write_AmbiguousName_ABadPick_ChangesNothing(string input)
    {
        Assert.Equal(1, await WriteCommand(input).RunAsync(["--write", "Notebook B"], Ct));
        Assert.False(File.Exists(Store.Path));
    }

    [Fact]
    public async Task Write_NoMatch_ChangesNothing()
    {
        // A notebook's folder name isn't its displayed name when it has a nickname.
        Assert.Equal(1, await WriteCommand("yes\n").RunAsync(["--write", "folder-b"], Ct));
        Assert.Contains("No open notebook has that name", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Store.Path));
    }

    [Fact]
    public async Task Write_NotebookWithNoPath_IsRefused_NothingWritten()
    {
        Assert.Equal(1, await WriteCommand("yes\n").RunAsync(["--write", "Notebook C"], Ct));
        Assert.Contains("no path", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Store.Path));
    }

    [Fact]
    public async Task Write_NameAskedWhenNotGiven()
    {
        Assert.Equal(0, await WriteCommand("Notebook A\nyes\n").RunAsync(["--write"], Ct));
        Assert.Contains("Notebook name: ", output.ToString(), StringComparison.Ordinal);
        Assert.Single(Store.Read().Notebooks);
    }

    [Fact]
    public async Task Write_WithReadSection_IsBadUsage()
    {
        Assert.Equal(2, await WriteCommand("yes\n").RunAsync(["--write", "--read-section"], Ct));
        Assert.False(File.Exists(Store.Path));
    }

    [Fact]
    public void Usage_NamesWrite_AndItsRevoke()
    {
        Assert.Contains("grant --write", GrantCommand.Usage, StringComparison.Ordinal);
        Assert.Contains("grant --revoke --write", GrantCommand.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_Granted_SaysHowToRevoke()
    {
        Assert.Equal(0, await WriteCommand("yes\n").RunAsync(["--write", "Notebook A"], Ct));

        Assert.Contains("grant --revoke --write \"<notebook name>\"", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_Granted_TheSuggestedRevokeCommand_NeverHoldsTheNotebooksName()
    {
        // A shared notebook can be named $(...), which PowerShell would expand inside double quotes.
        var xml = NotebooksXml.Replace("\"Notebook A\"", "\"$(evil)\"", StringComparison.Ordinal);

        Assert.Equal(0, await WriteCommand("yes\n", xml: xml).RunAsync(["--write", "$(evil)"], Ct));

        var command = output.ToString().Split("with: ")[1].Trim();
        Assert.Equal("OneNoteMcp grant --revoke --write \"<notebook name>\"", command);
    }

    // --- grant --revoke --write (story 2.6, AD-6) -------------------------------------------------------------------

    private void SaveWriteGrant(string key, string name) =>
        new SettingsWriter(Store, clock).SaveWriteGrant(new NotebookGrant(key, name, "{OLD}{1}{B0}", true, false, clock.GetUtcNow(), GrantedVia.Elicitation));

    [Fact]
    public async Task RevokeWrite_ByTheLiveDisplayedName_RemovesIt_WithNoYes()
    {
        // Stored under an old name; the open notebook with that key is displayed as Notebook A now.
        SaveWriteGrant(@"C:\notebooks\A", "Old name");
        SaveWriteGrant(@"C:\notebooks\B", "Notebook B");

        var code = await WriteCommand(string.Empty).RunAsync(["--revoke", "--write", " notebook a "], Ct);

        Assert.Equal(0, code);
        Assert.Contains("Revoked the write grant for \"Notebook A\"", output.ToString(), StringComparison.Ordinal);
        Assert.Equal([@"C:\notebooks\B"], Store.Read().Notebooks.Keys);
        Assert.Equal(1, notebookReads);
    }

    [Fact]
    public async Task RevokeWrite_ANotebookThatIsntOpen_ByItsStoredName()
    {
        SaveWriteGrant(@"C:\gone\Old", "Old");

        Assert.Equal(0, await WriteCommand(string.Empty).RunAsync(["--revoke", "--write", "Old"], Ct));

        Assert.Empty(Store.Read().Notebooks);
    }

    [Fact]
    public async Task RevokeWrite_SeveralMatches_ListsEachWithItsPath_UserPicks()
    {
        SaveWriteGrant("https://d.docs.live.net/abc/Documents/folder-b", "Notebook B");
        SaveWriteGrant(@"C:\notebooks\B", "Notebook B");

        var code = await WriteCommand("2\n").RunAsync(["--revoke", "--write", "Notebook B"], Ct);

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("Several notebooks match", text, StringComparison.Ordinal);
        Assert.Contains("1) Notebook B (https://d.docs.live.net/abc/Documents/folder-b/)", text, StringComparison.Ordinal);
        Assert.Contains(@"2) Notebook B (C:\notebooks\B)", text, StringComparison.Ordinal);
        Assert.Equal(["https://d.docs.live.net/abc/Documents/folder-b"], Store.Read().Notebooks.Keys);
    }

    [Theory]
    [InlineData("3\n")]
    [InlineData("\n")]
    public async Task RevokeWrite_SeveralMatches_ABadPick_ChangesNothing(string input)
    {
        SaveWriteGrant("https://d.docs.live.net/abc/Documents/folder-b", "Notebook B");
        SaveWriteGrant(@"C:\notebooks\B", "Notebook B");

        Assert.Equal(1, await WriteCommand(input).RunAsync(["--revoke", "--write", "Notebook B"], Ct));

        Assert.Equal(2, Store.Read().Notebooks.Count);
    }

    [Fact]
    public async Task RevokeWrite_WithNoMatchingGrant_ChangesNothing()
    {
        SaveWriteGrant(@"C:\notebooks\A", "Notebook A");

        Assert.Equal(1, await WriteCommand(string.Empty).RunAsync(["--revoke", "--write", "Notebook C"], Ct));

        Assert.Contains("No stored write grant matches", output.ToString(), StringComparison.Ordinal);
        Assert.Single(Store.Read().Notebooks);
    }

    [Fact]
    public async Task RevokeWrite_AnEntryWithWriteFalse_IsNotACandidate()
    {
        File.WriteAllText(
            Store.Path,
            """{"schemaVersion": 1, "notebooks": {"C:\\notebooks\\A": {"name": "Notebook A", "idAtGrant": "i", "write": false, "permanentDelete": true, "grantedAt": "2026-11-07T12:00:00.000Z", "grantedVia": "cli"}}}""");
        var before = File.ReadAllText(Store.Path);

        Assert.Equal(1, await WriteCommand(string.Empty).RunAsync(["--revoke", "--write", "Notebook A"], Ct));

        Assert.Contains("No stored write grant matches", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(Store.Path));
    }

    [Fact]
    public async Task RevokeWrite_LeavesReadGrants()
    {
        SaveWriteGrant(@"C:\notebooks\A", "Notebook A");
        new SettingsWriter(Store, clock).SaveReadGrant(new ProtectedSectionGrant(@"C:\notebooks\A\S.one", "S", @"C:\notebooks\A", "{S}", clock.GetUtcNow(), GrantedVia.Cli));

        Assert.Equal(0, await WriteCommand(string.Empty).RunAsync(["--revoke", "--write", "Notebook A"], Ct));

        var read = Store.Read();
        Assert.Empty(read.Notebooks);
        Assert.Single(read.ProtectedSections);
    }

    [Fact]
    public async Task RevokeWrite_RunsOnAFileInvalidOnlyForDuplicateKeys_RemovingEveryExactAndCaseOnlyCopy()
    {
        var entry = """{"name": "Notebook A", "idAtGrant": "i", "write": true, "permanentDelete": false, "grantedAt": "2026-11-07T12:00:00Z", "grantedVia": "cli"}""";
        File.WriteAllText(Store.Path, """{"schemaVersion": 1, "notebooks": {"C:\\notebooks\\A": @E@, "C:\\notebooks\\A": @E@, "c:\\NOTEBOOKS\\a": @E@, "C:\\notebooks\\B": @B@}}"""
            .Replace("@E@", entry, StringComparison.Ordinal)
            .Replace("@B@", entry.Replace("Notebook A", "Notebook B", StringComparison.Ordinal), StringComparison.Ordinal));
        Assert.True(Store.Read().DuplicateNotebookKeys);

        Assert.Equal(0, await WriteCommand(string.Empty).RunAsync(["--revoke", "--write", "Notebook A"], Ct));

        var read = Store.Read();
        Assert.Equal(SettingsStatus.Ok, read.Status);
        Assert.Equal([@"C:\notebooks\B"], read.Notebooks.Keys);
    }

    [Fact]
    public async Task RevokeWrite_OnAnInvalidSettingsFile_FailsAndSaysWhy()
    {
        File.WriteAllText(Store.Path, "{ nope");

        Assert.Equal(1, await WriteCommand(string.Empty).RunAsync(["--revoke", "--write", "Notebook A"], Ct));

        Assert.Contains("Nothing changed", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("{ nope", File.ReadAllText(Store.Path));
    }

    // --- grant --permanent-delete (story 5.6, plan Q1 c and Q2 b) -------------------------------------------------------

    [Fact]
    public void Usage_NamesThePermanentDeleteGrantAndItsRevoke()
    {
        Assert.Contains("grant --permanent-delete", GrantCommand.Usage, StringComparison.Ordinal);
        Assert.Contains("grant --revoke --permanent-delete", GrantCommand.Usage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--write", "--permanent-delete")]
    [InlineData("--permanent-delete", "--read-section")]
    public async Task PermanentDelete_WithAnotherKind_IsBadUsage(string first, string second) =>
        Assert.Equal(2, await WriteCommand("yes\n").RunAsync([first, second], Ct));

    [Fact]
    public async Task PermanentDelete_WithoutAWriteGrant_IsRefused_AndNothingChanges()
    {
        Assert.Equal(1, await WriteCommand("yes\n").RunAsync(["--permanent-delete", "Notebook A"], Ct));

        Assert.Contains("has no write grant", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Store.Path));
    }

    [Fact]
    public async Task PermanentDelete_ShowsThePath_AndIsMadeOnlyOnYes_BesideTheWriteGrant()
    {
        SaveWriteGrant(@"C:\notebooks\A", "Notebook A");

        Assert.Equal(0, await WriteCommand(" yes \n").RunAsync(["--permanent-delete", " notebook a "], Ct));

        var text = output.ToString();
        Assert.Contains(@"Path:     C:\notebooks\A", text, StringComparison.Ordinal);
        Assert.Contains("grant --revoke --permanent-delete \"<notebook name>\"", text, StringComparison.Ordinal);
        var grant = Assert.Single(Store.Read().Notebooks.Values);
        Assert.True(grant.Write && grant.PermanentDelete);
    }

    [Fact]
    public async Task PermanentDelete_WithoutAWriteGrant_TheSuggestedCommand_NeverHoldsTheNotebooksName()
    {
        // A shared or model-created notebook can be named $(...), which PowerShell would expand inside double quotes.
        var xml = NotebooksXml.Replace("\"Notebook A\"", "\"$(evil)\"", StringComparison.Ordinal);

        Assert.Equal(1, await WriteCommand("yes\n", xml: xml).RunAsync(["--permanent-delete", "$(evil)"], Ct));

        var command = output.ToString().Split("with: ")[1].Split('\n')[0].Trim();
        Assert.Equal("OneNoteMcp grant --write \"<notebook name>\"", command);
    }

    [Fact]
    public async Task PermanentDelete_Granted_TheSuggestedRevokeCommand_NeverHoldsTheNotebooksName()
    {
        // A shared or model-created notebook can be named $(...), which PowerShell would expand inside double quotes.
        SaveWriteGrant(@"C:\notebooks\A", "$(evil)");
        var xml = NotebooksXml.Replace("\"Notebook A\"", "\"$(evil)\"", StringComparison.Ordinal);

        Assert.Equal(0, await WriteCommand("yes\n", xml: xml).RunAsync(["--permanent-delete", "$(evil)"], Ct));

        var command = output.ToString().Split("with: ")[1].Trim();
        Assert.Equal("OneNoteMcp grant --revoke --permanent-delete \"<notebook name>\"", command);
    }

    [Theory]
    [InlineData("no\n")]
    [InlineData("")]
    public async Task PermanentDelete_NotConfirmedWithYes_ChangesNothing(string input)
    {
        SaveWriteGrant(@"C:\notebooks\A", "Notebook A");

        Assert.Equal(1, await WriteCommand(input).RunAsync(["--permanent-delete", "Notebook A"], Ct));

        Assert.False(Assert.Single(Store.Read().Notebooks.Values).PermanentDelete);
    }

    [Fact]
    public async Task RevokePermanentDelete_RemovesOnlyIt_TheWriteGrantStays()
    {
        SaveWriteGrant(@"C:\notebooks\A", "Notebook A");
        new SettingsWriter(Store, clock).SavePermanentDeleteGrant(@"C:\notebooks\A");

        Assert.Equal(0, await WriteCommand(string.Empty).RunAsync(["--revoke", "--permanent-delete", "Notebook A"], Ct));

        Assert.Contains("Revoked the permanent-delete grant for \"Notebook A\"", output.ToString(), StringComparison.Ordinal);
        var grant = Assert.Single(Store.Read().Notebooks.Values);
        Assert.True(grant.Write);
        Assert.False(grant.PermanentDelete);
    }

    [Fact]
    public async Task RevokePermanentDelete_WithNoneStored_ChangesNothing()
    {
        SaveWriteGrant(@"C:\notebooks\A", "Notebook A");

        Assert.Equal(1, await WriteCommand(string.Empty).RunAsync(["--revoke", "--permanent-delete", "Notebook A"], Ct));

        Assert.True(Assert.Single(Store.Read().Notebooks.Values).Write);
    }

    [Fact]
    public async Task RevokeWrite_TakesThePermanentDeleteGrantWithIt_AndSaysSo()
    {
        SaveWriteGrant(@"C:\notebooks\A", "Notebook A");
        new SettingsWriter(Store, clock).SavePermanentDeleteGrant(@"C:\notebooks\A");

        Assert.Equal(0, await WriteCommand(string.Empty).RunAsync(["--revoke", "--write", "Notebook A"], Ct));

        Assert.Contains("and its permanent-delete grant with it", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(Store.Read().Notebooks);
    }

    [Fact]
    public void Writer_PermanentDeleteGrant_NeedsAWriteGrant_AndAWriteRegrantKeepsALiveOne()
    {
        var writer = new SettingsWriter(Store, clock);
        Assert.Equal(ErrorCode.WriteNotGranted, Assert.Throws<OneNoteException>(() => writer.SavePermanentDeleteGrant(@"C:\notebooks\A")).Code);

        SaveWriteGrant(@"C:\notebooks\A", "Notebook A");
        writer.SavePermanentDeleteGrant(@"c:\NOTEBOOKS\a");
        SaveWriteGrant(@"C:\notebooks\A", "Notebook A");

        Assert.True(Assert.Single(Store.Read().Notebooks.Values).PermanentDelete);
        Assert.Equal(1, writer.RevokePermanentDeleteGrant(@"C:\notebooks\A"));
        Assert.Equal(0, writer.RevokePermanentDeleteGrant(@"C:\notebooks\A"));
    }

    [Fact]
    public void PermanentDeleteRequest_NamesWhatGoes_AndAsksForTheNameInAFreshField()
    {
        var field = ElicitationGrantPrompt.NewObjectNameField();
        Assert.Matches("^objectName_[0-9a-f]{8}$", field);
        Assert.NotEqual(field, ElicitationGrantPrompt.NewObjectNameField());

        var request = ElicitationGrantPrompt.PermanentDeleteRequest(new PermanentDeleteQuestion("{S}", "section", "Old notes", "Notebook A", "Old notes", @"C:\notebooks\A", PageCount: 3), field);

        Assert.StartsWith(@"Claude wants to permanently delete a section with 3 pages from the OneNote notebook stored at C:\notebooks\A. ", request.Message, StringComparison.Ordinal);
        Assert.Contains(@"Its name is ""Old notes"", and the notebook's name is ""Notebook A"".", request.Message, StringComparison.Ordinal);
        Assert.Contains("grant --permanent-delete \"<notebook name>\"", request.Message, StringComparison.Ordinal);
        Assert.Equal([field], request.RequestedSchema!.Required);
        var typed = Assert.IsType<ElicitRequestParams.StringSchema>(request.RequestedSchema!.Properties[field]);
        Assert.Contains("Old notes", typed.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void PermanentDeleteRequest_ForAnUntitledPage_AsksForTheWordDelete()
    {
        var request = ElicitationGrantPrompt.PermanentDeleteRequest(new PermanentDeleteQuestion("{P}", "page", string.Empty, "Notebook A", "delete", SectionName: "Ideas"), "objectName_00000000");

        Assert.Contains("It has no title, it is in the section \"Ideas\", and the notebook's name is \"Notebook A\".", request.Message, StringComparison.Ordinal);
        Assert.Contains("type \"delete\"", request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PermanentDeleteRequest_PutsWhatTheServerWorkedOut_BeforeAnyModelWrittenName()
    {
        // Fresh review: a name such as  Draft＂ (0 pages) of the notebook ＂Scratch  must not open the sentence with fake facts.
        var name = DialogText.Shown("Draft\uFF02 (0 pages) of the notebook \uFF02Scratch", out var typed);
        var request = ElicitationGrantPrompt.PermanentDeleteRequest(
            new PermanentDeleteQuestion("{P}", "page", name, "Notebook A", typed, @"C:\notebooks\A", SectionName: "Ideas", LastModified: "2026-11-12 00:00:01 UTC"),
            "objectName_00000000");

        var facts = @"Claude wants to permanently delete a page, last modified 2026-11-12 00:00:01 UTC, from the OneNote notebook stored at C:\notebooks\A. ";
        Assert.StartsWith(facts, request.Message, StringComparison.Ordinal);
        Assert.Contains("Its title is \"Draft' (0 pages) of the notebook 'Scratch\", it is in the section \"Ideas\"", request.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFF02", request.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PermanentDeleteRequest_TheSuggestedCommand_NeverHoldsTheNotebooksName()
    {
        // A model can name a notebook $(...), which PowerShell would expand inside double quotes.
        var request = ElicitationGrantPrompt.PermanentDeleteRequest(new PermanentDeleteQuestion("{P}", "page", "x", "$(evil)", "x"), "objectName_00000000");

        var command = request.Message!.Split('`')[1];
        Assert.EndsWith("grant --permanent-delete \"<notebook name>\"", command, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Plain", "Plain", "Plain")]
    [InlineData("  a\u202Eb\u200Bc\r\n\u2028d\u2029\"e\"  ", "abcd'e'", "abcd'e'")]
    [InlineData("", "", "")]
    [InlineData(null, "", "")]
    public void Shown_DropsControlFormatAndSeparatorCharacters_AndSwapsDoubleQuotes(string? text, string shown, string typed)
    {
        Assert.Equal(shown, DialogText.Shown(text, out var expected));
        Assert.Equal(typed, expected);
    }

    [Theory]
    [InlineData("“A” ‘B’ „C‟ ‚D‛", "'A' 'B' 'C' 'D'")]
    [InlineData("«E» ‹F› ＂G＂ ＇H＇", "'E' 'F' 'G' 'H'")]
    [InlineData("「I」 『J』 〝K〞〟 ｢L｣", "'I' 'J' 'K'' 'L'")]
    [InlineData("″M″ 〃N ⹂O ❝P❞", "'M' 'N 'O 'P'")]
    [InlineData("(a) [b] {c} <d> 【e】 （f）", "(a) [b] {c} <d> 【e】 （f）")]
    public void Shown_TurnsLookAlikeQuotesIntoApostrophes_ButKeepsBrackets(string text, string shown) =>
        Assert.Equal(shown, DialogText.Shown(text, out _));

    [Fact]
    public void Shown_CutsALongNameTo80_WithAnEllipsis_NeverSplittingASurrogatePair()
    {
        var shown = DialogText.Shown(new string('a', 78) + "\U0001F600" + "zzz", out var typed);

        Assert.Equal(new string('a', 78) + "…", shown);
        Assert.Equal(new string('a', 78), typed);
    }

    [Theory]
    [InlineData("Old notes", "Old notes", true)]
    [InlineData("  old NOTES ", "Old notes", true)]
    [InlineData("Old", "Old notes", false)]
    [InlineData("", "Old notes", false)]
    [InlineData(null, "Old notes", false)]
    public void PermanentDeleteDialog_MatchesTheTrimmedName_IgnoringCase(string? typed, string expected, bool matches) =>
        Assert.Equal(matches, DialogText.Matches(typed, expected));

    private GrantCommand WriteCommand(string input, bool interactive = true, string xml = NotebooksXml) =>
        new(new StringReader(input), output, interactive, _ =>
        {
            notebookReads++;
            return Task.FromResult(xml);
        }, new SettingsWriter(Store, clock), clock);

    // --- Elicitation adapter --------------------------------------------------------------------------------------

    [Fact]
    public void Elicitation_Form_NamesSectionAndNotebook_TypedNameRequired_ThreeLifetimes_Hours1To168()
    {
        var request = ElicitationGrantPrompt.Request(new GrantQuestion("{S}", "Vault", "Notebook A", ["Group"], "Vault"), "sectionName_0a1b2c3d");

        Assert.Contains("\"Vault\"", request.Message, StringComparison.Ordinal);
        Assert.Contains("\"Notebook A\"", request.Message, StringComparison.Ordinal);
        Assert.Contains("\"Group\"", request.Message, StringComparison.Ordinal);
        var schema = request.RequestedSchema!;
        Assert.Equal(["sectionName_0a1b2c3d", ElicitationGrantPrompt.LifetimeField], schema.Required);
        var lifetimes = Assert.IsType<ElicitRequestParams.TitledSingleSelectEnumSchema>(schema.Properties[ElicitationGrantPrompt.LifetimeField]);
        Assert.Equal(["once", "hours:1", "hours:8", "hours:24", "hours:168", "untilRevoked"], lifetimes.OneOf.Select(o => o.Const));
        Assert.Equal(["This section, for 5 minutes", "1 hour", "8 hours", "24 hours", "7 days", "Until I revoke it"], lifetimes.OneOf.Select(o => o.Title));
        Assert.Equal("once", lifetimes.Default);
        Assert.Equal(2, schema.Properties.Count);
        Assert.False(schema.Properties.ContainsKey("hours"));
        var name = Assert.IsType<ElicitRequestParams.StringSchema>(schema.Properties["sectionName_0a1b2c3d"]);
        Assert.Contains("\"Vault\"", name.Description, StringComparison.Ordinal);
        Assert.IsType<ElicitRequestParams.StringSchema>(schema.Properties["sectionName_0a1b2c3d"]);
        Assert.False(schema.Properties.ContainsKey(ElicitationGrantPrompt.SectionNameField));
        Assert.Equal("This section, for 5 minutes", lifetimes.OneOf[0].Title);
    }

    [Fact]
    public void Elicitation_Form_PutsWhatTheServerWorkedOut_BeforeAnyName_AndAsksForTheShownName()
    {
        // Follow-up to 5.6: a section named  Vault＂ in the notebook “Work”  must not open the sentence with fake facts.
        var section = DialogText.Shown("Vault＂ in the notebook “Work”​", out var typed);
        var request = ElicitationGrantPrompt.Request(new GrantQuestion("{S}", section, "Notebook A", ["Group"], typed), "sectionName_0a1b2c3d");

        Assert.StartsWith("Claude wants to read a password-protected OneNote section. ", request.Message, StringComparison.Ordinal);
        Assert.Contains(
            "Its name is \"Vault' in the notebook 'Work'\", it is in the section group \"Group\", and the notebook's name is \"Notebook A\". "
                + "To allow it, type \"Vault' in the notebook 'Work'\" and choose for how long.",
            request.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("​", request.Message, StringComparison.Ordinal);
        var field = Assert.IsType<ElicitRequestParams.StringSchema>(request.RequestedSchema!.Properties["sectionName_0a1b2c3d"]);
        Assert.Equal("Type \"Vault' in the notebook 'Work'\" to confirm.", field.Description);
    }

    [Fact]
    public void Elicitation_Answers_MapToTheOutcome()
    {
        Assert.Equal(GrantPromptStatus.Declined, ElicitationGrantPrompt.Read(new ElicitResult { Action = "decline" }, "f").Status);
        Assert.Equal(GrantPromptStatus.Cancelled, ElicitationGrantPrompt.Read(new ElicitResult { Action = "cancel" }, "f").Status);

        // Each offered const, and anything else (an unknown value, an hour count not offered, the old "hours") as once.
        (string Const, GrantPromptResult Expected)[] cases =
        [
            ("once", new(GrantPromptStatus.Accepted, "Vault", ReadGrantLifetime.Once)),
            ("hours:1", new(GrantPromptStatus.Accepted, "Vault", ReadGrantLifetime.Hours, 1)),
            ("hours:8", new(GrantPromptStatus.Accepted, "Vault", ReadGrantLifetime.Hours, 8)),
            ("hours:24", new(GrantPromptStatus.Accepted, "Vault", ReadGrantLifetime.Hours, 24)),
            ("hours:168", new(GrantPromptStatus.Accepted, "Vault", ReadGrantLifetime.Hours, 168)),
            ("untilRevoked", new(GrantPromptStatus.Accepted, "Vault", ReadGrantLifetime.UntilRevoked)),
            ("forever", new(GrantPromptStatus.Accepted, "Vault", ReadGrantLifetime.Once)),
            ("hours:12", new(GrantPromptStatus.Accepted, "Vault", ReadGrantLifetime.Once)),
            ("hours", new(GrantPromptStatus.Accepted, "Vault", ReadGrantLifetime.Once)),
        ];
        foreach (var (value, expected) in cases)
        {
            var answer = ElicitationGrantPrompt.Read(new ElicitResult
            {
                Action = "accept",
                Content = new Dictionary<string, JsonElement>
                {
                    ["sectionName_0a1b2c3d"] = JsonSerializer.SerializeToElement("Vault"),
                    ["lifetime"] = JsonSerializer.SerializeToElement(value),
                },
            }, "sectionName_0a1b2c3d");
            Assert.Equal(expected, answer);
        }
    }

    [Fact]
    public void Elicitation_TypedNameField_IsNewEveryRequest_AndAnyOtherKey_IsAMismatch()
    {
        var a = ElicitationGrantPrompt.NewSectionNameField();
        var b = ElicitationGrantPrompt.NewSectionNameField();
        Assert.Matches("^sectionName_[0-9a-f]{8}$", a);
        Assert.NotEqual(a, b);

        // The old fixed key, or another request's field, gives no typed name: Core then finds no match.
        foreach (var key in new[] { ElicitationGrantPrompt.SectionNameField, b })
        {
            var answer = ElicitationGrantPrompt.Read(new ElicitResult
            {
                Action = "accept",
                Content = new Dictionary<string, JsonElement>
                {
                    [key] = JsonSerializer.SerializeToElement("Vault"),
                    ["lifetime"] = JsonSerializer.SerializeToElement("untilRevoked"),
                },
            }, a);
            Assert.Equal(GrantPromptStatus.Accepted, answer.Status);
            Assert.Null(answer.TypedName);
            Assert.False(GrantKeys.NameMatches(answer.TypedName, "Vault"));
        }
    }

    [Theory]
    [InlineData(5, "This section, for 5 minutes")]
    [InlineData(1, "This section, for 1 minute")]
    [InlineData(60, "This section, for 60 minutes")]
    public void Elicitation_OnceOption_NamesTheWindow(int minutes, string title)
    {
        Assert.Equal(title, ElicitationGrantPrompt.OnceTitle(minutes));
        var request = ElicitationGrantPrompt.Request(new GrantQuestion("{S}", "Vault", "Notebook A", [], "Vault", minutes), "sectionName_00000000");
        var lifetimes = Assert.IsType<ElicitRequestParams.TitledSingleSelectEnumSchema>(request.RequestedSchema!.Properties[ElicitationGrantPrompt.LifetimeField]);
        Assert.Equal(title, lifetimes.OneOf.Single(o => o.Const == ReadGrantLifetimes.Once).Title);
    }

    [Fact]
    public void Elicitation_NoServer_NoPrompt() => Assert.Null(ElicitationGrantPrompt.For(null));

    // --- The write-grant dialog's form and answers (story 2.6, AD-6) --------------------------------------------------

    [Fact]
    public void WriteForm_NamesTheNotebookAndPath_SaysUntilRevoked_OneTypedNameField_WithTheNameAsHint_NoLifetime()
    {
        var request = ElicitationGrantPrompt.WriteRequest(new WriteGrantQuestion("{NA}{1}{B0}", "Notebook A", @"C:\notebooks\A", "Notebook A"), "notebookName_0a1b2c3d");

        Assert.Contains("\"Notebook A\"", request.Message, StringComparison.Ordinal);
        Assert.Contains(@"C:\notebooks\A", request.Message, StringComparison.Ordinal);
        Assert.Contains("until you revoke it", request.Message, StringComparison.Ordinal);
        Assert.Contains("grant --revoke --write \"<notebook name>\"", request.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{NA}", request.Message, StringComparison.Ordinal);
        var schema = request.RequestedSchema!;
        Assert.Equal(["notebookName_0a1b2c3d"], schema.Properties.Keys);
        Assert.Equal(["notebookName_0a1b2c3d"], schema.Required);
        Assert.False(schema.Properties.ContainsKey(ElicitationGrantPrompt.LifetimeField));
        var name = Assert.IsType<ElicitRequestParams.StringSchema>(schema.Properties["notebookName_0a1b2c3d"]);
        Assert.Contains("\"Notebook A\"", name.Description, StringComparison.Ordinal);
        Assert.Equal(1, name.MinLength);
    }

    [Fact]
    public void WriteForm_PutsThePathBeforeTheNotebooksName_AndAsksForTheShownName()
    {
        // Follow-up to 5.6: a model-written name such as  Scratch＂ at C:\fake: “ok”  must not open the sentence with fake facts.
        var name = DialogText.Shown("Scratch\uFF02 at C:\\fake: \u201Cok\u201D\u202E", out var typed);
        var request = ElicitationGrantPrompt.WriteRequest(new WriteGrantQuestion("{NA}{1}{B0}", name, @"C:\notebooks\A", typed), "notebookName_0a1b2c3d");

        Assert.StartsWith(@"Claude wants to write to a OneNote notebook stored at C:\notebooks\A: to create and change pages in it. ", request.Message, StringComparison.Ordinal);
        Assert.Contains("Its name is \"Scratch' at C:\\fake: 'ok'\". To allow it, type \"Scratch' at C:\\fake: 'ok'\".", request.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFF02", request.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202E", request.Message, StringComparison.Ordinal);
        var field = Assert.IsType<ElicitRequestParams.StringSchema>(request.RequestedSchema!.Properties["notebookName_0a1b2c3d"]);
        Assert.Equal("Type \"Scratch' at C:\\fake: 'ok'\" to confirm.", field.Description);
    }

    [Fact]
    public void WriteForm_ALongName_IsShownCut_AndTheFieldAsksForWhatIsTyped()
    {
        var name = DialogText.Shown(new string('n', 100), out var typed);
        var request = ElicitationGrantPrompt.WriteRequest(new WriteGrantQuestion("{NA}{1}{B0}", name, @"C:\notebooks\A", typed), "notebookName_0a1b2c3d");

        Assert.Contains($"Its name is \"{new string('n', 79)}…\".", request.Message, StringComparison.Ordinal);
        var field = Assert.IsType<ElicitRequestParams.StringSchema>(request.RequestedSchema!.Properties["notebookName_0a1b2c3d"]);
        Assert.Equal($"Type \"{new string('n', 79)}\" to confirm.", field.Description);
    }

    [Fact]
    public void WriteForm_TheSuggestedCommand_NeverHoldsTheNotebooksName()
    {
        // A shared notebook can be named $(...), which PowerShell would expand inside double quotes.
        var request = ElicitationGrantPrompt.WriteRequest(new WriteGrantQuestion("{NA}{1}{B0}", "$(evil)", @"C:\notebooks\A", "$(evil)"), "notebookName_0a1b2c3d");

        var command = request.Message!.Split('`')[1];
        Assert.EndsWith("grant --revoke --write \"<notebook name>\"", command, StringComparison.Ordinal);
        Assert.DoesNotContain("evil", command, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteForm_Answers_MapToTheOutcome_WithNoLifetime()
    {
        Assert.Equal(new GrantPromptResult(GrantPromptStatus.Declined), ElicitationGrantPrompt.ReadWrite(new ElicitResult { Action = "decline" }, "f"));
        Assert.Equal(new GrantPromptResult(GrantPromptStatus.Cancelled), ElicitationGrantPrompt.ReadWrite(new ElicitResult { Action = "cancel" }, "f"));
        Assert.Equal(new GrantPromptResult(GrantPromptStatus.Accepted), ElicitationGrantPrompt.ReadWrite(new ElicitResult { Action = "accept" }, "f"));

        var answer = ElicitationGrantPrompt.ReadWrite(new ElicitResult
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement>
            {
                ["notebookName_0a1b2c3d"] = JsonSerializer.SerializeToElement(" Notebook A "),
                ["lifetime"] = JsonSerializer.SerializeToElement("untilRevoked"),
            },
        }, "notebookName_0a1b2c3d");
        Assert.Equal(new GrantPromptResult(GrantPromptStatus.Accepted, " Notebook A "), answer);
    }

    [Fact]
    public void WriteForm_TypedNameField_IsNewEveryRequest_AndAnyOtherKey_IsAMismatch()
    {
        var a = ElicitationGrantPrompt.NewNotebookNameField();
        var b = ElicitationGrantPrompt.NewNotebookNameField();
        Assert.Matches("^notebookName_[0-9a-f]{8}$", a);
        Assert.NotEqual(a, b);

        foreach (var key in new[] { ElicitationGrantPrompt.NotebookNameField, b, "sectionName_00000000" })
        {
            var answer = ElicitationGrantPrompt.ReadWrite(new ElicitResult
            {
                Action = "accept",
                Content = new Dictionary<string, JsonElement> { [key] = JsonSerializer.SerializeToElement("Notebook A") },
            }, a);
            Assert.Equal(GrantPromptStatus.Accepted, answer.Status);
            Assert.Null(answer.TypedName);
        }
    }

    private GrantCommand Command(string input, bool interactive = true) =>
        new(new StringReader(input), output, interactive, ct => gateway.GetHierarchyAsync(string.Empty, Core.HierarchyScope.Sections, ct), new SettingsWriter(Store, clock), clock);
}
