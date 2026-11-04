using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>AD-15/AD-19: the checked-in plugin source and marketplace entry stay consistent with the pack script's output.</summary>
public sealed class PluginPackagingTests
{
    private const string PluginManifest = "plugin/.claude-plugin/plugin.json";
    private const string McpConfig = "plugin/.mcp.json";
    private const string Marketplace = ".claude-plugin/marketplace.json";

    private static JsonElement ReadJson(string relativePath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoPaths.Combine(relativePath.Split('/'))));
        return doc.RootElement.Clone();
    }

    private static JsonElement SinglePluginEntry() =>
        Assert.Single(ReadJson(Marketplace).GetProperty("plugins").EnumerateArray());

    private static string PropsVersion() =>
        XDocument.Load(RepoPaths.Combine("Directory.Build.props")).Descendants("Version").Single().Value.Trim();

    [Fact]
    public void Checked_in_plugin_manifest_has_no_version()
    {
        // The pack script writes <Version> into the staged copy; a checked-in version would drift (research note, Release flow 2).
        Assert.False(ReadJson(PluginManifest).TryGetProperty("version", out _), $"{PluginManifest} must not carry a 'version'.");
    }

    [Fact]
    public void Marketplace_entry_has_no_version()
    {
        Assert.False(SinglePluginEntry().TryGetProperty("version", out _), $"{Marketplace} entry must not carry a 'version'.");
    }

    [Fact]
    public void Plugin_name_matches_marketplace_entry()
    {
        var name = ReadJson(PluginManifest).GetProperty("name").GetString();
        Assert.Equal("onenote-mcp", name);
        Assert.Equal(name, SinglePluginEntry().GetProperty("name").GetString());
        Assert.Equal("onenote-mcp", ReadJson(Marketplace).GetProperty("name").GetString());
    }

    [Fact]
    public void Mcp_config_runs_OneNoteMcp_exe_from_plugin_root()
    {
        var server = ReadJson(McpConfig).GetProperty("mcpServers").GetProperty("onenote");
        var command = server.GetProperty("command").GetString();
        Assert.NotNull(command);
        Assert.StartsWith("${CLAUDE_PLUGIN_ROOT}/", command, StringComparison.Ordinal);
        Assert.EndsWith("/OneNoteMcp.exe", command, StringComparison.Ordinal);
        Assert.DoesNotContain("/bin/", command, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Marketplace_entry_is_archive_for_props_version()
    {
        var ver = PropsVersion();
        var source = SinglePluginEntry().GetProperty("source");
        Assert.Equal("archive", source.GetProperty("source").GetString());

        var url = source.GetProperty("url").GetString();
        Assert.NotNull(url);
        Assert.StartsWith("https://", url, StringComparison.Ordinal);
        Assert.EndsWith($"/releases/download/v{ver}/onenote-{ver}-win-x64.zip", url, StringComparison.Ordinal);

        var sha = source.GetProperty("sha256").GetString();
        Assert.Matches("^[0-9a-f]{64}$", sha);
    }

    [Fact]
    public void Plugin_source_has_no_bin_folder()
    {
        Assert.False(Directory.Exists(RepoPaths.Combine("plugin", "bin")), "plugin/bin must not exist (AD-15).");
    }
}
