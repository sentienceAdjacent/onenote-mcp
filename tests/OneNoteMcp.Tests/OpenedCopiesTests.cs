using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 3.8, Phill's Q3 (a) and follow-ups (2026-11-18): where opened copies live, and the startup cleanup (b): a folder in
/// the opened-copies folder is deleted when it holds the marker, its notebook isn't open in OneNote, and nothing in it has
/// changed for more than 7 days (backlog 21). Folders without the marker are never touched. Everything runs in a temporary folder.
/// </summary>
public sealed class OpenedCopiesTests : IDisposable
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";

    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory temp = new();
    private readonly FakeGateway gateway = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Folder => temp.Combine("opened");

    public void Dispose() => temp.Dispose();

    // --- Where copies live ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultFolder_IsOpenedInOneNoteMcpsLocalAppData()
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneNoteMcp", "opened");

        Assert.Equal(expected, OpenedCopies.DefaultFolder(_ => null));
    }

    [Fact]
    public void TheOverrides_KeepTestsOutOfTheUsersAppData()
    {
        Assert.Equal(@"D:\Scratch\opened", OpenedCopies.DefaultFolder(v => v == OpenedCopies.DirectoryOverrideVariable ? @"D:\Scratch\opened" : null));
        Assert.Equal(@"D:\Scratch\opened", OpenedCopies.DefaultFolder(v => v == SettingsStore.PathOverrideVariable ? @"D:\Scratch\settings.json" : null));
    }

    [Fact]
    public void TheStoresFolder_IsAProtectedRoot_EvenWhenTheFileIsInvalid()
    {
        File.WriteAllText(temp.Combine("settings.json"), "{ not json");
        var store = new SettingsStore(temp.Combine("settings.json"), temp.Combine("journal"), Folder);

        Assert.Equal(Folder, store.Read().OpenedCopies);
        Assert.Contains(Folder, Protection.RootsOf(store.Read()));
    }

    [Fact]
    public void Holds_FindsAPathInsideAKnownOneDriveFolder_AtASegmentBoundary()
    {
        var cloud = temp.Combine("OneDrive");
        var folders = new OneDriveFolders([new OneDriveAccount(cloud)]);

        Assert.True(folders.Holds(cloud));
        Assert.True(folders.Holds(Path.Combine(cloud.ToUpperInvariant(), "a", "b")));
        Assert.False(folders.Holds(temp.Combine("OneDrive - Contoso")));
        Assert.False(folders.Holds(temp.Combine("elsewhere")));
        Assert.False(OneDriveFolders.None.Holds(cloud));
        Assert.False(folders.Holds("https://d.docs.live.net/0123456789abcdef/x"));
    }

    // --- The startup cleanup ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMarkedCopy_OlderThan7Days_NotOpen_IsDeleted_AndTheRestAreKept()
    {
        var old = Copy("Old 20261101-100000", Now.AddDays(-8));
        var young = Copy("Young 20261115-100000", Now.AddDays(-6));
        var unmarked = Copy("Mine", Now.AddDays(-30), marker: false);
        var open = Copy("Open 20261101-100000", Now.AddDays(-8));
        OpenNotebooks(open);

        var deleted = await OpenedCopySweep.SweepAsync(gateway, Folder, Now, Ct);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(young));
        Assert.True(Directory.Exists(unmarked));
        Assert.True(Directory.Exists(open));
    }

    // --- Backlog 21: age is the last change in the copy, never its folder's creation time alone ----------------------------

    [Fact]
    public async Task ACopyCreatedLongAgo_WithAFileWrittenYesterday_IsKept()
    {
        var copy = Copy("Edited 20261101-100000", Now.AddDays(-30));
        File.SetLastWriteTimeUtc(Path.Combine(copy, "Section.one"), Now.AddDays(-1).UtcDateTime);

        Assert.Equal(0, await OpenedCopySweep.SweepAsync(gateway, Folder, Now, Ct));
        Assert.True(Directory.Exists(copy));
    }

    [Fact]
    public async Task ACopyWithANestedFileChangedRecently_IsKept()
    {
        var copy = Copy("Nested 20261101-100000", Now.AddDays(-30));
        var inner = Path.Combine(copy, "Group");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "Inner.one"), "y");
        Age(copy, Now.AddDays(-30));
        File.SetLastWriteTimeUtc(Path.Combine(inner, "Inner.one"), Now.AddDays(-6).UtcDateTime);

        Assert.Equal(0, await OpenedCopySweep.SweepAsync(gateway, Folder, Now, Ct));
        Assert.True(Directory.Exists(copy));
    }

    [Fact]
    public async Task AFileCopiedInRecently_KeepsItsOldWriteTime_ButItsCreationTimeKeepsTheCopy()
    {
        // A copied file keeps its source's last-write time; its creation time says when it arrived.
        var copy = Copy("Copied 20261101-100000", Now.AddDays(-30));
        File.SetCreationTimeUtc(Path.Combine(copy, "Section.one"), Now.AddDays(-2).UtcDateTime);

        Assert.Equal(0, await OpenedCopySweep.SweepAsync(gateway, Folder, Now, Ct));
        Assert.True(Directory.Exists(copy));
    }

    [Fact]
    public async Task ACopyWhoseFolderWasWrittenRecently_IsKept()
    {
        // A file added or removed changes the folder's own last-write time.
        var copy = Copy("Touched 20261101-100000", Now.AddDays(-30));
        Directory.SetLastWriteTimeUtc(copy, Now.AddDays(-3).UtcDateTime);

        Assert.Equal(0, await OpenedCopySweep.SweepAsync(gateway, Folder, Now, Ct));
        Assert.True(Directory.Exists(copy));
    }

    [Fact]
    public async Task AJunctionInsideACopy_CountsByItsOwnTimes_AndIsNeverFollowed()
    {
        // The target outside the copy changed yesterday; the copy and the junction itself are 8 days old.
        var outside = temp.Combine("outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Recent.one"), "z");
        var copy = Copy("Linked 20261101-100000", Now.AddDays(-30));
        Junction(Path.Combine(copy, "Link"), outside);
        Age(copy, Now.AddDays(-8));
        File.SetLastWriteTimeUtc(Path.Combine(outside, "Recent.one"), Now.AddDays(-1).UtcDateTime);
        Directory.SetLastWriteTimeUtc(outside, Now.AddDays(-1).UtcDateTime);
        await OpenedCopySweep.SweepAsync(gateway, Folder, Now, Ct);

        // The copy was judged old and deleted: its contents are gone. Windows can briefly refuse the now-empty folder after
        // a junction is removed from it, so the folder itself may linger (the sweep counts it as not deleted then).
        Assert.True(!Directory.Exists(copy) || !Directory.EnumerateFileSystemEntries(copy).Any());
        Assert.True(File.Exists(Path.Combine(outside, "Recent.one")));
    }

    [Fact]
    public async Task ACopyUntouchedFor8Days_InsideAndOut_IsStillDeleted()
    {
        var copy = Copy("Stale 20261101-100000", Now.AddDays(-30));
        var inner = Path.Combine(copy, "Group");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "Inner.one"), "y");
        Age(copy, Now.AddDays(-8));

        Assert.Equal(1, await OpenedCopySweep.SweepAsync(gateway, Folder, Now, Ct));
        Assert.False(Directory.Exists(copy));
    }

    [Fact]
    public async Task NothingOldEnough_MeansNoOneNoteRead()
    {
        Copy("Young 20261115-100000", Now.AddDays(-1));
        var reads = 0;
        gateway.OnGetHierarchy = (_, _) => reads++;

        Assert.Equal(0, await OpenedCopySweep.SweepAsync(gateway, Folder, Now, Ct));
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task OneNoteNotRunning_IsCantTell_SoNothingIsDeleted()
    {
        // Review of 6fe92b3: a closed OneNote still lists its notebooks and reopens them, and "not running" also covers a
        // OneNote with no window yet; "the app is closed" isn't "the notebook was closed", so nothing goes.
        var old = Copy("Old 20261101-100000", Now.AddDays(-8));
        gateway.OnGetHierarchy = (_, _) => throw new OneNoteException(ErrorCode.OneNoteNotRunning, "not running");

        Assert.Equal(0, await OpenedCopySweep.SweepAsync(gateway, Folder, Now, Ct));
        Assert.True(Directory.Exists(old));
    }

    [Fact]
    public void IsOpen_MatchesTheSameFolderInEveryForm_ThroughAJunction()
    {
        // OneNote is given the canonical (link-followed) path; the sweep lists copies through the plain folder.
        var real = temp.Combine("real");
        Directory.CreateDirectory(Path.Combine(real, "Copy"));
        var link = temp.Combine("linked");
        Junction(link, real);

        Assert.True(OpenedCopies.IsOpen(Path.Combine(link, "Copy"), [Path.Combine(real, "Copy")]));
        Assert.True(OpenedCopies.IsOpen(Path.Combine(link, "Copy"), [Path.Combine(real, "Copy", "Inner")]));
        Assert.False(OpenedCopies.IsOpen(Path.Combine(link, "Copy"), [Path.Combine(real, "Other")]));
    }

    private static void Junction(string link, string target)
    {
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    [Fact]
    public async Task AFailedReadOfTheOpenNotebooks_DeletesNothing()
    {
        var old = Copy("Old 20261101-100000", Now.AddDays(-8));
        gateway.OnGetHierarchy = (_, _) => throw new OneNoteException(ErrorCode.ComBusy, "busy");

        Assert.Equal(0, await OpenedCopySweep.SweepAsync(gateway, Folder, Now, Ct));
        Assert.True(Directory.Exists(old));
    }

    [Fact]
    public async Task ANotebookOpenInsideACopy_KeepsIt()
    {
        var old = Copy("Old 20261101-100000", Now.AddDays(-8));
        OpenNotebooks(Path.Combine(old, "Inner"));

        Assert.Equal(0, await OpenedCopySweep.SweepAsync(gateway, Folder, Now, Ct));
        Assert.True(Directory.Exists(old));
    }

    // --- D186: a copy goes entry by entry, retried, its marker last -----------------------------------------------------

    [Fact]
    public async Task AnEntryRefusedOnce_IsTriedAgain_AndTheCopyGoes()
    {
        var copy = Copy("Busy 20261101-100000", Now.AddDays(-8));
        var section = Path.Combine(copy, "Section.one");
        var tries = 0;

        var deleted = await OpenedCopySweep.SweepAsync(gateway, Folder, Now, path =>
        {
            if (string.Equals(path, section, StringComparison.OrdinalIgnoreCase) && ++tries == 1)
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            OpenedCopySweep.DeleteEntry(path);
        }, Ct);

        Assert.Equal(1, deleted);
        Assert.Equal(2, tries);
        Assert.False(Directory.Exists(copy));
    }

    [Fact]
    public async Task TheMarkerIsDeletedLast_ThenTheEmptyFolder()
    {
        var copy = Copy("Ordered 20261101-100000", Now.AddDays(-8));
        var inner = Path.Combine(copy, "Group");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "Inner.one"), "y");
        File.WriteAllText(Path.Combine(copy, "Zz.one"), "z");
        Age(copy, Now.AddDays(-8));
        var order = new List<string>();

        await OpenedCopySweep.SweepAsync(gateway, Folder, Now, path =>
        {
            order.Add(Path.GetRelativePath(Folder, path));
            OpenedCopySweep.DeleteEntry(path);
        }, Ct);

        Assert.Equal(5, order.Count);
        Assert.Equal(Path.Combine("Ordered 20261101-100000", ExportRootGuard.MarkerFileName), order[^2]);
        Assert.Equal("Ordered 20261101-100000", order[^1]);
        Assert.False(Directory.Exists(copy));
    }

    [Fact]
    public async Task AnEntryThatStaysLocked_LeavesTheMarker_SoTheNextSweepStillSeesTheCopy()
    {
        // A real lock: on main, one recursive delete took the marker (it sorts first) and then failed on the locked file,
        // leaving a folder no later sweep would look at.
        var copy = Copy("Locked 20261101-100000", Now.AddDays(-8));
        var locked = Path.Combine(copy, "Zz.one");
        File.WriteAllText(locked, "z");
        Age(copy, Now.AddDays(-8));

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(0, await OpenedCopySweep.SweepAsync(gateway, Folder, Now, Ct));
        }

        Assert.True(File.Exists(Path.Combine(copy, ExportRootGuard.MarkerFileName)));
        Assert.Contains(copy, OpenedCopies.MarkedCopies(Folder));

        // Once the lock is gone (and nothing changed: the failed sweep only removed entries), the next sweep deletes it.
        Age(copy, Now.AddDays(-8));
        Assert.Equal(1, await OpenedCopySweep.SweepAsync(gateway, Folder, Now, Ct));
        Assert.False(Directory.Exists(copy));
    }

    [Fact]
    public async Task AFolderDeleteThatStaysRefused_WritesTheMarkerBack_SoTheNextSweepStillSeesTheCopy()
    {
        var copy = Copy("Held 20261101-100000", Now.AddDays(-8));

        var deleted = await OpenedCopySweep.SweepAsync(gateway, Folder, Now, path =>
        {
            if (string.Equals(path, copy, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            OpenedCopySweep.DeleteEntry(path);
        }, Ct);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(Path.Combine(copy, ExportRootGuard.MarkerFileName)));
        Assert.Contains(copy, OpenedCopies.MarkedCopies(Folder));
    }

    [Fact]
    public async Task AnEntryThatAppearsAfterTheListing_KeepsTheFolder_AndItsMarker()
    {
        var copy = Copy("Late 20261101-100000", Now.AddDays(-8));
        var late = Path.Combine(copy, "Late.one");

        var deleted = await OpenedCopySweep.SweepAsync(gateway, Folder, Now, path =>
        {
            OpenedCopySweep.DeleteEntry(path);
            if (path.EndsWith(ExportRootGuard.MarkerFileName, StringComparison.OrdinalIgnoreCase) && !File.Exists(late))
            {
                File.WriteAllText(late, "late");
            }
        }, Ct);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(late));
        Assert.True(File.Exists(Path.Combine(copy, ExportRootGuard.MarkerFileName)));
    }

    [Fact]
    public async Task NoFolder_IsNothingToDo()
    {
        Assert.Equal(0, await OpenedCopySweep.SweepAsync(gateway, temp.Combine("missing"), Now, Ct));
    }

    private string Copy(string name, DateTimeOffset created, bool marker = true)
    {
        var folder = Path.Combine(Folder, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Section.one"), "x");
        if (marker)
        {
            File.WriteAllText(Path.Combine(folder, ExportRootGuard.MarkerFileName), string.Empty);
        }

        // Backlog 21: the copy as a whole is untouched since it was made, so every entry's times are that date.
        Age(folder, created);
        return folder;
    }

    /// <summary>Sets the creation and last-write times of <paramref name="folder"/> and everything in it to <paramref name="at"/>.</summary>
    private static void Age(string folder, DateTimeOffset at)
    {
        // Never through a link or junction: a link is aged as an entry, what it points at is left alone.
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo inner && !inner.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Age(inner.FullName, at);
                continue;
            }

            entry.CreationTimeUtc = at.UtcDateTime;
            entry.LastWriteTimeUtc = at.UtcDateTime;
        }

        Directory.SetCreationTimeUtc(folder, at.UtcDateTime);
        Directory.SetLastWriteTimeUtc(folder, at.UtcDateTime);
    }

    private void OpenNotebooks(params string[] paths) =>
        gateway.Hierarchies[(string.Empty, HierarchyScope.Notebooks)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebooks {Ns}>"
            + string.Concat(paths.Select((p, i) => $"<one:Notebook name=\"N{i}\" ID=\"{{N{i}}}\" path=\"{p}\" />"))
            + "</one:Notebooks>";
}
