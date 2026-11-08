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
        Assert.Equal(["once", "hours", "untilRevoked"], lifetimes.OneOf.Select(o => o.Const));
        var hours = Assert.IsType<ElicitRequestParams.NumberSchema>(schema.Properties[ElicitationGrantPrompt.HoursField]);
        Assert.Equal((1d, 168d, 8d), (hours.Minimum, hours.Maximum, hours.Default));
        Assert.IsType<ElicitRequestParams.StringSchema>(schema.Properties["sectionName_0a1b2c3d"]);
        Assert.False(schema.Properties.ContainsKey(ElicitationGrantPrompt.SectionNameField));
        Assert.Equal("This section, for 5 minutes", lifetimes.OneOf[0].Title);
    }

    [Fact]
    public void Elicitation_Answers_MapToTheOutcome()
    {
        Assert.Equal(GrantPromptStatus.Declined, ElicitationGrantPrompt.Read(new ElicitResult { Action = "decline" }, "f").Status);
        Assert.Equal(GrantPromptStatus.Cancelled, ElicitationGrantPrompt.Read(new ElicitResult { Action = "cancel" }, "f").Status);

        var accepted = ElicitationGrantPrompt.Read(new ElicitResult
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement>
            {
                ["sectionName_0a1b2c3d"] = JsonSerializer.SerializeToElement("Vault"),
                ["lifetime"] = JsonSerializer.SerializeToElement("hours"),
                ["hours"] = JsonSerializer.SerializeToElement(12),
            },
        }, "sectionName_0a1b2c3d");
        Assert.Equal(new GrantPromptResult(GrantPromptStatus.Accepted, "Vault", ReadGrantLifetime.Hours, 12), accepted);

        // An unknown lifetime is never widened, and a fractional hour is out of range.
        var odd = ElicitationGrantPrompt.Read(new ElicitResult
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement>
            {
                ["sectionName_0a1b2c3d"] = JsonSerializer.SerializeToElement("Vault"),
                ["lifetime"] = JsonSerializer.SerializeToElement("forever"),
                ["hours"] = JsonSerializer.SerializeToElement(1.5),
            },
        }, "sectionName_0a1b2c3d");
        Assert.Equal((ReadGrantLifetime.Once, -1), (odd.Lifetime, odd.Hours));
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
