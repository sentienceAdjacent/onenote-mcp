using System.Text.Json;
using ModelContextProtocol.Protocol;
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

    private SettingsStore Store => new(temp.Combine("settings.json"));

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

    [Theory]
    [InlineData("--write", "--revoke")]
    [InlineData("--write", "--read-section")]
    public async Task Write_WithRevokeOrReadSection_IsBadUsage(string a, string b)
    {
        Assert.Equal(2, await WriteCommand("yes\n").RunAsync([a, b], Ct));
        Assert.False(File.Exists(Store.Path));
    }

    [Fact]
    public void Usage_NamesWrite() => Assert.Contains("grant --write", GrantCommand.Usage, StringComparison.Ordinal);

    private GrantCommand WriteCommand(string input, bool interactive = true) =>
        new(new StringReader(input), output, interactive, _ =>
        {
            notebookReads++;
            return Task.FromResult(NotebooksXml);
        }, new SettingsWriter(Store, clock), clock);

    // --- Elicitation adapter --------------------------------------------------------------------------------------

    [Fact]
    public void Elicitation_Form_NamesSectionAndNotebook_TypedNameRequired_ThreeLifetimes_Hours1To168()
    {
        var request = ElicitationGrantPrompt.Request(new GrantQuestion("{S}", "Vault", "Notebook A", ["Group"]), "sectionName_0a1b2c3d");

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
        var request = ElicitationGrantPrompt.Request(new GrantQuestion("{S}", "Vault", "Notebook A", [], minutes), "sectionName_00000000");
        var lifetimes = Assert.IsType<ElicitRequestParams.TitledSingleSelectEnumSchema>(request.RequestedSchema!.Properties[ElicitationGrantPrompt.LifetimeField]);
        Assert.Equal(title, lifetimes.OneOf.Single(o => o.Const == ReadGrantLifetimes.Once).Title);
    }

    [Fact]
    public void Elicitation_NoServer_NoPrompt() => Assert.Null(ElicitationGrantPrompt.For(null));

    private GrantCommand Command(string input, bool interactive = true) =>
        new(new StringReader(input), output, interactive, ct => gateway.GetHierarchyAsync(string.Empty, Core.HierarchyScope.Sections, ct), new SettingsWriter(Store, clock), clock);
}
