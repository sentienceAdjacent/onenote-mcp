using OneNoteMcp.Core.Journal;
using OneNoteMcp.Core.Settings;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>Runs the tests that block a thread on purpose alone, after the parallel ones, so their waits can't starve a timing-sensitive test.</summary>
[CollectionDefinition(nameof(BlockingTestsCollection), DisableParallelization = true)]
public sealed class BlockingTestsCollection;

/// <summary>
/// Story 5.5: <see cref="JournalStore.TryAddRecycleBinId"/> waits out another process that briefly holds a just-written
/// entry (a virus scanner, the search indexer), and gives up cleanly when the hold lasts. These tests hold the file and
/// wait for real, so they run in <see cref="BlockingTestsCollection"/> (with them in the parallel run, the gateway's
/// deadline test failed four runs in eight).
/// </summary>
[Collection(nameof(BlockingTestsCollection))]
public sealed class JournalRewriteRetryTests : IDisposable
{
    private readonly TempDirectory temp = new();

    public void Dispose() => temp.Dispose();

    private string Folder => temp.Combine("journal");

    private SettingsStore Store => new(temp.Combine("settings.json"), Folder);

    [Fact]
    public async Task AddRecycleBinId_WhenAnotherProcessBrieflyHoldsTheEntry_WaitsAndRewritesIt()
    {
        // A virus scanner or the search indexer opens a just-written file for a moment without delete sharing, and
        // File.Replace then fails with a sharing violation (2026-11-13: one full-suite run in six).
        var entry = JournalTests.Entry();
        var written = new JournalStore(Store.Read).Write(entry);
        var held = new FileStream(written.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var release = Task.Run(async () =>
        {
            await Task.Delay(150, TestContext.Current.CancellationToken);
            await held.DisposeAsync();
        }, TestContext.Current.CancellationToken);

        var why = JournalStore.TryAddRecycleBinId(written.Path, entry.EntryId, "{RB}{1}{E9}");
        await release;

        Assert.Null(why);
        Assert.Equal("{RB}{1}{E9}", JournalStore.Read(written.Path).RecycleBinId);
        Assert.Equal([Path.GetFileName(written.Path)], Directory.GetFiles(Folder).Select(Path.GetFileName));
    }

    [Fact]
    public void AddRecycleBinId_WhenTheEntryStaysHeld_GivesUp_LeavingTheEntryAsWritten_AndNoTempFile()
    {
        var entry = JournalTests.Entry();
        var written = new JournalStore(Store.Read).Write(entry);

        string? why;
        using (new FileStream(written.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            why = JournalStore.TryAddRecycleBinId(written.Path, entry.EntryId, "{RB}{1}{E9}");
        }

        Assert.Equal("IOException while rewriting the entry", why);
        Assert.Null(JournalStore.Read(written.Path).RecycleBinId);
        Assert.Equal([Path.GetFileName(written.Path)], Directory.GetFiles(Folder).Select(Path.GetFileName));
    }
}
