using System.Text.Json;
using System.Text.RegularExpressions;

namespace OneNoteMcp.Server.Hooks;

/// <summary>
/// AD-6's plugin guard: "the plugin blocks the model from editing <c>settings.json</c>", where the user's grants live.
/// The plugin's <c>PreToolUse</c> hook runs <c>OneNoteMcp.exe hook guard-settings</c> with Claude Code's hook JSON on
/// stdin. It denies a file write (<c>Edit</c>, <c>Write</c>, <c>MultiEdit</c>, <c>NotebookEdit</c>) whose path is the
/// server's <c>settings.json</c>, or any <c>settings.json</c> in a folder named <c>OneNoteMcp</c>, ignoring case; and a
/// shell command (<c>Bash</c>, <c>PowerShell</c>) whose text names either. Everything else, reads and unreadable input
/// included, is allowed silently. It also denies a shell command that runs the grant CLI (<c>OneNoteMcp</c>,
/// <c>OneNoteMcp.exe</c> or a path ending in it, quoted or not, followed by <c>grant</c>): only the user runs it. Best
/// effort: an obfuscated shell command gets past it (AD-6 Limits; the README says so).
/// </summary>
public static partial class SettingsGuard
{
    public const string Reason =
        "OneNoteMcp's settings.json holds the user's grants, which only the user may change (AD-6). "
        + "Don't edit it, and don't run the OneNoteMcp grant command: tell the user what they would need to do instead.";

    public const string GrantReason =
        "Only the user runs the OneNoteMcp grant command, in their own terminal (AD-6, AD-21). "
        + "Don't run it, in any form: tell the user the command to run instead.";

    private static readonly HashSet<string> FileTools = new(StringComparer.Ordinal) { "Edit", "Write", "MultiEdit", "NotebookEdit" };
    private static readonly HashSet<string> ShellTools = new(StringComparer.Ordinal) { "Bash", "PowerShell" };

    /// <summary>The deny reason for <paramref name="hookInput"/>, or null to allow. Never throws.</summary>
    /// <param name="hookInput">Claude Code's PreToolUse JSON: <c>{tool_name, tool_input}</c>.</param>
    /// <param name="settingsPath">The server's resolved <c>settings.json</c> path (<c>SettingsStore.Path</c>).</param>
    public static string? Decide(string? hookInput, string settingsPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(hookInput ?? string.Empty);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tool_name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var tool = nameElement.GetString()!;
            if (FileTools.Contains(tool))
            {
                return IsSettingsPath(String(input, "file_path"), settingsPath) || IsSettingsPath(String(input, "notebook_path"), settingsPath) ? Reason : null;
            }

            if (ShellTools.Contains(tool))
            {
                var command = String(input, "command");
                return NamesSettings(command, settingsPath) ? Reason : RunsGrant(command) ? GrantReason : null;
            }

            return null;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The PreToolUse output that denies with <paramref name="reason"/>.</summary>
    public static string DenyOutput(string reason) => JsonSerializer.Serialize(new
    {
        hookSpecificOutput = new
        {
            hookEventName = "PreToolUse",
            permissionDecision = "deny",
            permissionDecisionReason = reason,
        },
    });

    /// <summary>True when <paramref name="path"/> resolves to <paramref name="settingsPath"/>, or to a settings.json in a OneNoteMcp folder.</summary>
    public static bool IsSettingsPath(string? path, string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = path;
        }

        if (string.Equals(full, Full(settingsPath), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(Path.GetFileName(full), "settings.json", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(full)), "OneNoteMcp", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when a shell command's text names the settings file: its full path, or <c>OneNoteMcp\settings.json</c> with either slash.</summary>
    public static bool NamesSettings(string? command, string settingsPath) =>
        !string.IsNullOrEmpty(command)
        && (command.Contains(Full(settingsPath), StringComparison.OrdinalIgnoreCase)
            || command.Contains(Full(settingsPath).Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || InOneNoteMcpFolder().IsMatch(command));

    /// <summary>
    /// True when a shell command runs the grant CLI: <c>OneNoteMcp</c> or <c>OneNoteMcp.exe</c>, bare or at the end of a
    /// path, quoted or not, then <c>grant</c>, ignoring case (so <c>grant --revoke</c> too).
    /// </summary>
    public static bool RunsGrant(string? command) => !string.IsNullOrEmpty(command) && GrantInvocation().IsMatch(command);

    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string? String(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"OneNoteMcp[\\/]+settings\.json", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InOneNoteMcpFolder();

    // Not preceded by a word character, dot or hyphen, so OneNoteMcp.Tests or my-OneNoteMcp don't count.
    [GeneratedRegex(@"(?<![\w.-])OneNoteMcp(?:\.exe)?[""']?\s+grant\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GrantInvocation();
}
