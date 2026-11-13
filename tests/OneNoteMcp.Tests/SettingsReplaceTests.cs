using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Settings;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Bug 2.15 (found in story 2.6): <c>SettingsWriter</c> replaces <c>settings.json</c> with a short retry, so a file another
/// program holds for a moment doesn't fail a grant save, and a replace that fails after removing the old file
/// (<c>ERROR_UNABLE_TO_MOVE_REPLACEMENT</c>, 1176) never loses the new content.
/// </summary>
public sealed class SettingsReplaceTests : IDisposable
{
    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();

    private string SettingsPath => temp.Combine("settings.json");

    public void Dispose() => temp.Dispose();

    private NotebookGrant Grant() => new(@"C:\nb\A", "A", "{NB}{1}{B0}", true, false, clock.GetUtcNow(), GrantedVia.Cli);

    [Fact]
    public void AReplaceThatRemovedTheTargetAndFailed_MovesTheNewContentIntoPlace()
    {
        File.WriteAllText(SettingsPath, "old");
        var replacement = temp.Combine(".settings.1.0123456789abcdef0123456789abcdef.tmp");
        File.WriteAllText(replacement, "new");
        var calls = 0;

        SettingsWriter.ReplaceWithRetry(replacement, SettingsPath, (_, target) =>
        {
            calls++;
            File.Delete(target);
            throw new IOException("Unable to move the replacement file to the file to be replaced.");
        });

        Assert.Equal(1, calls);
        Assert.Equal("new", File.ReadAllText(SettingsPath));
        Assert.False(File.Exists(replacement));
    }

    [Fact]
    public void ASettingsFileHeldForAMoment_StillSaves()
    {
        File.WriteAllText(SettingsPath, """{"schemaVersion": 1}""");
        var writer = new SettingsWriter(new SettingsStore(SettingsPath), clock);
        var holder = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // A dedicated thread, not the thread pool: under a busy parallel run a pool task's 100 ms delay could fire after
        // the writer's half-second retry window (2.11 build, 2026-11-13).
        var release = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(100));
            holder.Dispose();
        }) { IsBackground = true };
        release.Start();

        try
        {
            writer.SaveWriteGrant(Grant());
        }
        finally
        {
            release.Join();
        }

        Assert.Single(new SettingsStore(SettingsPath).Read().Notebooks);
    }

    [Fact]
    public async Task ASettingsFileHeldTooLong_FailsTheSave_AndLeavesTheOldFile()
    {
        const string Old = """{"schemaVersion": 1}""";
        File.WriteAllText(SettingsPath, Old);
        var writer = new SettingsWriter(new SettingsStore(SettingsPath), clock);
        var holder = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            await holder.DisposeAsync();
        }, TestContext.Current.CancellationToken);

        OneNoteException ex;
        try
        {
            ex = Assert.Throws<OneNoteException>(() => writer.SaveWriteGrant(Grant()));
        }
        finally
        {
            await release;
        }

        Assert.Equal(ErrorCode.SettingsInvalid, ex.Code);
        Assert.Contains("couldn't be written", ex.Message, StringComparison.Ordinal);
        Assert.Equal(Old, File.ReadAllText(SettingsPath));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, ".settings.*.tmp"));
    }
}
