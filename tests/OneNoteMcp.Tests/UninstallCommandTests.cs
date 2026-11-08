using System.Text.Json;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Server.Cli;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 1.13: <c>OneNoteMcp uninstall --purge</c> (AD-19), every row of the plan's I/O matrix, on temp folders laid out
/// like <c>%APPDATA%\OneNoteMcp</c> and <c>%LOCALAPPDATA%\OneNoteMcp</c>. Nothing here touches the real profile.
/// </summary>
public sealed class UninstallCommandTests : IDisposable
{
    private const string JournalName = "20261107T101112123-4242-0123456789abcdef0123456789abcdef.json.gz";
    private const string JournalNameD = "20261107T101112124-77-01234567-89ab-cdef-0123-456789abcdef.json.gz";

    private readonly TempDirectory temp = new();
    private readonly StringWriter output = new();

    public UninstallCommandTests()
    {
        Directory.CreateDirectory(SettingsFolder);
    }

    private string SettingsFolder => temp.Combine(Path.Combine("Roaming", "OneNoteMcp"));

    private string SettingsPath => Path.Combine(SettingsFolder, "settings.json");

    private string LocalFolder => temp.Combine(Path.Combine("Local", "OneNoteMcp"));

    private string LogFolder => Path.Combine(LocalFolder, "logs");

    private string JournalFolder => Path.Combine(LocalFolder, "journal");

    public void Dispose()
    {
        output.Dispose();
        temp.Dispose();
    }

    private UninstallCommand Command(string input = "", bool interactive = false, IReadOnlyList<string>? systemFolders = null) =>
        new(new StringReader(input), output, interactive, new UninstallPaths(SettingsPath, LogFolder, JournalFolder, systemFolders ?? []));

    private static string Touch(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private void WriteSettings(string? journalPath = null)
    {
        var root = new Dictionary<string, object> { ["schemaVersion"] = 1 };
        if (journalPath is not null)
        {
            root["journal"] = new Dictionary<string, object> { ["path"] = journalPath };
        }

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(root));
    }

    /// <summary>Settings, a writer leftover, logs (one rolled) and journal entries in both GUID formats.</summary>
    private void SeedEverything()
    {
        WriteSettings();
        Touch(SettingsFolder, ".settings.1234.0123456789abcdef0123456789abcdef.tmp");
        Touch(LogFolder, "onenote-mcp-20261107T101112-4242.log");
        Touch(LogFolder, "onenote-mcp-20261107T101112-4242_001.log");
        Touch(JournalFolder, JournalName);
        Touch(JournalFolder, JournalNameD);
    }

    [Fact]
    public void Purge_RemovesOwnFilesAndTheirEmptyFolders_Exit0()
    {
        SeedEverything();

        var code = Command().Run(["--purge", "--yes"]);

        Assert.Equal(0, code);
        Assert.False(Directory.Exists(SettingsFolder));
        Assert.False(Directory.Exists(LogFolder));
        Assert.False(Directory.Exists(JournalFolder));
        Assert.False(Directory.Exists(LocalFolder), "the emptied OneNoteMcp folder holding logs and journal goes too");
        Assert.True(Directory.Exists(temp.Combine("Local")), "the folder above OneNoteMcp is never removed");
        Assert.True(Directory.Exists(temp.Combine("Roaming")));
        var text = output.ToString();
        Assert.Contains($"Removed {SettingsPath}", text, StringComparison.Ordinal);
        Assert.Contains($"Removed {Path.Combine(JournalFolder, JournalName)}", text, StringComparison.Ordinal);
        Assert.Contains("Done.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Interactive_TypingPurge_Removes()
    {
        SeedEverything();

        var code = Command("purge\n", interactive: true).Run(["--purge"]);

        Assert.Equal(0, code);
        Assert.Contains("Type purge", output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(SettingsPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("yes\n")]
    [InlineData("PURGE\n")]
    public void Interactive_AnythingButPurge_RemovesNothing_Exit1(string answer)
    {
        SeedEverything();

        var code = Command(answer, interactive: true).Run(["--purge"]);

        Assert.Equal(1, code);
        Assert.Contains("Nothing removed.", output.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(SettingsPath));
        Assert.Equal(2, Directory.GetFiles(JournalFolder).Length);
    }

    [Fact]
    public void NotInteractive_WithoutYes_RemovesNothing_Exit2()
    {
        SeedEverything();

        var code = Command().Run(["--purge"]);

        Assert.Equal(2, code);
        Assert.Contains("--yes", output.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(SettingsPath));
        Assert.Equal(2, Directory.GetFiles(LogFolder).Length);
    }

    [Fact]
    public void ForeignFiles_AreKept_WithTheirFolders()
    {
        SeedEverything();
        var notes = Touch(JournalFolder, "notes.txt");
        var nearMiss = Touch(JournalFolder, "20261107T101112123-4242-not-a-guid.json.gz");
        var nested = Touch(Path.Combine(JournalFolder, "sub"), JournalName);
        var otherLog = Touch(LogFolder, "other.log");
        var otherSettings = Touch(SettingsFolder, "settings.json.bak");

        var code = Command().Run(["--purge", "--yes"]);

        Assert.Equal(0, code);
        Assert.All(new[] { notes, nearMiss, nested, otherLog, otherSettings }, f => Assert.True(File.Exists(f), $"{f} must be kept"));
        Assert.False(File.Exists(Path.Combine(JournalFolder, JournalName)));
        Assert.False(File.Exists(SettingsPath));
        Assert.Empty(Directory.GetFiles(LogFolder, "onenote-mcp-*"));
        Assert.Contains($"Kept {JournalFolder}", output.ToString(), StringComparison.Ordinal);
        Assert.True(Directory.Exists(LocalFolder));
    }

    public static TheoryData<string> RootLikeJournals() => new()
    {
        Path.GetPathRoot(Path.GetTempPath())!,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    };

    [Theory]
    [MemberData(nameof(RootLikeJournals))]
    public void RootLikeJournal_IsRefused_SettingsAndLogsStillPurged_Exit1(string journalPath)
    {
        WriteSettings(journalPath);
        Touch(LogFolder, "onenote-mcp-20261107T101112-4242.log");

        var code = Command(systemFolders: UninstallPaths.DefaultSystemFolders()).Run(["--purge", "--yes"]);

        Assert.Equal(1, code);
        var text = output.ToString();
        Assert.Contains($"Refused: {Path.GetFullPath(journalPath)}", text, StringComparison.Ordinal);
        Assert.False(File.Exists(SettingsPath));
        Assert.False(Directory.Exists(LogFolder));
        Assert.True(Directory.Exists(journalPath));
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"C:\OneNoteMcp", true)]
    [InlineData(@"C:\OneNoteMcp\journal\", false)]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\server\share\journal", true)]
    [InlineData(@"\\server\share\OneNoteMcp\journal", false)]
    [InlineData(@"D:\Backups\Journal", false)]
    public void RootLikeReason_ByDepth(string path, bool refused) =>
        Assert.Equal(refused, UninstallCommand.RootLikeReason(path, []) is not null);

    [Theory]
    [InlineData(Environment.SpecialFolder.UserProfile)]
    [InlineData(Environment.SpecialFolder.ApplicationData)]
    [InlineData(Environment.SpecialFolder.LocalApplicationData)]
    [InlineData(Environment.SpecialFolder.DesktopDirectory)]
    [InlineData(Environment.SpecialFolder.MyDocuments)]
    [InlineData(Environment.SpecialFolder.Windows)]
    [InlineData(Environment.SpecialFolder.ProgramFiles)]
    public void RootLikeReason_SystemFolders(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        Assert.NotNull(UninstallCommand.RootLikeReason(path + Path.DirectorySeparatorChar, UninstallPaths.DefaultSystemFolders()));
        Assert.Null(UninstallCommand.RootLikeReason(Path.Combine(path, "OneNoteMcp", "journal"), UninstallPaths.DefaultSystemFolders()));
    }

    [Fact]
    public void NothingThere_SaysSo_Exit0()
    {
        Directory.Delete(SettingsFolder);

        var code = Command().Run(["--purge", "--yes"]);

        Assert.Equal(0, code);
        Assert.Contains("Nothing to remove.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidSettings_FileStillRemoved_JournalUsesTheDefault_AndSaysSo()
    {
        File.WriteAllText(SettingsPath, "{ not json");
        Touch(JournalFolder, JournalName);

        var code = Command().Run(["--purge", "--yes"]);

        Assert.Equal(0, code);
        Assert.False(File.Exists(SettingsPath));
        Assert.False(Directory.Exists(JournalFolder));
        Assert.Contains($"the journal folder checked is the default: {JournalFolder}", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NewerSchemaSettings_FileStillRemoved_JournalUsesTheDefault_AndSaysSo()
    {
        var custom = temp.Combine(Path.Combine("Elsewhere", "journal"));
        var customEntry = Touch(custom, JournalName);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["schemaVersion"] = SettingsSnapshot.KnownSchemaVersion + 1,
            ["journal"] = new Dictionary<string, object> { ["path"] = custom },
        }));
        Touch(JournalFolder, JournalName);

        var code = Command().Run(["--purge", "--yes"]);

        Assert.Equal(0, code);
        Assert.False(File.Exists(SettingsPath));
        Assert.False(Directory.Exists(JournalFolder));
        Assert.True(File.Exists(customEntry), "a newer schema's journal.path isn't trusted; the default folder is purged instead");
        Assert.Contains($"settings.json is from a newer OneNoteMcp, so the journal folder checked is the default: {JournalFolder}", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void JournalPathFromSettings_IsTheOnePurged()
    {
        var custom = temp.Combine(Path.Combine("Elsewhere", "journal"));
        WriteSettings(custom);
        Touch(custom, JournalName);
        var defaultEntry = Touch(JournalFolder, JournalName);

        var code = Command().Run(["--purge", "--yes"]);

        Assert.Equal(0, code);
        Assert.False(Directory.Exists(custom));
        Assert.True(Directory.Exists(temp.Combine("Elsewhere")), "a parent not named OneNoteMcp is never removed");
        Assert.True(File.Exists(defaultEntry), "the default folder isn't the journal when settings name another");
    }

    [Theory]
    [InlineData]
    [InlineData("--yes")]
    [InlineData("--purge", "--bogus")]
    [InlineData("purge")]
    public void BadUsage_PrintsUsage_RemovesNothing_Exit2(params string[] args)
    {
        SeedEverything();

        var code = Command("purge\n", interactive: true).Run(args);

        Assert.Equal(2, code);
        Assert.Contains("Usage:", output.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(SettingsPath));
        Assert.Equal(2, Directory.GetFiles(JournalFolder).Length);
    }

    [Fact]
    public void LockedLog_OthersRemoved_ThatOneReported_Exit1()
    {
        SeedEverything();
        var locked = Path.Combine(LogFolder, "onenote-mcp-20261107T101112-4242.log");

        int code;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            code = Command().Run(["--purge", "--yes"]);
        }

        Assert.Equal(1, code);
        var text = output.ToString();
        Assert.Contains($"Couldn't remove {locked}", text, StringComparison.Ordinal);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(SettingsPath));
        Assert.False(Directory.Exists(JournalFolder));
        Assert.True(Directory.Exists(LogFolder));
    }
}
