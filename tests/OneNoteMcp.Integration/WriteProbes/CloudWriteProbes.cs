using System.Xml.Linq;
using OneNoteMcp.Com.Interop;
using Xunit;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Story 2.2, scenario <c>write-probes-cloud</c>: probes on MCP Test Cloud's section <c>Probes</c> (Text Page, Table
/// Page, Image Page, made by Phill). The notebook is found, never created; writes go through
/// <see cref="TestNotebook.WriteAsync{T}"/>. The order is fixed:
/// <list type="number">
/// <item>Cloud image, first, and only after a read confirms Image Page's image is <c>binaryUnavailable</c>. Phill
/// doesn't open Image Page before the run.</item>
/// <item>Width: 20 reads of Table Page, before anything writes to it.</item>
/// <item>Dates: Text Page (DeletePageContent first, on its existing text) and Table Page (UpdatePageContent first), each
/// while its page-XML and hierarchy values still differ, if they do.</item>
/// <item>Merged cells: an <c>xs2026</c> write of Table Page's table, then a 2013 round trip.</item>
/// </list>
/// </summary>
public sealed class CloudWriteProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "write-probes-cloud";
    public const string TextPageTitle = "Text Page";
    public const string TablePageTitle = "Table Page";
    public const string ImagePageTitle = "Image Page";

    private const string BinaryUnavailable = "0x8004200f";

    private static readonly XNamespace One26 = ProbeSession.One2026;

    [Fact]
    public async Task WriteProbesCloud()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output);
        var cloudId = await s.CloudIdAsync();
        var sectionId = TestNotebookGuard.FindSectionId((await s.TreeAsync(cloudId, OneNoteMcp.Core.HierarchyScope.Sections)).ToString(), ProbeSession.ProbesSectionName);
        Assert.SkipWhen(sectionId is null, "MCP Test Cloud has no Probes section.");
        var textPage = await s.PageIdByTitleAsync(sectionId!, TextPageTitle);
        var tablePage = await s.PageIdByTitleAsync(sectionId!, TablePageTitle);
        var imagePage = await s.PageIdByTitleAsync(sectionId!, ImagePageTitle);

        await s.StepAsync("cloud-image", () => imagePage is null ? Missing(s, "cloud-image", ImagePageTitle) : ImageAsync(s, imagePage));
        await s.StepAsync("cloud-width", () => tablePage is null ? Missing(s, "cloud-width", TablePageTitle) : LocalWriteProbes.RepeatReadsAsync(s, "width-cloud-table-page", "Width", tablePage));
        await s.StepAsync("cloud-dates-text", async () =>
        {
            if (textPage is null)
            {
                await Missing(s, "cloud-dates-text", TextPageTitle);
                return;
            }

            // Not displayed. A skipped pause runs as-is: the row records displayed.
            if (await IsViewedAsync(s, sectionId!, textPage))
            {
                await s.PauseAsync("cloud-dates-text-hide", "In OneNote, show a page other than Text Page (MCP Test Cloud > Probes), but not Image Page. Then continue.");
            }

            await DatesProbe.RunAsync(s, "cloud-text", sectionId!, textPage, deleteFirst: true, deleteExisting: true);
        });
        await s.StepAsync("cloud-dates-table", async () =>
        {
            if (tablePage is null)
            {
                await Missing(s, "cloud-dates-table", TablePageTitle);
                return;
            }

            // Displayed. A skipped pause runs as-is: the row records displayed.
            if (!await IsViewedAsync(s, sectionId!, tablePage))
            {
                await s.PauseAsync("cloud-dates-table-show", "In OneNote, show Table Page (MCP Test Cloud > Probes) and leave it shown. Don't type in it. Then continue.");
            }

            await DatesProbe.RunAsync(s, "cloud-table", sectionId!, tablePage, deleteFirst: false, deleteExisting: false);
        });
        await s.StepAsync("cloud-merged-cells", () => tablePage is null ? Missing(s, "cloud-merged-cells", TablePageTitle) : MergedCellsAsync(s, tablePage));

        s.Finish();
    }

    private static async Task<bool> IsViewedAsync(ProbeSession s, string sectionId, string pageId) =>
        (string?)(await s.HierarchyPageAsync(sectionId, pageId)).Attribute("isCurrentlyViewed") == "true";

    private static Task Missing(ProbeSession s, string probe, string title)
    {
        s.Recorder.Row(probe, "setup", "GetHierarchy", null, null, new { notProbed = $"no page titled {title} in MCP Test Cloud > Probes" });
        return Task.CompletedTask;
    }

    // --- Cloud image ----------------------------------------------------------------------------------------------

    private static async Task ImageAsync(ProbeSession s, string pageId)
    {
        const string Q = "Cloud image";
        var page = await s.PageAsync(pageId);
        var images = page.Descendants(One + "Image").ToList();
        var checks = new List<(XElement Image, string? CallbackId, Attempt Binary)>();
        foreach (var image in images)
        {
            var callbackId = (string?)image.Element(One + "CallbackID")?.Attribute("callbackID");
            var binary = callbackId is null
                ? new Attempt("not-run", null, null, null, 0, "no CallbackID")
                : await s.TryReadAsync("GetBinaryPageContent", app => app.GetBinaryPageContent(pageId, callbackId, out _));
            checks.Add((image, callbackId, binary));
        }

        var unavailable = checks.Where(c => c.Binary.HResult == BinaryUnavailable).ToList();
        s.Recorder.Row("cloud-image-precheck", Q, "GetBinaryPageContent", new { images = images.Count }, null, new
        {
            binaries = checks.Select(c => new { c.Binary.Outcome, c.Binary.HResult, pageLevel = c.Image.Parent?.Name.LocalName == "Page" }).ToList(),
            unavailable = unavailable.Count,
        });
        if (unavailable.Count == 0)
        {
            s.Recorder.Row("cloud-image-replace", Q, "UpdatePageContent", null, null, new { notProbed = "no image on Image Page is binaryUnavailable (already downloaded?)" });
            return;
        }

        var (image0, callback0, _) = unavailable[0];
        var outline = image0.Ancestors(One + "Outline").FirstOrDefault();
        if (outline is null)
        {
            s.Recorder.Row("cloud-image-replace", Q, "UpdatePageContent", null, null, new { notProbed = "the unavailable image is page-level, not in an Outline" });
            return;
        }

        var outlineId = ObjectId(outline)!;

        // A: the Outline resent as read (piBasic: the image with its CallbackID, no Data), plus one new OE.
        var resend = new XElement(outline);
        resend.Element(One + "OEChildren")!.Add(new XElement(One + "OE", new XElement(One + "T", new XCData("img-probe-a"))));
        var attempt = await s.UpdateAsync(pageId, LocalWriteProbes.PageOf(pageId, One, resend), DateTime.MinValue);
        await RecordImageAsync(s, "cloud-image-resend-outline", "the Outline as read (Image with CallbackID, no Data) + a new OE", attempt, pageId, outlineId, callback0);

        // B: a replacement Outline: the same content with every objectID stripped (what delete-then-add would send).
        var fresh = new XElement(outline);
        foreach (var e in fresh.DescendantsAndSelf())
        {
            e.Attribute("objectID")?.Remove();
        }

        fresh.Element(One + "OEChildren")!.Add(new XElement(One + "OE", new XElement(One + "T", new XCData("img-probe-b"))));
        attempt = await s.UpdateAsync(pageId, LocalWriteProbes.PageOf(pageId, One, fresh), DateTime.MinValue);
        var after = await s.PageAsync(pageId);
        var newOutline = OeByText(after, "img-probe-b") is { } b ? b.Ancestors(One + "Outline").FirstOrDefault() : null;
        s.Recorder.Row("cloud-image-new-outline", Q, "UpdatePageContent", new { shape = "a new Outline (objectIDs stripped) with the image as read (CallbackID, no Data)" }, attempt, new
        {
            newOutlineAdded = newOutline is not null,
            imagesInNewOutline = newOutline?.Descendants(One + "Image").Count(),
            originalOutlineImages = ById(after, outlineId)?.Descendants(One + "Image").Count(),
        });
    }

    private static async Task RecordImageAsync(ProbeSession s, string probe, string shape, Attempt attempt, string pageId, string outlineId, string? callbackId)
    {
        var after = await s.PageAsync(pageId);
        var outline = ById(after, outlineId);
        var callbacks = outline?.Descendants(One + "CallbackID").Select(c => (string?)c.Attribute("callbackID")).ToList() ?? [];
        var binary = callbackId is null ? null : await s.TryReadAsync("GetBinaryPageContent", app => app.GetBinaryPageContent(pageId, callbackId, out _));
        s.Recorder.Row(probe, "Cloud image", "UpdatePageContent", new { shape }, attempt, new
        {
            outlineKept = outline is not null,
            imagesInOutline = outline?.Descendants(One + "Image").Count(),
            sameCallbackId = callbacks.Contains(callbackId),
            binaryAfter = binary?.HResult ?? binary?.Outcome,
        });
    }

    // --- Merged cells ---------------------------------------------------------------------------------------------

    private static async Task MergedCellsAsync(ProbeSession s, string pageId)
    {
        const string Q = "Merged cells";
        var page26 = await s.PageAsync(pageId, XMLSchema.xs2026);
        var table26 = page26.Descendants(One26 + "Table").FirstOrDefault();
        var outline26 = table26?.Ancestors(One26 + "Outline").FirstOrDefault();
        var page13 = await s.PageAsync(pageId);
        var table13 = page13.Descendants(One + "Table").FirstOrDefault();
        s.Recorder.Row("merged-precheck", Q, "GetPageContent", new { schemas = "xs2026 and xs2013" }, null, new
        {
            spans2026 = Spans(page26),
            cells2013 = table13?.Elements(One + "Row").Select(r => r.Elements(One + "Cell").Select(c => Oes(c).Count(oe => !string.IsNullOrEmpty(Text(oe)))).ToList()).ToList(),
            cells2026 = table26?.Elements(One26 + "Row").Select(r => r.Elements(One26 + "Cell").Count()).ToList(),
        });
        if (outline26 is null || table13 is null)
        {
            s.Recorder.Row("merged-xs2026-update", Q, "UpdatePageContent", null, null, new { notProbed = "no table on Table Page" });
            return;
        }

        var outline13 = table13.Ancestors(One + "Outline").First();
        var authorDefs = page26.Elements().Where(e => e.Name.LocalName.StartsWith("Author", StringComparison.Ordinal)).ToArray();

        // A: the table's Outline as read in 2026, written back unchanged with xs2026.
        var attempt = await s.UpdateAsync(pageId, LocalWriteProbes.PageOf(pageId, One26, [.. authorDefs, outline26]), DateTime.MinValue, XMLSchema.xs2026, "UpdatePageContent(xs2026)");
        s.Recorder.Row("merged-xs2026-update", Q, "UpdatePageContent", new { schema = "xs2026", shape = "the table's Outline as read in 2026 (with the page's Author* definitions), unchanged" }, attempt, new
        {
            spans2026After = Spans(await s.PageAsync(pageId, XMLSchema.xs2026)),
        });

        // B: the 2013 round trip: the table's Outline as read in 2013, written back unchanged with xs2013.
        attempt = await s.UpdateAsync(pageId, LocalWriteProbes.PageOf(pageId, One, outline13), DateTime.MinValue);
        var after26 = await s.PageAsync(pageId, XMLSchema.xs2026);
        var spansAfter = Spans(after26);
        s.Recorder.Row("merged-xs2013-roundtrip", Q, "UpdatePageContent", new { schema = "xs2013", shape = "the table's Outline as read in 2013, unchanged" }, attempt, new
        {
            spans2026After = spansAfter,
            mergeSurvived = spansAfter.Count >= Spans(page26).Count && Spans(page26).Count > 0,
        });

        // C: if the merge was lost, put the 2026 Outline read at the start back with xs2026.
        if (Spans(page26).Count > 0 && spansAfter.Count < Spans(page26).Count)
        {
            attempt = await s.UpdateAsync(pageId, LocalWriteProbes.PageOf(pageId, One26, [.. authorDefs, outline26]), DateTime.MinValue, XMLSchema.xs2026, "UpdatePageContent(xs2026 remerge)");
            s.Recorder.Row("merged-xs2026-remerge", Q, "UpdatePageContent", new { schema = "xs2026", shape = "the 2026 Outline read before the round trip" }, attempt, new
            {
                spans2026After = Spans(await s.PageAsync(pageId, XMLSchema.xs2026)),
            });
        }
    }

    internal static List<string> Spans(XElement page26) =>
        page26.Descendants(One26 + "Cell")
            .Where(c => ((int?)c.Attribute("colSpan") ?? 1) > 1 || ((int?)c.Attribute("rowSpan") ?? 1) > 1)
            .Select(c => $"colSpan={(string?)c.Attribute("colSpan") ?? "-"},rowSpan={(string?)c.Attribute("rowSpan") ?? "-"}")
            .ToList();
}
