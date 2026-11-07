namespace OneNoteMcp.Core.Access;

/// <summary>
/// How a userAction names the grant command (AD-6, AD-21). A plugin install keeps <c>OneNoteMcp.exe</c> in the plugin's
/// own folder, not on PATH, so when this process is <c>OneNoteMcp.exe</c> the command is its full path, quoted; otherwise
/// (tests, a build run another way) it is the bare <c>OneNoteMcp</c>.
/// </summary>
public static class GrantCli
{
    public const string ExeName = "OneNoteMcp.exe";

    /// <summary>The command for this process: the quoted exe path, or <c>OneNoteMcp</c>.</summary>
    public static string Command { get; } = Resolve(Environment.ProcessPath);

    /// <summary>The command for <paramref name="processPath"/>: quoted when it is <c>OneNoteMcp.exe</c>, else <c>OneNoteMcp</c>.</summary>
    public static string Resolve(string? processPath) =>
        !string.IsNullOrEmpty(processPath) && string.Equals(Path.GetFileName(processPath), ExeName, StringComparison.OrdinalIgnoreCase)
            ? $"\"{processPath}\""
            : "OneNoteMcp";

    /// <summary>
    /// The text that tells the user to run <c>grant</c> with <paramref name="arguments"/> in a terminal, for
    /// <paramref name="command"/> (default: this process's). A quoted path gets PowerShell's <c>&amp;</c> note.
    /// </summary>
    public static string Run(string arguments, string? command = null)
    {
        var c = command ?? Command;
        var line = $"`{c} grant {arguments}`";
        return c.StartsWith('"') ? $"{line} in a terminal (in PowerShell, put & and a space before it)" : $"{line} in a terminal";
    }
}
