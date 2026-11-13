using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Server.Logging;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// No unit test may reach the user's real settings, journal or logs (2026-11-13: a store built without a journal folder
/// fell back to the real one and wrote test entries there). <see cref="TestEnvironment"/> points every default at a
/// temporary folder before any test runs.
/// </summary>
public sealed class TestEnvironmentTests
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    [Fact]
    public void ADefaultJournal_IsUnderTheTestRoot_NeverTheUsersFolder()
    {
        var store = new SettingsStore(Path.Combine(TestEnvironment.Root, "unused-settings.json"));

        Assert.StartsWith(TestEnvironment.Root, store.DefaultJournal.Path, StringComparison.OrdinalIgnoreCase);
        Assert.False(store.DefaultJournal.Path.StartsWith(Path.Combine(LocalAppData, "OneNoteMcp"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheDefaultSettingsPathAndLogFolder_AreUnderTheTestRoot()
    {
        var settings = SettingsStore.ResolvePath(Environment.GetEnvironmentVariable);
        var logs = FileLog.ResolveDirectory(Environment.GetEnvironmentVariable);

        Assert.StartsWith(TestEnvironment.Root, settings, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(TestEnvironment.Root, logs, StringComparison.OrdinalIgnoreCase);
        Assert.False(settings.StartsWith(Path.Combine(AppData, "OneNoteMcp"), StringComparison.OrdinalIgnoreCase));
        Assert.False(JournalFiles.DefaultFolder(Environment.GetEnvironmentVariable).StartsWith(Path.Combine(LocalAppData, "OneNoteMcp"), StringComparison.OrdinalIgnoreCase));
    }
}
