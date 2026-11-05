using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>AD-14/AD-15/AD-19: the checked-in plugin source, marketplace entry and release workflow stay consistent with the pack script's output.</summary>
public sealed class PluginPackagingTests
{
    private const string PluginManifest = "plugin/.claude-plugin/plugin.json";
    private const string McpConfig = "plugin/.mcp.json";
    private const string Marketplace = ".claude-plugin/marketplace.json";
    private const string ReleaseWorkflow = ".github/workflows/release.yml";
    private const string CiWorkflow = ".github/workflows/ci.yml";
    private const string PackScript = "scripts/pack-plugin.ps1";
    private const string RepoUrl = "https://github.com/sentienceAdjacent/onenote-mcp";
    private const string NoReleaseYetSha = "0000000000000000000000000000000000000000000000000000000000000000";

    private static readonly Regex ArchiveUrlVersion =
        new(@"/releases/download/v(?<ver>[^/]+)/onenote-\k<ver>-win-x64\.zip$", RegexOptions.CultureInvariant);

    private static string ReadText(string relativePath) => File.ReadAllText(RepoPaths.Combine(relativePath.Split('/')));

    private static JsonElement ReadJson(string relativePath)
    {
        using var doc = JsonDocument.Parse(ReadText(relativePath));
        return doc.RootElement.Clone();
    }

    private static JsonElement SinglePluginEntry() =>
        Assert.Single(ReadJson(Marketplace).GetProperty("plugins").EnumerateArray());

    private static string PropsVersion() =>
        XDocument.Load(RepoPaths.Combine("Directory.Build.props")).Descendants("Version").Single().Value.Trim();

    /// <summary>The URL suffix the release workflow pins for a version (release.yml, "Pin marketplace.json").</summary>
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
    public void MarketplaceEntry_HasNoVersion()
    {
        Assert.False(SinglePluginEntry().TryGetProperty("version", out _), $"{Marketplace} entry must not carry a 'version'.");
    }

    [Fact]
    public void PluginName_MatchesTheMarketplaceEntry()
    {
        var name = ReadJson(PluginManifest).GetProperty("name").GetString();
        Assert.Equal("onenote-mcp", name);
        Assert.Equal(name, SinglePluginEntry().GetProperty("name").GetString());
        Assert.Equal("onenote-mcp", ReadJson(Marketplace).GetProperty("name").GetString());
    }

    [Fact]
    public void McpConfig_RunsOneNoteMcpExeFromThePluginRoot()
    {
        var server = ReadJson(McpConfig).GetProperty("mcpServers").GetProperty("onenote");
        var command = server.GetProperty("command").GetString();
        Assert.NotNull(command);
        Assert.StartsWith("${CLAUDE_PLUGIN_ROOT}/", command, StringComparison.Ordinal);
        Assert.EndsWith("/OneNoteMcp.exe", command, StringComparison.Ordinal);
        Assert.DoesNotContain("/bin/", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MarketplaceEntry_IsAnArchiveOfAReleaseNoNewerThanTheProps()
    {
        // The release workflow commits url and sha256 to main after publishing, so the tagged commit and any
        // later <Version> bump carry the previous release's values: the catalog may lag the props, never lead them.
        var props = PropsVersion();
        var source = SinglePluginEntry().GetProperty("source");
        Assert.Equal("archive", source.GetProperty("source").GetString());

        var url = source.GetProperty("url").GetString();
        Assert.NotNull(url);
        Assert.StartsWith(RepoUrl + "/", url, StringComparison.Ordinal);
        var pinned = VersionOfArchiveUrl(url);
        Assert.True(pinned is not null, $"'{url}' must end with {ArchiveUrlSuffix("<ver>")}, the same <ver> twice.");
        Assert.Matches(WorkflowVersionPattern(), pinned);

        var sha = source.GetProperty("sha256").GetString();
        Assert.Matches("^[0-9a-f]{64}$", sha);

        // All zeros means no release has been published yet; the first release replaces it.
        if (sha != NoReleaseYetSha)
        {
            Assert.True(SemVer.Compare(pinned, props) <= 0, $"{Marketplace} pins {pinned}, newer than <Version> {props}.");
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
            "claude plugin validate --strict .",
            "claude plugin validate --strict dist/stage/onenote-mcp",
        })
        {
            Assert.Contains(line, ci, StringComparison.Ordinal);
            Assert.Contains(line, release, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("0.4.0-rc.1", "0.4.0-rc.2", -1)]
    [InlineData("0.4.0-rc.2", "0.4.0-rc.10", -1)]
    [InlineData("0.4.0-rc.1", "0.4.0", -1)]
    [InlineData("0.4.0", "0.4.0", 0)]
    [InlineData("0.4.1", "0.4.0", 1)]
    [InlineData("0.10.0", "0.9.9", 1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("1.0.0-alpha.beta", "1.0.0-alpha.1", 1)]
    public void SemVer_ComparesByPrecedence(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(SemVer.Compare(a, b)));
    }

    [Fact]
    public void PluginSource_HasNoBinFolder()
    {
        Assert.False(Directory.Exists(RepoPaths.Combine("plugin", "bin")), "plugin/bin must not exist (AD-15).");
    }

    /// <summary>Semver 2.0 precedence for major.minor.patch[-prerelease]; enough for the catalog's lag check.</summary>
    private static class SemVer
    {
        public static int Compare(string a, string b)
        {
            var (coreA, preA) = Split(a);
            var (coreB, preB) = Split(b);
            for (var i = 0; i < 3; i++)
            {
                var c = coreA[i].CompareTo(coreB[i]);
                if (c != 0) return c;
            }

            // A release outranks its prereleases.
            if (preA.Length == 0 || preB.Length == 0) return preB.Length.CompareTo(preA.Length);
            for (var i = 0; i < Math.Min(preA.Length, preB.Length); i++)
            {
                var c = ComparePart(preA[i], preB[i]);
                if (c != 0) return c;
            }

            return preA.Length.CompareTo(preB.Length);
        }

        private static (long[] Core, string[] Pre) Split(string version)
        {
            var dash = version.IndexOf('-', StringComparison.Ordinal);
            var core = (dash < 0 ? version : version[..dash]).Split('.').Select(long.Parse).ToArray();
            string[] pre = dash < 0 ? [] : version[(dash + 1)..].Split('.');
            return (core, pre);
        }

        private static int ComparePart(string a, string b)
        {
            var aNumeric = long.TryParse(a, out var x);
            var bNumeric = long.TryParse(b, out var y);
            if (aNumeric && bNumeric) return x.CompareTo(y);
            // Numeric identifiers rank below alphanumeric ones.
            if (aNumeric != bNumeric) return aNumeric ? -1 : 1;
            return string.CompareOrdinal(a, b);
        }
    }
}
