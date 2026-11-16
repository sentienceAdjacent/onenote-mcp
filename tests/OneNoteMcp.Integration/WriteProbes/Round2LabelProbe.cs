using System.Xml.Linq;
using OneNoteMcp.Com.Interop;
using Xunit;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Spike 3.1, scenario <c>write-probes-r2-label</c> (Q5, last in sitting B): which <c>Publish</c> formats return
/// <c>0x80042031</c> hrPublishFormatUnsupportedForLabels once a section carries a sensitivity label? It runs only on
/// <c>MCP Test Tenant</c>, a notebook Phill makes on his Office 365 tenant at the first pause; the guard adopts it by
/// that exact name and an <c>https</c> path off OneDrive personal (<see cref="TestNotebookGuard.IsTenantTest"/>), and
/// nothing else on the tenant is ever written. Every URL, and the tenant notebook's path, is written as
/// <c>tenant notebook</c> in rows and echo lines (<see cref="ProbeRecorder.RedactUrlsFromNowOn"/>).
/// </summary>
public sealed class Round2LabelProbe(ITestOutputHelper output)
{
    public const string ScenarioName = "write-probes-r2-label";

    private const string Q = "Labels";

    [Fact]
    public async Task WriteProbesR2Label()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output, ProbeSession.CloudProbeCallSeconds);
        s.Recorder.RedactUrlsFromNowOn(); // before the first row: no tenant URL may reach the log
        var stamp = HierarchyProbes.Stamp();
        string? notebook = null;
        string? section = null;
        string? page = null;

        await s.StepAsync("label-tenant", async () =>
        {
            var (id, path) = await s.AdoptTenantAsync();
            if (id is null)
            {
                await s.PauseAsync(
                    "label-create",
                    $"In OneNote, create a new notebook on your Office 365 work or school account (File > New, then that account's OneDrive or SharePoint), named exactly \"{TestNotebook.TenantNotebookName}\". "
                    + "Give it one section named \"probe label\", and leave it empty. Wait until it shows in OneNote. Don't open or change any other notebook on that account. Then continue.");
                (id, path) = await s.AdoptTenantAsync();
            }

            if (path is not null)
            {
                s.Recorder.RedactUrlsFromNowOn(path);
            }

            notebook = id;
            section = id is null ? null : FirstSection(await s.TreeAsync(id, OneNoteMcp.Core.HierarchyScope.Sections));
            s.Recorder.Row("lb-tenant", "Setup", "GetHierarchy", null, null, new
            {
                found = id is not null,
                notebook = TestNotebook.TenantNotebookName,
                sectionFound = section is not null,
                note = "exactly one open notebook named MCP Test Tenant with an https path off OneDrive personal; its path is redacted",
            });
        });
        if (notebook is null || section is null)
        {
            s.Recorder.Row("lb", Q, "Publish", null, null, new { notProbed = "the tenant notebook, or its section, wasn't found" });
            s.Finish();
            return;
        }

        var sectionId = section;
        await s.StepAsync("label-baseline", async () =>
        {
            page = await s.CreateProbePageAsync(sectionId, $"probe label page {stamp}");
            foreach (var format in new[] { PublishFormat.pfPDF, PublishFormat.pfOneNote })
            {
                var landing = await s.PublishFreshAsync($"lb-baseline-page-{format}-{stamp}", page, format, $"probe-lb-page{Round2ExportProbes.Extension(format)}");
                s.Recorder.Row($"lb-baseline-page-{format}", Q, $"Publish({format})", new { scope = "page", labelled = false }, landing.Attempt, landing.Summary);
            }

            await CheckSyncAsync(s, "label-baseline");
        });

        var labelled = false;
        await s.StepAsync("label-apply", async () =>
        {
            labelled = await s.PauseAsync(
                "label-apply",
                $"In OneNote, open the section \"probe label\" in \"{TestNotebook.TenantNotebookName}\" and apply a sensitivity label to it (Home > Sensitivity, or the Sensitivity button). "
                + "Pick any label your organisation offers; one that encrypts or restricts export is the most telling. Tell the team lead whether the label you picked encrypts or restricts the content, but not its name (it can identify the organisation). "
                + "Then click a page in another notebook so the section isn't on screen, and continue. If no sensitivity label is offered, replace this file's content with skip.");
            var node = await s.NodeAsync(notebook, sectionId);
            s.Recorder.Row("lb-label", Q, "GetHierarchy", null, null, labelled
                ? new { labelled, node = (object?)new { node?.Name, node?.Encrypted, node?.Locked, node?.LastModifiedTime }, attributeNames = (object?)(await AttributesAsync(s, notebook, sectionId)) }
                : new { labelled, node = (object?)null, attributeNames = (object?)"not probed: the tenant offers no sensitivity label (pause skipped)" });
        });

        if (!labelled || page is null)
        {
            s.Recorder.Row("lb-publish", Q, "Publish", null, null, new { notProbed = labelled ? "no probe page (step label-baseline didn't run)" : "no sensitivity label applied (the label pause was skipped)" });
            s.Finish();
            return;
        }

        var pageId = page;
        await s.StepAsync("label-publish", async () =>
        {
            var summary = new List<object>();
            foreach (var (scope, id) in new[] { ("page", pageId), ("section", sectionId) })
            {
                foreach (var format in Round2ExportProbes.AllFormats)
                {
                    if (format == PublishFormat.pfOneNote2007)
                    {
                        // Sitting A (2026-11-16): Publish(pfOneNote2007) ended ONENOTE.EXE every time, so it is never sent to
                        // the tenant notebook either.
                        s.Recorder.Row($"lb-{scope}-{format}", Q, $"Publish({format})", new { scope, labelled = true }, null, new { notProbed = "pfOneNote2007 crashes OneNote (sitting A); not sent" });
                        summary.Add(new { scope, format = format.ToString(), Outcome = "notProbed", HResult = (string?)null, landed = false });
                        continue;
                    }

                    var landing = await s.PublishFreshAsync($"lb-{scope}-{format}-{stamp}", id, format, $"probe-lb-{scope}{Round2ExportProbes.Extension(format)}");
                    s.Recorder.Row($"lb-{scope}-{format}", Q, $"Publish({format})", new { scope, labelled = true }, landing.Attempt, landing.Summary);
                    summary.Add(new { scope, format = format.ToString(), landing.Attempt.Outcome, landing.Attempt.HResult, landed = landing.Landing.TargetExists });
                }
            }

            s.Recorder.Row("lb-summary", Q, "Publish", null, null, new { summary });
            await CheckSyncAsync(s, "label-publish");
        });

        s.Finish();
    }

    /// <summary>
    /// The plan's Sync Now check after a cloud step, worded for the tenant notebook only (CloudJamRepro.CheckSyncAsync
    /// names MCP Test Cloud).
    /// </summary>
    private static Task<bool> CheckSyncAsync(ProbeSession s, string step) => s.PauseAsync(
        $"{step}-check-sync",
        $"In OneNote, open File > Info > View Sync Status, press Sync Now on \"{TestNotebook.TenantNotebookName}\" only, and wait about 30 seconds. "
        + "Tell the team lead whether it says \"Up to date\" or shows an error (for example 0xE0000644), with its code. Don't sync or open any other notebook on that account. Then continue.");

    /// <summary>The first section of the tenant notebook that isn't in its recycle bin.</summary>
    internal static string? FirstSection(XElement notebook) =>
        notebook.Descendants(notebook.Name.Namespace + "Section")
            .Where(e => !NodeInfo.From(e).InRecycleBin)
            .Select(e => (string?)e.Attribute("ID"))
            .FirstOrDefault();

    /// <summary>
    /// The section's attribute names only (a label may show as one). Never the values: a label's name or ID can
    /// identify the organisation.
    /// </summary>
    private static async Task<List<string>> AttributesAsync(ProbeSession s, string notebook, string sectionId) =>
        ById(await s.TreeAsync(notebook, OneNoteMcp.Core.HierarchyScope.Sections), sectionId)?.Attributes()
            .Select(a => a.Name.ToString())
            .ToList()
            ?? [];
}
