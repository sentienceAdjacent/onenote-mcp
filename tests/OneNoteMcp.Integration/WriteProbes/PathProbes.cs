using System.Diagnostics;
using System.Xml.Linq;
using Xunit;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Story 2.2, scenario <c>write-probes-paths</c>, read-only: the <c>path</c> of MCP Test and MCP Test Cloud and of
/// their sections, captured before and after each change Phill makes in a pause: close and reopen, a rename in OneNote
/// (then back), a rename of MCP Test's folder (then back), and last a rename of the OneDrive item (then back). Only
/// notebooks whose name or nickname starts with <c>MCP Test</c> are captured, and cloud paths are logged with
/// <c>&lt;cid&gt;</c>. Matrix row: Paths.
/// </summary>
public sealed class PathProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "write-probes-paths";

    private static readonly XNamespace One = ProbeXml.One;

    private Capture? initial;

    [Fact]
    public async Task WriteProbesPaths()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output);
        var folder = TestNotebook.ResolveFolder(Environment.GetEnvironmentVariable);
        var moved = folder + " Moved";

        await s.StepAsync("paths-initial", async () => initial = await CaptureAsync(s, "initial"));

        await ChangeAsync(s, "paths-close-reopen", "after-close-reopen",
            "In OneNote, close MCP Test and MCP Test Cloud (right-click each notebook > Close This Notebook). Then reopen both: "
            + $"MCP Test with File > Open > Browse, picking \"Open Notebook.onetoc2\" in {folder}; MCP Test Cloud with File > Open, from OneDrive. "
            + "Wait until both show their sections, then continue.");

        await ChangeAsync(s, "paths-rename-in-onenote", "after-rename-in-onenote",
            "In OneNote: rename the notebook MCP Test to \"MCP Test R\" and MCP Test Cloud to \"MCP Test Cloud R\" (right-click the notebook > "
            + "Properties > Display name, or Rename if offered); rename the section Seed (MCP Test) to \"Seed R\" and Probes (MCP Test Cloud) to "
            + "\"Probes R\" (right-click the section tab > Rename). Then continue.");
        await ChangeAsync(s, "paths-rename-back-in-onenote", "after-rename-back-in-onenote",
            "In OneNote, rename them all back: \"MCP Test\", \"MCP Test Cloud\", sections \"Seed\" and \"Probes\". Then continue.");

        await ChangeAsync(s, "paths-rename-local-folder", "after-rename-local-folder",
            $"Close MCP Test in OneNote. In File Explorer, rename the folder {folder} to \"{Path.GetFileName(moved)}\". "
            + $"Then reopen it in OneNote with File > Open > Browse, picking \"Open Notebook.onetoc2\" in {moved}. Then continue.");
        await ChangeAsync(s, "paths-rename-local-folder-back", "after-rename-local-folder-back",
            $"Close MCP Test in OneNote, rename the folder back to \"{Path.GetFileName(folder)}\", and reopen it from {folder}. Then continue.");

        // Last: the OneDrive rename may leave MCP Test Cloud needing a reopen.
        await ChangeAsync(s, "paths-rename-onedrive-item", "after-rename-onedrive-item",
            "In OneDrive on the web, rename the folder Documents/MCP Test Cloud to \"MCP Test Cloud Moved\". Wait about two minutes. "
            + "If OneNote then shows a sync error on MCP Test Cloud, close it and reopen it from OneDrive under its new name. Then continue.");
        await ChangeAsync(s, "paths-rename-onedrive-item-back", "after-rename-onedrive-item-back",
            "In OneDrive on the web, rename the folder back to \"MCP Test Cloud\". Wait about two minutes; if OneNote shows a sync error, "
            + "close MCP Test Cloud and reopen it from OneDrive (Documents/MCP Test Cloud). Then continue.");

        s.Finish();
    }

    private Task ChangeAsync(ProbeSession s, string step, string label, string instruction) =>
        s.StepAsync(step, async () =>
        {
            if (await s.PauseAsync(step, instruction))
            {
                await CaptureAsync(s, label);
            }
        });

    /// <summary>
    /// Waits (up to 180 s) until a local and a cloud MCP Test notebook are open, the cloud one with at least one
    /// section, then logs every candidate notebook's name, nickname, ID and path, and its sections' and groups' paths,
    /// with what changed since the initial capture.
    /// </summary>
    private async Task<Capture> CaptureAsync(ProbeSession s, string label)
    {
        var waited = Stopwatch.StartNew();
        List<NotebookCapture> notebooks;
        while (true)
        {
            notebooks = [];
            foreach (var nb in (await s.NotebooksAsync()).Elements(One + "Notebook").Where(IsCandidate))
            {
                var id = (string)nb.Attribute("ID")!;
                var tree = await s.TreeAsync(id, CoreScope.Sections);
                var nodes = tree.Descendants().Where(e => e.Name == One + "Section" || e.Name == One + "SectionGroup")
                    .Where(e => (string?)e.Attribute("isRecycleBin") != "true" && (string?)e.Attribute("isInRecycleBin") != "true")
                    .Select(e => new NodeCapture(e.Name.LocalName, (string?)e.Attribute("name"), (string?)e.Attribute("ID"), (string?)e.Attribute("path")))
                    .ToList();
                var path = (string?)nb.Attribute("path");
                notebooks.Add(new NotebookCapture(
                    path?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true ? "cloud" : "local",
                    (string?)nb.Attribute("name"),
                    (string?)nb.Attribute("nickname"),
                    id,
                    path,
                    nodes));
            }

            var ready = notebooks.Any(n => n.Role == "local") && notebooks.Any(n => n.Role == "cloud" && n.Nodes.Any(x => x.Kind == "Section"));
            if (ready || waited.Elapsed > TimeSpan.FromSeconds(180))
            {
                break;
            }

            await Task.Delay(5000, ProbeSession.Ct);
        }

        var capture = new Capture(label, notebooks);
        s.Recorder.Row("paths-" + label, "Paths", "GetHierarchy", new { scope = "notebooks, then sections per notebook" }, null, new
        {
            waitedMs = waited.ElapsedMilliseconds,
            notebooks,
            changesSinceInitial = initial is null ? null : Compare(initial, capture),
        });
        return capture;
    }

    private static bool IsCandidate(XElement nb) =>
        ((string?)nb.Attribute("name"))?.StartsWith("MCP Test", StringComparison.OrdinalIgnoreCase) == true
        || ((string?)nb.Attribute("nickname"))?.StartsWith("MCP Test", StringComparison.OrdinalIgnoreCase) == true;

    private static List<object> Compare(Capture before, Capture after)
    {
        var changes = new List<object>();
        foreach (var role in new[] { "local", "cloud" })
        {
            var a = before.Notebooks.FirstOrDefault(n => n.Role == role);
            var b = after.Notebooks.FirstOrDefault(n => n.Role == role);
            if (a is null || b is null)
            {
                changes.Add(new { role, present = new { before = a is not null, after = b is not null } });
                continue;
            }

            // Sections are matched by their position among the notebook's nodes (names may change in a rename probe).
            var nodes = a.Nodes.Zip(b.Nodes, (x, y) => new
            {
                before = x.Name,
                after = y.Name,
                pathChanged = !string.Equals(x.Path, y.Path, StringComparison.Ordinal),
                pathChangedIgnoringCase = !string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase),
                idChanged = x.Id != y.Id,
            }).ToList();
            changes.Add(new
            {
                role,
                nameChanged = a.Name != b.Name,
                nicknameChanged = a.Nickname != b.Nickname,
                idChanged = a.Id != b.Id,
                pathChanged = !string.Equals(a.Path, b.Path, StringComparison.Ordinal),
                pathChangedIgnoringCase = !string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase),
                nodeCount = new { before = a.Nodes.Count, after = b.Nodes.Count },
                nodes,
            });
        }

        return changes;
    }

    private sealed record Capture(string Label, List<NotebookCapture> Notebooks);

    private sealed record NotebookCapture(string Role, string? Name, string? Nickname, string Id, string? Path, List<NodeCapture> Nodes);

    private sealed record NodeCapture(string Kind, string? Name, string? Id, string? Path);
}
