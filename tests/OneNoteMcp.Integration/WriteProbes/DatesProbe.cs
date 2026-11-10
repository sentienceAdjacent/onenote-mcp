using System.Xml.Linq;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Matrix row Dates: which <c>dateExpectedLastModified</c> each of <c>UpdatePageContent</c> and <c>DeletePageContent</c>
/// accepts. Before every attempt it re-reads the page-XML and hierarchy <c>lastModifiedTime</c> and the page's
/// <c>isCurrentlyViewed</c>, and sends one candidate: <c>stale</c> (an hour before the earlier of the two),
/// <c>hierarchy</c>, then <c>pageXml</c>. A rejected attempt leaves the page as it was, so the next candidate still
/// sees values that differ; an accepted one changes them, and the row records that. All dates are UTC.
/// </summary>
internal static class DatesProbe
{
    private static readonly string[] Candidates = ["stale", "hierarchy", "pageXml"];

    /// <param name="deleteFirst">Run the DeletePageContent attempts before the UpdatePageContent ones (to catch differing values first).</param>
    /// <param name="deleteExisting">
    /// Delete existing plain-text OEs on the page (no table, image or file below them) before preparing targets. False
    /// prepares each target with a setup update, which itself changes the page's dates.
    /// </param>
    public static async Task RunAsync(ProbeSession s, string label, string sectionId, string pageId, bool deleteFirst, bool deleteExisting)
    {
        var methods = deleteFirst ? new[] { "DeletePageContent", "UpdatePageContent" } : ["UpdatePageContent", "DeletePageContent"];
        // A per-run stamp in every marker, so a re-run never finds a marker an earlier run left on the page.
        var run = DateTime.UtcNow.ToString("MMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        var n = 0;
        foreach (var method in methods)
        {
            foreach (var candidate in Candidates)
            {
                n++;
                string? target = null;
                var prepared = false;
                if (method == "DeletePageContent")
                {
                    var current = await s.PageAsync(pageId);
                    target = deleteExisting ? ObjectId(DeletableOe(current)) : null;
                    if (target is null)
                    {
                        var marker = $"date-target-{label}-{run}-{n}";
                        await s.MustUpdateAsync(pageId, Outline(null, Oe(null, marker)));
                        target = ObjectId(OeByText(await s.PageAsync(pageId), marker));
                        prepared = true;
                    }
                }

                var page = await s.PageAsync(pageId);
                var node = await s.HierarchyPageAsync(sectionId, pageId);
                var pageLmt = Lmt(page);
                var hierarchyLmt = Lmt(node);
                var displayed = (string?)node.Attribute("isCurrentlyViewed") == "true";
                var p = pageLmt is null ? (DateTime?)null : ParseUtc(pageLmt);
                var h = hierarchyLmt is null ? (DateTime?)null : ParseUtc(hierarchyLmt);
                DateTime? date = candidate switch
                {
                    "stale" when p is not null && h is not null => (p < h ? p.Value : h.Value).AddHours(-1),
                    "hierarchy" => h,
                    "pageXml" => p,
                    _ => null,
                };

                var probe = $"dates-{label}-{method}-{candidate}";
                if (date is null)
                {
                    s.Recorder.Row(probe, "Dates", method, new { candidate }, null, new { notProbed = "the page or hierarchy lastModifiedTime is missing", pageLmt, hierarchyLmt });
                    continue;
                }

                var updateMarker = $"date-update-{label}-{run}-{n}";
                var attempt = method == "UpdatePageContent"
                    ? await s.UpdateAsync(pageId, Page(pageId, Outline(null, Oe(null, updateMarker))), date.Value)
                    : await s.DeleteContentAsync(pageId, target!, date.Value);

                var after = await s.PageAsync(pageId);
                var nodeAfter = await s.HierarchyPageAsync(sectionId, pageId);
                var applied = method == "UpdatePageContent" ? OeByText(after, updateMarker) is not null : ById(after, target!) is null;
                s.Recorder.Row(probe, "Dates", method, new { candidate, dateSent = FormatUtc(date.Value), dateKind = date.Value.Kind.ToString() }, attempt, new
                {
                    pageXmlLmt = pageLmt,
                    hierarchyLmt,
                    differ = p != h,
                    displayed,
                    targetPrepared = prepared,
                    applied,
                    pageXmlLmtAfter = Lmt(after),
                    hierarchyLmtAfter = Lmt(nodeAfter),
                });
            }
        }
    }

    /// <summary>The last plain-text OE on the page (outside the title, not a list item, with no table, image, ink or file below it), or null.</summary>
    private static XElement? DeletableOe(XElement page) =>
        Oes(page)
            .Where(oe => oe.Element(oe.Name.Namespace + "OEChildren") is null)
            .Where(oe => oe.Element(oe.Name.Namespace + "List") is null) // list items stay for skill-list-claim-ui-page
            .Where(oe => !oe.Descendants().Any(d => d.Name.LocalName is "Table" or "Image" or "InsertedFile" or "InkDrawing" or "InkWord" or "MediaFile"))
            .Where(oe => !oe.Ancestors(oe.Name.Namespace + "Table").Any())
            .LastOrDefault();
}
