using System.Text.Json;
using OneNoteMcp.Server.Hooks;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// AD-6's plugin guard (story 1.20, plan pass 1): the PreToolUse decision for <c>OneNoteMcp.exe hook guard-settings</c>.
/// Writes to OneNoteMcp's <c>settings.json</c>, and shell commands naming it, are denied; everything else is allowed.
/// </summary>
public sealed class SettingsGuardTests
{
    private const string SettingsPath = @"C:\Users\someone\AppData\Roaming\OneNoteMcp\settings.json";

    private static string Input(string tool, object toolInput) =>
        JsonSerializer.Serialize(new { session_id = "s", hook_event_name = "PreToolUse", tool_name = tool, tool_input = toolInput });

    public static TheoryData<string, string> DeniedWrites() => new()
    {
        { "Edit", SettingsPath },
        { "Write", SettingsPath.ToUpperInvariant() },
        { "MultiEdit", SettingsPath.Replace('\\', '/') },
        { "Edit", @"C:\Users\someone\AppData\Roaming\OneNoteMcp\..\OneNoteMcp\settings.json" },
        { "Write", @"D:\elsewhere\onenotemcp\Settings.JSON" },
        { "Edit", "%APPDATA%\\OneNoteMcp\\settings.json" },
    };

    [Theory]
    [MemberData(nameof(DeniedWrites))]
    public void FileWrites_ToTheSettings_AreDenied(string tool, string path) =>
        Assert.Equal(SettingsGuard.Reason, SettingsGuard.Decide(Input(tool, new { file_path = path, content = "x" }), SettingsPath));

    public static TheoryData<string, string> DeniedFolderWrites() => new()
    {
        // Bug 1.22 S2: a file written into the settings folder could be renamed over settings.json.
        { "Write", @"C:\Users\someone\AppData\Roaming\OneNoteMcp\g.json" },
        { "Edit", "c:/users/someone/appdata/roaming/onenotemcp/notes.txt" },
        { "Write", "%APPDATA%\\OneNoteMcp\\g.json" },
        { "MultiEdit", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OneNoteMcp", "x.json") },
    };

    [Theory]
    [MemberData(nameof(DeniedFolderWrites))]
    public void FileWrites_IntoTheSettingsFolder_AreDenied(string tool, string path) =>
        Assert.Equal(SettingsGuard.Reason, SettingsGuard.Decide(Input(tool, new { file_path = path, content = "x" }), SettingsPath));

    [Theory]
    [InlineData("Bash", "cd \"$APPDATA/OneNoteMcp\" && mv g.json settings.json")]
    [InlineData("Bash", "cd $APPDATA/onenotemcp; mv g.json settings.json")]
    [InlineData("Bash", "mv /tmp/g.json \"${APPDATA}\"/OneNoteMcp/")]
    [InlineData("PowerShell", "Set-Location $env:APPDATA\\OneNoteMcp; Move-Item g.json settings.json -Force")]
    [InlineData("PowerShell", "Move-Item g.json \"$env:APPDATA/OneNoteMcp/\"")]
    [InlineData("Bash", "cmd /c \"move g.json %APPDATA%\\OneNoteMcp\"")]
    [InlineData("Bash", "cp g.json C:/Users/someone/AppData/Roaming/OneNoteMcp/")]
    [InlineData("PowerShell", "Copy-Item g.json C:\\Users\\someone\\AppData\\Roaming\\OneNoteMcp")]
    public void ShellCommands_NamingTheSettingsFolder_AreDenied(string tool, string command) =>
        Assert.Equal(SettingsGuard.Reason, SettingsGuard.Decide(Input(tool, new { command }), SettingsPath));

    [Theory]
    [InlineData("& \"C:\\Users\\x\\.claude\\plugins\\cache\\sentienceadjacent\\onenote\\0.8.0\\OneNoteMcp.exe\" --version")]
    [InlineData("OneNoteMcp.exe --version")]
    [InlineData("ls $LOCALAPPDATA/OneNoteMcp/logs")]
    [InlineData("ls $APPDATA/OneNoteMcpOld")]
    [InlineData("cat C:/Users/someone/AppData/Roaming/OneNoteMcp-backup/notes.txt")]
    public void ShellCommands_MentioningOneNoteMcp_WithoutTheSettingsFolder_AreAllowed(string command) =>
        Assert.Null(SettingsGuard.Decide(Input("Bash", new { command }), SettingsPath));

    [Fact]
    public void NotebookEdit_ByNotebookPath_IsDenied() =>
        Assert.NotNull(SettingsGuard.Decide(Input("NotebookEdit", new { notebook_path = SettingsPath }), SettingsPath));

    [Fact]
    public void AnOverriddenSettingsPath_IsDeniedToo()
    {
        const string overridden = @"D:\tmp\custom-settings.json";

        Assert.NotNull(SettingsGuard.Decide(Input("Write", new { file_path = overridden }), overridden));
    }

    [Theory]
    [InlineData("Bash", "echo {} > \"$APPDATA/OneNoteMcp/settings.json\"")]
    [InlineData("PowerShell", "Set-Content $env:APPDATA\\OneNoteMcp\\settings.json '{}'")]
    [InlineData("Bash", "python -c \"open(r'C:\\Users\\someone\\AppData\\Roaming\\OneNoteMcp\\settings.json','w')\"")]
    [InlineData("Bash", "cp x c:/users/someone/appdata/roaming/onenotemcp/SETTINGS.json")]
    public void ShellCommands_NamingTheSettings_AreDenied(string tool, string command) =>
        Assert.NotNull(SettingsGuard.Decide(Input(tool, new { command }), SettingsPath));

    [Theory]
    [InlineData("Bash", "OneNoteMcp grant --read-section \"Vault\"")]
    [InlineData("Bash", "onenotemcp.exe GRANT --revoke --read-section Vault")]
    [InlineData("PowerShell", "& \"C:\\Users\\x\\.claude\\plugins\\cache\\sentienceadjacent\\onenote\\0.7.0\\OneNoteMcp.exe\" grant --read-section \"Vault\"")]
    [InlineData("PowerShell", "& 'C:\\tools\\OneNoteMcp.exe' grant --revoke --read-section Vault")]
    [InlineData("Bash", "C:/tools/OneNoteMcp.exe grant --read-section Vault")]
    [InlineData("Bash", "echo 1 | OneNoteMcp grant --read-section Vault")]
    public void ShellCommands_RunningTheGrantCli_AreDenied_SayingOnlyTheUserRunsIt(string tool, string command) =>
        Assert.Equal(SettingsGuard.GrantReason, SettingsGuard.Decide(Input(tool, new { command }), SettingsPath));

    [Theory]
    [InlineData("git commit -m \"grant read access in OneNoteMcp\"")]
    [InlineData("dotnet test tests/OneNoteMcp.Tests --filter GrantCommandTests")]
    [InlineData("grep -rn grant src/OneNoteMcp.Server")]
    [InlineData("OneNoteMcp hook guard-settings")]
    public void ShellCommands_MerelyMentioningGrant_AreAllowed(string command) =>
        Assert.Null(SettingsGuard.Decide(Input("Bash", new { command }), SettingsPath));

    [Theory]
    [InlineData("Bash", "OneNoteMcp uninstall --purge --yes")]
    [InlineData("Bash", "onenotemcp.exe UNINSTALL --purge")]
    [InlineData("Bash", "OneNoteMcp uninstall")]
    [InlineData("PowerShell", "& \"C:\\Users\\x\\.claude\\plugins\\cache\\sentienceadjacent\\onenote\\0.8.0\\OneNoteMcp.exe\" uninstall --purge --yes")]
    [InlineData("PowerShell", "& 'C:\\tools\\OneNoteMcp.exe' uninstall --purge")]
    [InlineData("Bash", "C:/tools/OneNoteMcp.exe uninstall --purge --yes")]
    [InlineData("Bash", "echo y | OneNoteMcp uninstall --purge")]
    public void ShellCommands_RunningThePurge_AreDenied_SayingOnlyTheUserRunsIt(string tool, string command) =>
        Assert.Equal(SettingsGuard.PurgeReason, SettingsGuard.Decide(Input(tool, new { command }), SettingsPath));

    [Theory]
    [InlineData("echo uninstall")]
    [InlineData("cat D:/notes/uninstall/OneNoteMcp.md")]
    [InlineData("ls C:/tools/OneNoteMcp/uninstall-notes.txt")]
    [InlineData("dotnet test tests/OneNoteMcp.Tests --filter UninstallCommandTests")]
    [InlineData("git commit -m \"document OneNoteMcp's uninstall --purge\"")]
    public void ShellCommands_MerelyMentioningUninstall_AreAllowed(string command) =>
        Assert.Null(SettingsGuard.Decide(Input("Bash", new { command }), SettingsPath));

    public static TheoryData<string> Allowed() => new()
    {
        Input("Read", new { file_path = SettingsPath }),
        Input("Grep", new { pattern = "x", path = SettingsPath }),
        Input("Edit", new { file_path = @"C:\repo\.claude\settings.json" }),
        Input("Write", new { file_path = @"C:\repo\src\Settings\SettingsStore.cs" }),
        Input("Bash", new { command = "dotnet test" }),
        Input("Bash", new { command = "cat .claude/settings.json" }),
        Input("Edit", new { }),
        "not json",
        string.Empty,
        "[]",
        """{"tool_name": 3, "tool_input": {}}""",
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public void EverythingElse_IsAllowedSilently(string input) =>
        Assert.Null(SettingsGuard.Decide(input, SettingsPath));

    [Fact]
    public void DenyOutput_IsClaudeCodesPreToolUseDenyShape()
    {
        using var doc = JsonDocument.Parse(SettingsGuard.DenyOutput("why"));
        var output = doc.RootElement.GetProperty("hookSpecificOutput");

        Assert.Equal("PreToolUse", output.GetProperty("hookEventName").GetString());
        Assert.Equal("deny", output.GetProperty("permissionDecision").GetString());
        Assert.Equal("why", output.GetProperty("permissionDecisionReason").GetString());
    }
}
