using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core.Settings;
using Xunit;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Story 3.8, scenario <c>open-package-onedrive-15m</c> (Phill, 2026-11-18): does a copy opened inside a OneDrive-synced
/// folder make OneNote close? Two copies of the throwaway's package, unpacked and opened as <c>open_package</c> does (the
/// marker file first, then <c>OpenPackage</c>, then <c>OpenHierarchy</c>): (i) in the probes' publish folder, outside
/// OneDrive; (ii) in <c>probe-crash-&lt;stamp&gt;</c> under the default export root, which must lie inside the OneDrive
/// folder named by <c>%OneDrive%</c> (else (ii) is not probed). After a pause for Phill to confirm both show, both copies are
/// read at <c>pages</c> scope every 60 s for 15 min, recording each copy's path form (local or URL) each time. ONENOTE.EXE
/// is watched in-process: its PID, start time and command line at the start; its exit time and exit code if it goes. The
/// Application log's events 1000, 1001 and 1002 for ONENOTE.EXE are counted before and after. Cleanup closes both copies
/// (guarded) and deletes both folders; if OneNote went, the run stops there and leaves both folders. Rows carry no user name,
/// cid or page content: paths are relative to their root, and a URL is recorded only as "url".
/// </summary>
[Collection(ThrowawayOpenState.Name)]
public sealed class OneDriveCrashProbe(ITestOutputHelper output)
{
    public const string ScenarioName = "open-package-onedrive-15m";

    private const string MarkerFile = ".onenote-mcp-opened";

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task OpenPackageOneDrive15Minutes()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.ProbeCallSeconds);
        var stamp = HierarchyProbes.Stamp();
        var minutes = int.TryParse(Environment.GetEnvironmentVariable("ONENOTE_MCP_PROBE_MINUTES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : 15;

        var throwaway = await s.SetupAsync("throwaway", () => Round2.AdoptOtherAsync(s, cloud: false));
        if (throwaway is null)
        {
            s.Recorder.Row("crash", "OneDrive", "-", null, null, new { notProbed = "the throwaway isn't open" });
            s.Finish();
            return;
        }

        // Where (ii) goes: the default export root, which must sit inside %OneDrive% (not hard-coded).
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive") ?? Environment.GetEnvironmentVariable("OneDriveConsumer") ?? Environment.GetEnvironmentVariable("OneDriveCommercial");
        var exportRoot = ExportRoots.Default().Paths[0];
        var exportRootInOneDrive = oneDrive is not null && Path.GetFullPath(exportRoot).StartsWith(TestNotebookGuard.TrimPath(Path.GetFullPath(oneDrive)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        s.Recorder.Row("crash-where", "OneDrive", "-", null, null, new { oneDriveKnown = oneDrive is not null, exportRootInOneDrive, minutes });

        var watcher = await OneNoteWatcher.StartAsync(s);
        var eventsBefore = await ApplicationEventsAsync();
        s.Recorder.Row("crash-events-before", "OneDrive", "wevtutil", null, null, eventsBefore);

        var package = await s.PublishFreshAsync($"crash-package-{stamp}", throwaway.NotebookId, PublishFormat.pfOneNotePackage, $"crash-{stamp}.onepkg");
        s.Recorder.Row("crash-package", "OneDrive", "Publish(pfOneNotePackage)", null, package.Attempt, package.Summary);
        if (package.Landed is not { } pkg)
        {
            s.Recorder.Row("crash", "OneDrive", "-", null, null, new { notProbed = "the package didn't land" });
            s.Finish();
            return;
        }

        var copies = new List<Copy>();
        await s.StepAsync("crash-open", async () =>
        {
            if (await OpenAsync(s, "local", pkg, ProbeSession.PublishDir, $"crash-local-{stamp}") is { } local)
            {
                copies.Add(local);
            }

            if (exportRootInOneDrive)
            {
                Directory.CreateDirectory(exportRoot);
                if (await OpenAsync(s, "onedrive", pkg, exportRoot, $"{TestNotebook.CrashProbeFolderPrefix}{stamp}") is { } inOneDrive)
                {
                    copies.Add(inOneDrive);
                }
            }
            else
            {
                s.Recorder.Row("crash-open-onedrive", "OneDrive", "-", null, null, new { notProbed = "the default export root isn't inside %OneDrive%" });
            }
        });

        await s.PauseAsync(
            "crash-start",
            $"Check OneNote's notebook list: it should show {copies.Count} new notebook(s) named \"crash-local-{stamp}\" and \"{TestNotebook.CrashProbeFolderPrefix}{stamp}\" (the second only if the default export folder is in OneDrive). "
            + "Don't open, edit or close them. Leave OneNote running and OneDrive syncing as usual. When you continue, the probe reads both copies every minute for "
            + minutes.ToString(CultureInfo.InvariantCulture) + " minutes; if OneNote closes, say when you noticed.");

        var died = false;
        await s.StepAsync("crash-watch", async () =>
        {
            for (var minute = 1; minute <= minutes && !died; minute++)
            {
                await Task.Delay(Every, ProbeSession.Ct);
                if (watcher.Exited() is { } exit)
                {
                    died = true;
                    s.Recorder.Row("crash-exit", "OneDrive", "-", new { minute }, null, exit);
                    break;
                }

                foreach (var copy in copies)
                {
                    s.Recorder.Row($"crash-read-{copy.Label}", "OneDrive", "GetHierarchy(pages)", new { minute }, null, await ReadAsync(s, copy));
                }
            }
        });

        s.Recorder.Row("crash-events-after", "OneDrive", "wevtutil", null, null, await ApplicationEventsAsync());
        if (died || watcher.Exited() is not null)
        {
            s.Recorder.Row("crash-cleanup", "OneDrive", "-", null, null, new { skipped = "OneNote closed: both folders are left for the coordinating session to clean up" });
            s.Finish();
            return;
        }

        await s.StepAsync("crash-cleanup", () => CleanupAsync(s, copies, package.Folder));
        s.Finish();
    }

    private sealed record Copy(string Label, string Id, string Folder, string Root);

    /// <summary>The marker, then <c>OpenPackage</c> into a fresh folder, then <c>OpenHierarchy</c> of what it reports.</summary>
    private static async Task<Copy?> OpenAsync(ProbeSession s, string label, string package, string root, string folderName)
    {
        var folder = ProbeSession.FreshFolder(root, folderName);
        await File.WriteAllTextAsync(Path.Combine(folder, MarkerFile), string.Empty, ProbeSession.Ct);
        var (unpack, pathOut) = await ProbeSession.TryValueAsync(() => TestNotebook.OpenPackageIntoAsync(s.Gateway, package, ProbeSession.PublishDir, folder, root, ProbeSession.Ct));
        s.Recorder.Row($"crash-unpack-{label}", "OneDrive", "OpenPackage", null, unpack, new { folder = folderName });
        if (!unpack.Ok || pathOut is null)
        {
            return null;
        }

        var (open, id) = await ProbeSession.TryValueAsync(() => TestNotebook.OpenBackupAsync(s.Gateway, pathOut, root, ProbeSession.Ct));
        s.Recorder.Row($"crash-open-{label}", "OneDrive", "OpenHierarchy", null, open, new { opened = id is not null });
        if (!open.Ok || id is null)
        {
            return null;
        }

        s.Opened.Add(id);
        return new Copy(label, id, folder, root);
    }

    /// <summary>One read: the copy's page count and the form of its notebook's and sections' paths (local, url or none).</summary>
    private static async Task<object> ReadAsync(ProbeSession s, Copy copy)
    {
        var (attempt, xml) = await ProbeSession.TryValueAsync(() => s.Gateway.GetHierarchyAsync(copy.Id, CoreScope.Pages, ProbeSession.Ct));
        if (!attempt.Ok || xml is null)
        {
            return new { attempt };
        }

        var root = XDocument.Parse(xml).Root!;
        var sections = root.Descendants(One + "Section").Select(e => Form((string?)e.Attribute("path"))).ToList();
        return new
        {
            attempt.Ms,
            notebookPath = Form((string?)root.Attribute("path")),
            sections = sections.Count,
            sectionsLocal = sections.Count(f => f == "local"),
            sectionsUrl = sections.Count(f => f == "url"),
            pages = root.Descendants(One + "Page").Count(),
        };
    }

    private static string Form(string? path) =>
        string.IsNullOrEmpty(path) ? "none" : path.Contains("://", StringComparison.Ordinal) ? "url" : "local";

    /// <summary>Closes each copy (guarded to its root; a copy whose path became a URL is refused and left for Phill), then deletes the closed copies' folders and the package's.</summary>
    private static async Task CleanupAsync(ProbeSession s, IReadOnlyList<Copy> copies, string packageFolder)
    {
        var stillOpen = new List<string>();
        foreach (var copy in copies)
        {
            var attempt = await ProbeSession.TryAsync(() => TestNotebook.CloseOpenedCopyAsync(s.Gateway, copy.Id, s.Opened, copy.Root, ProbeSession.Ct));
            s.Recorder.Row($"crash-close-{copy.Label}", "OneDrive", "CloseNotebook", null, attempt, null);
            if (!attempt.Ok)
            {
                stillOpen.Add(copy.Label);
            }
        }

        if (stillOpen.Count > 0)
        {
            await s.PauseAsync(
                "crash-close-by-hand",
                $"The probe couldn't close these copies: {string.Join(", ", stillOpen)} (the notebooks named \"crash-local-…\" and \"probe-crash-…\"). Close only them: right-click > Close This Notebook. Then continue.");
        }

        var open = (await Round2.OpenNotebooksAsync(s)).Keys.ToHashSet(StringComparer.Ordinal);
        var deleted = new List<string>();
        foreach (var copy in copies.Where(c => !open.Contains(c.Id)))
        {
            TestNotebookGuard.EnsureUnder("cleanup", copy.Folder, copy.Root);
            Directory.Delete(copy.Folder, recursive: true);
            deleted.Add(copy.Label);
        }

        var package = Path.Combine(ProbeSession.PublishDir, packageFolder);
        if (Directory.Exists(package))
        {
            TestNotebookGuard.EnsureUnder("cleanup", package, ProbeSession.PublishDir);
            Directory.Delete(package, recursive: true);
            deleted.Add("package");
        }

        s.Recorder.Row("crash-cleanup", "OneDrive", "-", null, null, new { deleted, leftOpen = copies.Where(c => open.Contains(c.Id)).Select(c => c.Label).ToList() });
    }

    /// <summary>The Application log's events 1000, 1001 and 1002 that mention ONENOTE.EXE (counts and times only).</summary>
    private static async Task<object> ApplicationEventsAsync()
    {
        var (code, text) = await RunAsync("wevtutil", "qe Application /q:\"*[System[(EventID=1000 or EventID=1001 or EventID=1002)]]\" /c:200 /rd:true /f:text");
        if (code != 0)
        {
            return new { wevtutil = code };
        }

        var events = text.Split("Event[", StringSplitOptions.RemoveEmptyEntries)
            .Where(e => e.Contains("ONENOTE.EXE", StringComparison.OrdinalIgnoreCase))
            .Select(e => new
            {
                id = Field(e, "Event ID:"),
                date = Field(e, "Date:"),
            })
            .ToList();
        return new { onenoteEvents = events.Count, latest = events.Take(5).ToList() };
    }

    private static string? Field(string block, string name)
    {
        var i = block.IndexOf(name, StringComparison.Ordinal);
        if (i < 0)
        {
            return null;
        }

        var start = i + name.Length;
        var end = block.IndexOf('\n', start);
        return (end < 0 ? block[start..] : block[start..end]).Trim();
    }

    internal static async Task<(int Code, string Text)> RunAsync(string file, string arguments)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true } };
        process.Start();
        var text = await process.StandardOutput.ReadToEndAsync(ProbeSession.Ct);
        await process.WaitForExitAsync(ProbeSession.Ct);
        return (process.ExitCode, text);
    }

    /// <summary>ONENOTE.EXE at the start (PID, start time, command line), held so its exit time and code can be read.</summary>
    private sealed class OneNoteWatcher(Process? process)
    {
        public static async Task<OneNoteWatcher> StartAsync(ProbeSession s)
        {
            var found = Process.GetProcessesByName("ONENOTE");
            var process = found.Length == 1 ? found[0] : null;
            string? commandLine = null;
            if (process is not null)
            {
                var (code, text) = await RunAsync(
                    "powershell",
                    $"-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={process.Id.ToString(CultureInfo.InvariantCulture)}').CommandLine\"");
                commandLine = code == 0 ? text.Trim() : null;
            }

            s.Recorder.Row("crash-onenote", "OneDrive", "-", null, null, new
            {
                processes = found.Length,
                pid = process?.Id,
                started = process is null ? null : Started(process),
                embedding = commandLine?.Contains("-Embedding", StringComparison.OrdinalIgnoreCase),
                commandLineArguments = commandLine is null ? null : Arguments(commandLine),
            });
            return new OneNoteWatcher(process);
        }

        /// <summary>Null while ONENOTE.EXE runs; once it has gone, when and with which exit code (null if Windows won't say).</summary>
        public object? Exited()
        {
            if (process is null || !process.HasExited)
            {
                return null;
            }

            int? code = null;
            DateTime? at = null;
            try
            {
                code = process.ExitCode;
                at = process.ExitTime.ToUniversalTime();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Not obtainable for this process: recorded as null.
            }

            return new { exitedUtc = at?.ToString("O", CultureInfo.InvariantCulture), exitCode = code, seenUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
        }

        private static string? Started(Process p)
        {
            try
            {
                return p.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>The command line without the executable's path (which holds no user name, but the arguments are what matter).</summary>
        private static string Arguments(string commandLine)
        {
            var trimmed = commandLine.TrimStart();
            var end = trimmed.StartsWith('"') ? trimmed.IndexOf('"', 1) : trimmed.IndexOf(' ', StringComparison.Ordinal);
            return end < 0 ? string.Empty : trimmed[(end + 1)..].Trim();
        }
    }
}
