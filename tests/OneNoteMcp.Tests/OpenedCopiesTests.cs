using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Tests;

/// <summary>
/// Story 3.8, Phill's Q3 (a) and follow-ups (2026-11-18): where opened copies live, and the startup cleanup (b): a folder in
/// the opened-copies folder is deleted when it holds the marker, its notebook isn't open in OneNote, and it is more than 7
/// days old. Folders without the marker are never touched. Everything runs in a temporary folder.
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

        Directory.SetCreationTimeUtc(folder, created.UtcDateTime);
        return folder;
    }

    private void OpenNotebooks(params string[] paths) =>
        gateway.Hierarchies[(string.Empty, HierarchyScope.Notebooks)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebooks {Ns}>"
            + string.Concat(paths.Select((p, i) => $"<one:Notebook name=\"N{i}\" ID=\"{{N{i}}}\" path=\"{p}\" />"))
            + "</one:Notebooks>";
}
