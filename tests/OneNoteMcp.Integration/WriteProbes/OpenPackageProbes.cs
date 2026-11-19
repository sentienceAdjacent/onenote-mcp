using System.Diagnostics;
using System.Xml.Linq;
using OneNoteMcp.Com.Interop;
using Xunit;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Story 3.8, scenario <c>open-package-p1-p2</c> (sitting S0, before the build): probes P1 and P2 on the local throwaway
/// <c>MCP Test Throwaway 1112-034716 a</c> (adopted by its exact name, or opened by Phill at a pause).
/// <list type="bullet">
/// <item>P1: package the throwaway (<c>Publish</c> as <c>pfOneNotePackage</c> into a fresh folder of the probes' publish
/// folder), unpack it with <c>OpenPackage</c> into a second fresh folder that already holds 3.8's marker file
/// <c>.onenote-mcp-opened</c>, then <c>OpenHierarchy</c> the unpacked folder while the original is still open. Records
/// whether the copy opens or clashes, both notebooks' IDs, the copy's notebook and section paths (relative to, and checked
/// under, the publish folder), focus, and the copy's pages at 0 s and 5 s.</item>
/// <item>P2: <c>CloseNotebook</c> (not forced) of the copy: focus, time, whether the copy is gone and the original still
/// open; then a pause for Phill to say whether any dialog appeared.</item>
/// <item>Cleanup: if the copy is still open, a pause asks Phill to close it; then the package's and the copy's folders are
/// deleted (only those two, both under the publish folder).</item>
/// </list>
/// Product code is not used: every call goes through the probe harness's guarded COM seam. Nothing is written into the
/// throwaway or any test notebook.
/// </summary>
public sealed class OpenPackageProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "open-package-p1-p2";

    /// <summary>The marker 3.8 writes into each folder it creates (<c>ExportRootGuard.MarkerFileName</c>).</summary>
    private const string MarkerFile = ".onenote-mcp-opened";

    [Fact]
    public async Task OpenPackageP1P2()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.ProbeCallSeconds);
        var stamp = HierarchyProbes.Stamp();
        var original = await s.SetupAsync("throwaway", () => Round2.AdoptOtherAsync(s, cloud: false));
        s.Recorder.Row("throwaway-adopt", "Setup", "GetHierarchy", null, null, new { found = original is not null });
        if (original is null)
        {
            s.Recorder.Row("p1", "OpenPackage", "-", null, null, new { notProbed = "the throwaway isn't open" });
            s.Finish();
            return;
        }

        string? packageFolder = null;
        string? copyFolder = null;
        string? copyId = null;
        await s.StepAsync("p1", async () => (packageFolder, copyFolder, copyId) = await P1Async(s, original, stamp));
        await s.StepAsync("p2", () => P2Async(s, original, copyId));
        await s.StepAsync("cleanup", () => CleanupAsync(s, copyId, packageFolder, copyFolder));
        s.Finish();
    }

    private static async Task<(string? PackageFolder, string? CopyFolder, string? CopyId)> P1Async(ProbeSession s, ProbeTarget original, string stamp)
    {
        var originalBefore = await NotebookViewAsync(s, original.NotebookId);
        s.Recorder.Row("p1-original", "OpenPackage", "GetHierarchy", null, null, new { originalId = original.NotebookId, original = originalBefore });

        var package = await s.PublishFreshAsync($"p1-package-{stamp}", original.NotebookId, PublishFormat.pfOneNotePackage, $"p1-{stamp}.onepkg");
        s.Recorder.Row("p1-package", "OpenPackage", "Publish(pfOneNotePackage)", new { scope = "notebook" }, package.Attempt, package.Summary);
        var packageFolder = Path.Combine(ProbeSession.PublishDir, package.Folder);
        if (package.Landed is not { } pkg)
        {
            s.Recorder.Row("p1", "OpenPackage", "OpenPackage", null, null, new { notProbed = "the package didn't land" });
            return (packageFolder, null, null);
        }

        // As 3.8 will: a new folder holding only the marker, then the unpack beside it (spike 3.1 O2).
        var copyFolder = ProbeSession.FreshFolder(ProbeSession.PublishDir, $"p1-copy-{stamp}");
        await File.WriteAllTextAsync(Path.Combine(copyFolder, MarkerFile), string.Empty, ProbeSession.Ct);

        var before = await Round2.OpenNotebooksAsync(s);
        var (unpack, pathOut, unpackFocusKept) = await s.OpenPackageIntoAsync(pkg, copyFolder);
        var afterUnpack = await Round2.OpenNotebooksAsync(s);
        s.Recorder.Row("p1-unpack", "OpenPackage", "OpenPackage", null, unpack, new
        {
            focusKept = unpackFocusKept,
            pathOut = pathOut is null ? null : Path.GetRelativePath(ProbeSession.PublishDir, pathOut),
            pathOutIsTheFolder = pathOut is not null && string.Equals(TestNotebookGuard.TrimPath(pathOut), TestNotebookGuard.TrimPath(copyFolder), StringComparison.OrdinalIgnoreCase),
            notebooksAddedByOpenPackage = afterUnpack.Keys.Except(before.Keys).Count(),
            folderAfter = ProbeSession.Snapshot(copyFolder).Take(50).ToList(),
        });
        if (!unpack.Ok || pathOut is null)
        {
            return (packageFolder, copyFolder, null);
        }

        // The question: does OpenHierarchy of the unpacked copy open it while the original is open, or clash?
        var focus = ProbeSession.Foreground();
        var watch = Stopwatch.StartNew();
        var (open, id, _) = await s.OpenBackupAsync(pathOut);
        watch.Stop();
        var focusKept = focus == ProbeSession.Foreground();
        var after = await Round2.OpenNotebooksAsync(s);
        var copy = id is null ? null : await NotebookViewAsync(s, id);
        var originalAfter = await NotebookViewAsync(s, original.NotebookId);
        s.Recorder.Row("p1-open", "OpenPackage", "OpenHierarchy", new { path = "the unpacked folder" }, open, new
        {
            ms = watch.ElapsedMilliseconds,
            focusKept,
            copyId = id,
            sameIdAsOriginal = id is not null && string.Equals(id, original.NotebookId, StringComparison.Ordinal),
            notebooksAdded = after.Keys.Except(before.Keys).ToList(),
            originalStillOpen = after.ContainsKey(original.NotebookId),
            originalUnchanged = originalAfter is not null && Equals(originalAfter.Path, originalBefore?.Path),
            copy,
            pages = id is null ? null : await Round2.PagesNowAndLaterAsync(s, id),

            // The first run (2026-11-18) saw 0 sections at once (as O1 saw 0 pages): read again after the 5 s page wait.
            copyAfter5s = id is null ? null : await NotebookViewAsync(s, id),
        });
        return (packageFolder, copyFolder, id);
    }

    private static async Task P2Async(ProbeSession s, ProbeTarget original, string? copyId)
    {
        if (copyId is null || copyId == original.NotebookId)
        {
            s.Recorder.Row("p2", "CloseNotebook", "-", null, null, new { notProbed = copyId is null ? "no copy was opened" : "the copy has the original's ID" });
            return;
        }

        var focus = ProbeSession.Foreground();
        var watch = Stopwatch.StartNew();
        var attempt = await ProbeSession.TryAsync(() => TestNotebook.CloseOpenedCopyAsync(s.Gateway, copyId, s.Opened, ProbeSession.PublishDir, ProbeSession.Ct));
        watch.Stop();
        var focusKept = focus == ProbeSession.Foreground();
        var after = await Round2.OpenNotebooksAsync(s);
        s.Recorder.Row("p2-close", "CloseNotebook", "CloseNotebook", new { force = false }, attempt, new
        {
            ms = watch.ElapsedMilliseconds,
            focusKept,
            copyGone = !after.ContainsKey(copyId),
            originalStillOpen = after.ContainsKey(original.NotebookId),
        });

        var answered = await s.PauseAsync(
            "p2-dialog",
            "P2 just closed the copy (the notebook named after the folder \"p1-copy-…\"). Look at OneNote: did any dialog or message appear? "
            + "Is the copy gone from the notebook list, and is \"" + Round2.LocalThrowawayName + "\" still open? Tell the session what you saw, then continue. "
            + "Don't close or change anything else.");
        s.Recorder.Row("p2-dialog", "CloseNotebook", "pause", null, null, new { answered, seenBy = "Phill, reported to the session" });
    }

    private static async Task CleanupAsync(ProbeSession s, string? copyId, string? packageFolder, string? copyFolder)
    {
        if (copyId is not null && (await Round2.OpenNotebooksAsync(s)).ContainsKey(copyId))
        {
            await s.PauseAsync(
                "close-copy",
                "The copy (the notebook named after the folder \"p1-copy-…\") is still open. Close only it: right-click it in the notebook list > Close This Notebook. "
                + "Don't close \"" + Round2.LocalThrowawayName + "\" or any MCP Test notebook. Then continue.");
        }

        var stillOpen = copyId is not null && (await Round2.OpenNotebooksAsync(s)).ContainsKey(copyId);
        var deleted = new List<string>();
        foreach (var folder in new[] { packageFolder, stillOpen ? null : copyFolder })
        {
            if (folder is null || !Directory.Exists(folder))
            {
                continue;
            }

            TestNotebookGuard.EnsureUnder("cleanup", folder, ProbeSession.PublishDir);
            Directory.Delete(folder, recursive: true);
            deleted.Add(Path.GetRelativePath(ProbeSession.PublishDir, folder));
        }

        s.Recorder.Row("cleanup", "Setup", "-", null, null, new { deleted, copyFolderKept = stillOpen ? "the copy is still open, so its folder stays" : null });
    }

    /// <summary>A notebook's name, path (relative to the publish folder when under it) and its sections' paths, by ID; null when it isn't open.</summary>
    private static async Task<NotebookView?> NotebookViewAsync(ProbeSession s, string notebookId)
    {
        XElement root;
        try
        {
            root = XDocument.Parse(await s.Gateway.GetHierarchyAsync(notebookId, CoreScope.Sections, ProbeSession.Ct)).Root!;
        }
        catch (OneNoteMcp.Core.OneNoteException)
        {
            return null;
        }

        static (string? Path, bool UnderPublishDir) Where(string? path) =>
            path is not null && Path.IsPathFullyQualified(path) && Path.GetFullPath(path).StartsWith(ProbeSession.PublishDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? (Path.GetRelativePath(ProbeSession.PublishDir, path), true)
                : (path is null ? null : "(outside the publish folder)", false);

        var (path, under) = Where((string?)root.Attribute("path"));
        var sections = root.Descendants(One + "Section")
            .Select(e => Where((string?)e.Attribute("path")))
            .ToList();
        return new NotebookView(
            (string?)root.Attribute("name"),
            path,
            under,
            sections.Count,
            sections.Count(x => x.UnderPublishDir),
            [.. sections.Where(x => x.UnderPublishDir).Select(x => x.Path!).Take(20)]);
    }

    private sealed record NotebookView(string? Name, string? Path, bool UnderPublishDir, int Sections, int SectionsUnderPublishDir, IReadOnlyList<string> SectionPaths);
}
