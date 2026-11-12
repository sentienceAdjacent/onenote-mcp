using System.Security.Cryptography;
using System.Text;
using OneNoteMcp.Com.Interop;
using Xunit;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Story 5.1, scenario <c>write-probes-protected-publish</c> (constraint 17's deciding probe, Phill 2026-11-12): does
/// <c>Publish</c> of an unlocked password-protected section write its text to disk in the clear, before anything opens
/// the file? Run 3 proved only that an opened backup is plaintext, because opening it made OneNote rewrite it. Per
/// notebook: a throwaway section with two pages, which Phill protects at a pause and leaves unlocked; one
/// <c>pfOneNote</c> publish; the file's bytes searched for the page titles and the body text (UTF-16 and UTF-8) straight
/// after and again 60 s later, never opening it. On MCP Test the live section's own <c>.one</c> is searched the same way,
/// as the control. Nothing is deleted.
/// </summary>
public sealed class ProtectedPublishProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "write-probes-protected-publish";

    private const string BodyText = "probe text";

    [Fact]
    public async Task WriteProbesProtectedPublish()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.CloudProbeCallSeconds);
        var stamp = HierarchyProbes.Stamp();

        var local = new ProbeTarget("plain-local", await s.SetupAsync("setup", s.McpTestIdAsync), TestNotebook.NotebookName, Cloud: false);
        await RunAsync(s, local, stamp);

        var cloudId = await s.SetupAsync("setup-cloud", () => TestNotebook.FindCloudAsync(s.Gateway, ProbeSession.Ct));
        if (cloudId is null)
        {
            s.Recorder.Row("plain-cloud", "Backup", "-", null, null, new { notProbed = "MCP Test Cloud is not open" });
        }
        else
        {
            await RunAsync(s, new ProbeTarget("plain-cloud", cloudId, TestNotebook.CloudNotebookName, Cloud: true), stamp);
        }

        s.Finish();
    }

    private static async Task RunAsync(ProbeSession s, ProbeTarget t, string stamp)
    {
        var p = t.Label;
        var sectionName = $"probe plain {stamp}";
        var where = $"{t.NotebookName} > probe run {stamp}";
        string? run = null;
        string? section = null;
        var titles = new List<string>();

        await s.StepAsync($"{p}-setup", async () =>
        {
            run = await HierarchyProbes.RunGroupAsync(s, t, stamp);
            section = await s.MustCreateChildAsync(run, sectionName + ".one", CreateFileType.cftSection);
            for (var i = 1; i <= 2; i++)
            {
                var title = $"probe plain p{i} {stamp}";
                await s.CreateProbePageAsync(section, title);
                titles.Add(title);
            }
        });
        if (section is null || run is null)
        {
            s.Recorder.Row($"{p}-stopped", "Backup", "-", null, null, new { notProbed = $"no section: step {p}-setup didn't run or failed" });
            return;
        }

        var sectionId = section;
        var runId = run;
        var ready = false;

        await s.StepAsync($"{p}-protect", async () =>
        {
            await s.PauseAsync(
                $"{p}-protect",
                $"In OneNote, open {where} and password-protect the section \"{sectionName}\": right-click its tab > Password Protect This Section, with a password you'll remember for this sitting. "
                + $"Leave it unlocked (if it locks, unlock it). Then click the page {ProbeSession.SafePage(t.Cloud)} so the section is no longer on screen. Don't touch any other section. Then continue.");

            // Constraint 15: protecting gives the section a new ID, so find it by name. FindByNameAsync adds a
            // probe-named node to Created, so the guarded Publish accepts the new ID.
            var node = await s.NodeAsync(t.NotebookId, sectionId)
                ?? (await s.FindByNameAsync(t.NotebookId, sectionName)).FirstOrDefault(n => n.Kind == "Section" && !n.InRecycleBin && n.Id is not null);
            if (node?.Id is not null)
            {
                sectionId = node.Id;
            }

            ready = node?.Encrypted is "true" or "1";
            s.Recorder.Row($"{p}-protected", "Backup", "GetHierarchy", null, null, new { node, ready, idChanged = node?.Id != section });
        });
        if (!ready)
        {
            s.Recorder.Row($"{p}-stopped", "Backup", "-", null, null, new { notProbed = "the section isn't encrypted, so nothing was published" });
            return;
        }

        string? published = null;
        await s.StepAsync($"{p}-publish", async () =>
        {
            await s.EnsureNothingShownAsync(sectionId, "Publish");
            var element = await HierarchyProbes.NodeElementAsync(s, t, sectionId);
            var (tries, path, last) = await s.PublishAsync($"{p}-publish", sectionId, [(PublishFormat.pfOneNote, $"{p}-plain-{stamp}.one")]);
            published = path;
            s.Recorder.Row($"{p}-publish", "Backup", "Publish(pfOneNote)", new { source = "an unlocked protected section", locked = (string?)element.Attribute("locked") }, last, new { tries, disk = ProbeSession.Disk(path) });
        });

        if (published is not null)
        {
            var path = published;
            await s.StepAsync($"{p}-bytes-now", async () =>
            {
                var landed = await WaitForStableFileAsync(path);
                s.Recorder.Row($"{p}-bytes-now", "Backup", "-", new { file = Path.GetFileName(path), opened = false }, null, new { landed, scan = Scan(path, titles) });
            });

            await s.StepAsync($"{p}-bytes-later", async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ProbeSession.Ct);
                s.Recorder.Row($"{p}-bytes-later", "Backup", "-", new { file = Path.GetFileName(path), opened = false, afterSeconds = 60 }, null, new { scan = Scan(path, titles) });
            });
        }

        // The control: the live encrypted section's own .one (local notebooks only; a cloud path is a URL).
        await s.StepAsync($"{p}-bytes-live", async () =>
        {
            var file = (string?)(await HierarchyProbes.NodeElementAsync(s, t, sectionId)).Attribute("path");
            s.Recorder.Row($"{p}-bytes-live", "Backup", "GetHierarchy", null, null, file is not null && Path.IsPathFullyQualified(file) && File.Exists(file)
                ? new { file = (string?)Path.GetFileName(file), scan = (object)Scan(file, titles) }
                : new { file = (string?)null, scan = (object)"not a local file" });
        });

        await s.StepAsync($"{p}-end", () => s.EnsureNothingShownAsync(runId, "next-pass"));
    }

    /// <summary>Waits up to 15 s for the file to exist with the same size on two reads 500 ms apart (run 4: a package wasn't on disk straight after Publish).</summary>
    private static async Task<object> WaitForStableFileAsync(string path)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long previous = -1;
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (File.Exists(path))
            {
                var size = new FileInfo(path).Length;
                if (size > 0 && size == previous)
                {
                    return new { stable = true, ms = watch.ElapsedMilliseconds, bytes = size };
                }

                previous = size;
            }

            await Task.Delay(500, ProbeSession.Ct);
        }

        return new { stable = false, ms = watch.ElapsedMilliseconds, bytes = File.Exists(path) ? new FileInfo(path).Length : -1 };
    }

    /// <summary>
    /// Reads <paramref name="path"/> (shared, read-only; never through OneNote) and counts each probe string in UTF-16LE
    /// and UTF-8. Records counts and a hash, never the bytes.
    /// </summary>
    private static object Scan(string path, IReadOnlyList<string> titles)
    {
        if (!File.Exists(path))
        {
            return new { exists = false };
        }

        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var memory = new MemoryStream())
        {
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }

        var needles = titles.Append(BodyText).ToList();
        var hits = needles.Select(n => new
        {
            text = n,
            utf16 = Count(bytes, Encoding.Unicode.GetBytes(n)),
            utf8 = Count(bytes, Encoding.UTF8.GetBytes(n)),
        }).ToList();
        return new
        {
            exists = true,
            bytes = bytes.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes))[..16],
            plaintext = hits.Any(h => h.utf16 > 0 || h.utf8 > 0),
            hits,
        };
    }

    private static int Count(byte[] haystack, byte[] needle)
    {
        var count = 0;
        var span = haystack.AsSpan();
        for (var at = span.IndexOf(needle); at >= 0; at = span.IndexOf(needle))
        {
            count++;
            span = span[(at + needle.Length)..];
        }

        return count;
    }
}
