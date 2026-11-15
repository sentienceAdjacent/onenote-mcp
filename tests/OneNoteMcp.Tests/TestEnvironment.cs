using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Server.Logging;

namespace OneNoteMcp.Tests;

/// <summary>
/// Points every default location the server resolves from its environment (settings, journal, logs) at a temporary folder
/// for this test process, before any test runs, so no unit test can reach the user's real <c>%APPDATA%\OneNoteMcp</c> or
/// <c>%LOCALAPPDATA%\OneNoteMcp</c> (2026-11-13: a <c>SettingsStore(path)</c> with no journal folder fell back to the real
/// journal and wrote test entries there). A test that needs a specific location still passes its own.
/// </summary>
internal static class TestEnvironment
{
    /// <summary>This test process's root folder, under the system temp folder.</summary>
    public static string Root { get; } = Path.Combine(
        Path.GetTempPath(), "onenote-mcp-unit-tests", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "A test assembly: it must isolate the environment before any test runs.")]
    internal static void Isolate()
    {
        Environment.SetEnvironmentVariable(SettingsStore.PathOverrideVariable, Path.Combine(Root, "settings.json"));
        Environment.SetEnvironmentVariable(JournalFiles.DirectoryOverrideVariable, Path.Combine(Root, "journal"));
        Environment.SetEnvironmentVariable(FileLog.DirectoryOverrideVariable, Path.Combine(Root, "logs"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best effort: it's under the temp folder.
            }
            catch (UnauthorizedAccessException)
            {
            }
        };
    }
}
