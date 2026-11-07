using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>AD-14/AD-15/AD-19: the checked-in plugin source and release workflow stay consistent with the pack script's output.
/// The catalog lives in sentienceAdjacent/plugins, which pins our release zips by the asset name checked here.</summary>
public sealed class PluginPackagingTests
{
    private const string PluginManifest = "plugin/.claude-plugin/plugin.json";
    private const string McpConfig = "plugin/.mcp.json";
    private const string RemovedMarketplace = ".claude-plugin/marketplace.json";
    private const string ReleaseWorkflow = ".github/workflows/release.yml";
    private const string CiWorkflow = ".github/workflows/ci.yml";
    private const string PackScript = "scripts/pack-plugin.ps1";
    private const string RepoUrl = "https://github.com/sentienceAdjacent/onenote-mcp";

    private static readonly Regex ArchiveUrlVersion =
        new(@"/releases/download/v(?<ver>[^/]+)/onenote-\k<ver>-win-x64\.zip$", RegexOptions.CultureInvariant);

    private static string ReadText(string relativePath) => File.ReadAllText(RepoPaths.Combine(relativePath.Split('/')));

    private static JsonElement ReadJson(string relativePath)
    {
        using var doc = JsonDocument.Parse(ReadText(relativePath));
        return doc.RootElement.Clone();
    }

    private static string PropsVersion() =>
        XDocument.Load(RepoPaths.Combine("Directory.Build.props")).Descendants("Version").Single().Value.Trim();

    /// <summary>The release-asset URL suffix for a version, which the sentienceAdjacent/plugins catalog pins.</summary>
    private static string ArchiveUrlSuffix(string version) => $"/releases/download/v{version}/onenote-{version}-win-x64.zip";

    /// <summary>The version an archive URL names, or null when its tag and file name disagree.</summary>
    private static string? VersionOfArchiveUrl(string url)
    {
        var match = ArchiveUrlVersion.Match(url);
        return match.Success ? match.Groups["ver"].Value : null;
    }

    private static string WorkflowVersionPattern()
    {
        var match = Regex.Match(ReadText(ReleaseWorkflow), @"^\s*VERSION_PATTERN:\s*'(?<p>[^']+)'\s*$", RegexOptions.Multiline);
        Assert.True(match.Success, $"{ReleaseWorkflow} must declare VERSION_PATTERN.");
        return match.Groups["p"].Value;
    }

    [Fact]
    public void PluginManifest_HasNoVersion()
    {
        // The pack script writes <Version> into the staged copy; a checked-in version would drift (research note, Release flow 2).
        Assert.False(ReadJson(PluginManifest).TryGetProperty("version", out _), $"{PluginManifest} must not carry a 'version'.");
    }

    [Fact]
    public void PluginName_IsOnenote()
    {
        Assert.Equal("onenote", ReadJson(PluginManifest).GetProperty("name").GetString());
    }

    [Fact]
    public void McpConfig_DeclaresTheSingleServerDesktop()
    {
        var name = Assert.Single(ReadJson(McpConfig).GetProperty("mcpServers").EnumerateObject()).Name;
        Assert.Equal("desktop", name);
    }

    [Fact]
    public void Repo_IsNotAMarketplace()
    {
        // The catalog moved to sentienceAdjacent/plugins (AD-15).
        Assert.False(File.Exists(RepoPaths.Combine(RemovedMarketplace.Split('/'))), $"{RemovedMarketplace} must not exist in this repo.");
    }

    [Fact]
    public void McpConfig_RunsOneNoteMcpExeFromThePluginRoot()
    {
        var server = ReadJson(McpConfig).GetProperty("mcpServers").GetProperty("desktop");
        var command = server.GetProperty("command").GetString();
        Assert.NotNull(command);
        Assert.StartsWith("${CLAUDE_PLUGIN_ROOT}/", command, StringComparison.Ordinal);
        Assert.EndsWith("/OneNoteMcp.exe", command, StringComparison.Ordinal);
        Assert.DoesNotContain("/bin/", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hooks_GuardSettingsJson_WithThePluginsOwnExe()
    {
        // AD-6: the plugin blocks the model from editing settings.json. The pack script copies plugin/ as-is, so the staged
        // plugin carries hooks/hooks.json too.
        var preToolUse = Assert.Single(ReadJson("plugin/hooks/hooks.json").GetProperty("hooks").GetProperty("PreToolUse").EnumerateArray());
        Assert.Equal("Edit|Write|MultiEdit|NotebookEdit|Bash|PowerShell", preToolUse.GetProperty("matcher").GetString());
        var hook = Assert.Single(preToolUse.GetProperty("hooks").EnumerateArray());
        Assert.Equal("command", hook.GetProperty("type").GetString());
        Assert.Equal("\"${CLAUDE_PLUGIN_ROOT}/OneNoteMcp.exe\" hook guard-settings", hook.GetProperty("command").GetString());
        Assert.Contains("Copy-Item -Recurse -Force -Destination $stageDir", ReadText(PackScript), StringComparison.Ordinal);

        var staged = RepoPaths.Combine("dist", "stage", "onenote", "hooks", "hooks.json");
        if (Directory.Exists(RepoPaths.Combine("dist", "stage", "onenote")))
        {
            Assert.True(File.Exists(staged), "The staged plugin must carry hooks/hooks.json; rerun scripts/pack-plugin.ps1.");
        }
    }

    [Fact]
    public void ArchiveUrl_ForAPrereleaseVersion_IsTheExpectedUrl()
    {
        var url = RepoUrl + ArchiveUrlSuffix("0.4.0-rc.1");
        Assert.Equal("https://github.com/sentienceAdjacent/onenote-mcp/releases/download/v0.4.0-rc.1/onenote-0.4.0-rc.1-win-x64.zip", url);
        Assert.Equal("0.4.0-rc.1", VersionOfArchiveUrl(url));
    }

    [Theory]
    [InlineData("0.4.0")]
    [InlineData("0.4.0-rc.1")]
    [InlineData("1.12.3-beta-2.7")]
    public void ArchiveUrl_RoundTripsTheVersion(string version)
    {
        Assert.Equal(version, VersionOfArchiveUrl(RepoUrl + ArchiveUrlSuffix(version)));
    }

    [Theory]
    [InlineData(RepoUrl + "/releases/download/v0.4.0-rc.1/onenote-0.4.0-rc.2-win-x64.zip")]
    [InlineData(RepoUrl + "/releases/download/0.4.0/onenote-0.4.0-win-x64.zip")]
    [InlineData(RepoUrl + "/releases/download/v0.4.0/onenote-0.4.0.zip")]
    public void ArchiveUrl_WhoseTagAndFileDisagree_HasNoVersion(string url)
    {
        Assert.Null(VersionOfArchiveUrl(url));
    }

    [Fact]
    public void Props_FitTheReleaseTagPattern()
    {
        Assert.Matches(WorkflowVersionPattern(), PropsVersion());
    }

    [Fact]
    public void PackScript_UsesTheWorkflowVersionPattern()
    {
        var match = Regex.Match(ReadText(PackScript), @"^\$versionPattern\s*=\s*'(?<p>[^']+)'\s*$", RegexOptions.Multiline);
        Assert.True(match.Success, $"{PackScript} must declare $versionPattern.");
        Assert.Equal(WorkflowVersionPattern(), match.Groups["p"].Value);
    }

    [Theory]
    [InlineData("0.4.0", true)]
    [InlineData("0.4.0-rc.1", true)]
    [InlineData("10.20.30-alpha-1.0", true)]
    [InlineData("0.4", false)]
    [InlineData("v0.4.0", false)]
    [InlineData("01.4.0", false)]
    [InlineData("0.4.0+abc", false)]
    [InlineData("0.4.0-", false)]
    [InlineData("0.4.0-rc..1", false)]
    [InlineData("0.4.0-rc.01", false)]
    [InlineData("0.4.0-0rc.1", true)]
    public void VersionPattern_AcceptsSemverWithAnOptionalPrerelease(string version, bool accepted)
    {
        Assert.Equal(accepted, Regex.IsMatch(version, WorkflowVersionPattern()));
    }

    [Fact]
    public void ReleaseWorkflow_RefusesATagOtherThanVPropsVersion_BeforeBuilding()
    {
        var yaml = ReadText(ReleaseWorkflow).ReplaceLineEndings("\n");
        Assert.Matches(@"(?m)^on:\n\s+push:\n\s+tags:\s*\['v\*'\]\s*$", yaml);

        // The check reads <Version> from the props and compares the tag, case-sensitively, with "v<Version>".
        var check = yaml.IndexOf("name: Check tag against <Version>", StringComparison.Ordinal);
        Assert.True(check >= 0, $"{ReleaseWorkflow} must have the 'Check tag against <Version>' step.");
        Assert.Contains("$tag = $env:GITHUB_REF_NAME", yaml, StringComparison.Ordinal);
        Assert.Contains("Get-Content -Raw Directory.Build.props", yaml, StringComparison.Ordinal);
        Assert.Contains("SelectSingleNode('/Project/PropertyGroup/Version')", yaml, StringComparison.Ordinal);
        Assert.Contains("$version -cnotmatch $env:VERSION_PATTERN", yaml, StringComparison.Ordinal);
        Assert.Contains("$tag -cne \"v$version\"", yaml, StringComparison.Ordinal);

        // Nothing is restored, built or packed before the check.
        foreach (var step in new[] { "name: Restore", "name: Build", "name: Pack plugin" })
        {
            Assert.True(yaml.IndexOf(step, StringComparison.Ordinal) > check, $"'{step}' must come after the tag check.");
        }
    }

    [Fact]
    public void ReleaseWorkflow_RunsEveryCiCheck_WithTheSamePins()
    {
        var release = ReadText(ReleaseWorkflow);
        var ci = ReadText(CiWorkflow);
        foreach (var line in new[]
        {
            "dotnet build OneNoteMcp.slnx --no-restore -c Release -warnaserror",
            "dotnet test tests/OneNoteMcp.Tests --no-build -c Release",
            "dotnet test tests/OneNoteMcp.Integration --no-build -c Release",
            "pwsh -NoProfile -File scripts/pack-plugin.ps1",
        })
        {
            Assert.Contains(line, ci, StringComparison.Ordinal);
            Assert.Contains(line, release, StringComparison.Ordinal);
        }

        // The repo root is no longer a marketplace, so neither workflow validates it as one.
        foreach (var yaml in new[] { ci, release })
        {
            Assert.Matches(@"(?m)claude plugin validate --strict dist/stage/onenote\s*$", yaml);
            Assert.DoesNotMatch(@"(?m)claude plugin validate --strict \.\s*$", yaml);
        }

        var pin = new Regex(@"@anthropic-ai/claude-code@(?<v>\S+)");
        var releasePin = pin.Match(release).Groups["v"].Value;
        Assert.NotEqual("", releasePin);
        Assert.Equal(pin.Match(ci).Groups["v"].Value, releasePin);

        foreach (var key in new[] { "dotnet-version", "node-version" })
        {
            var setting = new Regex($@"{key}:\s*'(?<v>[^']+)'");
            var releaseValue = setting.Match(release).Groups["v"].Value;
            Assert.NotEqual("", releaseValue);
            Assert.Equal(setting.Match(ci).Groups["v"].Value, releaseValue);
        }

        // Every action ci.yml uses is used by release.yml at the same version.
        var action = new Regex(@"uses:\s*(?<name>actions/[\w-]+)@(?<v>\S+)");
        var ciActions = action.Matches(ci).ToDictionary(m => m.Groups["name"].Value, m => m.Groups["v"].Value);
        Assert.NotEmpty(ciActions);
        foreach (var (name, version) in ciActions)
        {
            var used = action.Matches(release).Where(m => m.Groups["name"].Value == name).Select(m => m.Groups["v"].Value).ToList();
            Assert.True(used.Count > 0, $"{ReleaseWorkflow} must use {name} like {CiWorkflow}.");
            Assert.All(used, v => Assert.Equal(version, v));
        }
    }

    [Fact]
    public void ReleaseWorkflow_GrantsContentsWriteToOneJobOnly()
    {
        var yaml = ReadText(ReleaseWorkflow);
        Assert.Matches(@"(?m)^permissions:\s*\{\}\s*$", yaml);
        Assert.Single(Regex.Matches(yaml, @"(?m)^\s+contents:\s*write\s*$"));
    }

    private const string GuideSkill = "plugin/skills/guide/SKILL.md";
    private const string ToolContract = "tests/OneNoteMcp.Tests/Contract/tool-contract.json";
    private const string ToolPrefix = "mcp__plugin_onenote_desktop__";

    private static IReadOnlyList<string> ContractToolNames() =>
        ReadJson(ToolContract).GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();

    [Fact]
    public void GuideSkill_HasFrontmatterNamedGuide()
    {
        var lines = ReadText(GuideSkill).ReplaceLineEndings("\n").Split('\n');
        Assert.Equal("---", lines[0]);
        var end = Array.IndexOf(lines, "---", 1);
        Assert.True(end > 0, $"{GuideSkill} must close its YAML frontmatter with '---'.");

        var frontmatter = lines[1..end];
        Assert.Contains("name: guide", frontmatter);
        var description = Assert.Single(frontmatter, l => l.StartsWith("description:", StringComparison.Ordinal));
        Assert.NotEqual("", description["description:".Length..].Trim());
    }

    [Fact]
    public void GuideSkill_MentionsEveryContractTool()
    {
        // AD-14: a tool added to the contract snapshot fails here until the skill covers it.
        // Whole names only, so get_hierarchy isn't satisfied by get_hierarchy_parent.
        var named = NamedTools();
        var names = ContractToolNames();
        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.True(named.Contains(name), $"{GuideSkill} must name {ToolPrefix}{name}."));
    }

    [Fact]
    public void GuideSkill_NamesOnlyContractTools()
    {
        var names = ContractToolNames().ToHashSet(StringComparer.Ordinal);
        var named = NamedTools();
        Assert.NotEmpty(named);
        Assert.All(named, name => Assert.True(names.Contains(name), $"{GuideSkill} names '{ToolPrefix}{name}', which is not in the tool contract."));
    }

    /// <summary>Every whole tool name the skill writes after <see cref="ToolPrefix"/>.</summary>
    private static HashSet<string> NamedTools() =>
        Regex.Matches(ReadText(GuideSkill), Regex.Escape(ToolPrefix) + @"(?<name>\w+)")
            .Select(m => m.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void PluginSource_HasNoBinFolder()
    {
        Assert.False(Directory.Exists(RepoPaths.Combine("plugin", "bin")), "plugin/bin must not exist (AD-15).");
    }
}
