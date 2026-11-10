using System.Globalization;
using System.Xml.Linq;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Xml;
using Xunit;
using static OneNoteMcp.Integration.WriteProbes.ProbeXml;

namespace OneNoteMcp.Integration.WriteProbes;

/// <summary>
/// Story 2.2, scenario <c>write-probes-local</c>: page-write behaviour on MCP Test (section <c>Probes</c>, which Phill
/// creates in OneNote if it's missing). Every write goes through <see cref="TestNotebook.WriteAsync{T}"/>. Each probe
/// makes its own page, so a re-run starts clean; probe pages are left in place (no <c>DeleteHierarchy</c>).
/// Hitl: the displayed-page date probe and the modal-dialog probe pause for Phill (see <see cref="ProbeSession"/>).
/// Matrix rows: Merge, Ignored edits, Side effects, Modal dialog, HRESULTs, Dates (local), Width (fresh table), pathCache.
/// </summary>
public sealed class LocalWriteProbes(ITestOutputHelper output)
{
    public const string ScenarioName = "write-probes-local";

    [Fact]
    public async Task WriteProbesLocal()
    {
        ProbeSession.RequireScenario(ScenarioName);
        using var s = new ProbeSession(ScenarioName, output);
        var notebookId = await s.McpTestIdAsync();
        var sectionId = await s.SectionIdAsync(notebookId, TestNotebook.NotebookName, ProbeSession.ProbesSectionName);
        var stamp = DateTime.UtcNow.ToString("MMdd-HHmmss", CultureInfo.InvariantCulture);

        await s.StepAsync("merge", () => MergeAsync(s, sectionId, stamp));
        await s.StepAsync("ignored-oe-move", () => IgnoredOeMoveAsync(s, sectionId, stamp));
        await s.StepAsync("ignored-table-columns", () => IgnoredTableColumnsAsync(s, sectionId, stamp));
        await s.StepAsync("ignored-position-size", () => IgnoredPositionSizeAsync(s, sectionId, stamp));
        await s.StepAsync("ignored-quickstyleindex", () => IgnoredQuickStyleIndexAsync(s, sectionId, stamp));
        await s.StepAsync("side-untouched-children-lmt", () => SideUntouchedChildrenAsync(s, sectionId, stamp));
        await s.StepAsync("side-local-style", () => SideLocalStyleAsync(s, sectionId, stamp));
        await s.StepAsync("side-nbsp", () => SideNbspAsync(s, sectionId, stamp));
        await s.StepAsync("width-fresh-table", () => WidthFreshTableAsync(s, sectionId, stamp));
        await s.StepAsync("pathcache-ballast-files", () => PathCacheAsync(s, notebookId));
        await s.StepAsync("hresults-local", () => HResultTriggersAsync(s, sectionId, stamp));
        await s.StepAsync("dates-local-not-displayed", () => DatesLocalAsync(s, sectionId, stamp, displayed: false));
        await s.StepAsync("dates-local-displayed", () => DatesLocalAsync(s, sectionId, stamp, displayed: true));
        await s.StepAsync("modal-dialog", () => ModalDialogAsync(s, notebookId, sectionId, stamp));

        s.Finish();
    }

    // --- Merge ----------------------------------------------------------------------------------------------------

    private static async Task MergeAsync(ProbeSession s, string sectionId, string stamp)
    {
        const string Q = "Merge";
        var pageId = await s.CreatePageAsync(sectionId, $"probe merge {stamp}");
        await s.MustUpdateAsync(pageId, Outline(null, Oe(null, "m-a"), Oe(null, "m-b"), Oe(null, "m-c")));
        var page = await s.PageAsync(pageId);
        var aId = ObjectId(OeByText(page, "m-a"))!;
        var bId = ObjectId(OeByText(page, "m-b"))!;
        var cId = ObjectId(OeByText(page, "m-c"))!;
        var outlineId = OutlineOf(OeByText(page, "m-a")!)!;

        // A partial update that omits OEs b and c.
        var attempt = await s.UpdateAsync(pageId, Page(pageId, Outline(outlineId, Oe(aId, "m-a edited"))), DateTime.MinValue);
        page = await s.PageAsync(pageId);
        s.Recorder.Row("merge-omit-oe", Q, "UpdatePageContent", new { shape = "Outline(objectID) holding only OE a (text edited); OEs b and c omitted" }, attempt, new
        {
            aEdited = Text(ById(page, aId)!) == "m-a edited",
            bKept = ById(page, bId) is not null,
            cKept = ById(page, cId) is not null,
            outlineOrder = OutlineTexts(page, outlineId),
        });

        // An OE without an objectID next to an existing one.
        attempt = await s.UpdateAsync(pageId, Page(pageId, Outline(outlineId, Oe(aId, "m-a edited"), Oe(null, "m-d"))), DateTime.MinValue);
        page = await s.PageAsync(pageId);
        var d = OesByText(page, "m-d");
        s.Recorder.Row("merge-add-oe-no-objectid", Q, "UpdatePageContent", new { shape = "Outline(objectID): OE a (objectID), then a new OE d (no objectID)" }, attempt, new
        {
            dAdded = d.Count,
            dInSameOutline = d.Count > 0 && d.All(x => OutlineOf(x) == outlineId),
            outlineOrder = OutlineTexts(page, outlineId),
            bKept = ById(page, bId) is not null,
        });

        // An Outline without an objectID.
        var outlinesBefore = Outlines(page).Count();
        attempt = await s.UpdateAsync(pageId, Page(pageId, Outline(null, Oe(null, "m-e"))), DateTime.MinValue);
        page = await s.PageAsync(pageId);
        s.Recorder.Row("merge-add-outline-no-objectid", Q, "UpdatePageContent", new { shape = "a new Outline (no objectID) with one OE" }, attempt, new
        {
            outlinesBefore,
            outlinesAfter = Outlines(page).Count(),
            eAdded = OeByText(page, "m-e") is not null,
            firstOutlineUnchanged = OutlineTexts(page, outlineId),
        });

        // Replace = delete then add: DeletePageContent on one OE, then on the whole Outline.
        attempt = await s.DeleteContentAsync(pageId, bId, DateTime.MinValue);
        page = await s.PageAsync(pageId);
        s.Recorder.Row("merge-delete-oe", Q, "DeletePageContent", new { target = "OE b (objectID)", date = "MinValue" }, attempt, new
        {
            bGone = ById(page, bId) is null,
            aKept = ById(page, aId) is not null,
            cKept = ById(page, cId) is not null,
        });

        attempt = await s.DeleteContentAsync(pageId, outlineId, DateTime.MinValue);
        page = await s.PageAsync(pageId);
        s.Recorder.Row("merge-delete-outline", Q, "DeletePageContent", new { target = "the Outline (objectID)", date = "MinValue" }, attempt, new
        {
            outlineGone = ById(page, outlineId) is null,
            itsOesGone = ById(page, aId) is null && ById(page, cId) is null,
            otherOutlineKept = OeByText(page, "m-e") is not null,
        });
    }

    // --- Ignored edits --------------------------------------------------------------------------------------------

    private static async Task IgnoredOeMoveAsync(ProbeSession s, string sectionId, string stamp)
    {
        var pageId = await s.CreatePageAsync(sectionId, $"probe oe-move {stamp}");
        await s.MustUpdateAsync(pageId, OutlineWith(null, Position(40, 120), Oe(null, "i-1"), Oe(null, "i-2")));
        await s.MustUpdateAsync(pageId, OutlineWith(null, Position(40, 320), Oe(null, "i-3")));
        var beforeXml = await s.PageXmlAsync(pageId);
        var page = XDocument.Parse(beforeXml).Root!;
        var e1 = ObjectId(OeByText(page, "i-1"))!;
        var e2 = ObjectId(OeByText(page, "i-2"))!;
        var e3 = ObjectId(OeByText(page, "i-3"))!;
        var o1 = OutlineOf(OeByText(page, "i-1")!)!;
        var o2 = OutlineOf(OeByText(page, "i-3")!)!;

        var xml = Page(pageId, Outline(o1, Oe(e1, "i-1")) + Outline(o2, Oe(e3, "i-3"), Oe(e2, "i-2")));
        var attempt = await s.UpdateAsync(pageId, xml, DateTime.MinValue);
        var afterXml = await s.PageXmlAsync(pageId);
        var after = XDocument.Parse(afterXml).Root!;
        var e2After = ById(after, e2);
        var outlineOfE2 = e2After is null ? "gone" : OutlineOf(e2After) == o1 ? "O1" : OutlineOf(e2After) == o2 ? "O2" : "other";
        RecordIgnored(s, "ignored-oe-move", "UpdatePageContent", new { shape = "O1 sent with OE 1 only; O2 sent with OE 3 then OE 2 (OE 2's objectID)" }, attempt, beforeXml, afterXml, [o1, o2], new
        {
            applied = outlineOfE2 == "O2" && OesByText(after, "i-2").Count == 1,
            outlineOfE2,
            i2Count = OesByText(after, "i-2").Count,
            o1Order = OutlineTexts(after, o1),
            o2Order = OutlineTexts(after, o2),
        });
    }

    private static async Task IgnoredTableColumnsAsync(ProbeSession s, string sectionId, string stamp)
    {
        var pageId = await s.CreatePageAsync(sectionId, $"probe table-columns {stamp}");
        await s.MustUpdateAsync(pageId, Outline(null, OeRaw(null, Table([(80, false), (80, false)], ["t-1", "t-2"]))));
        var beforeXml = await s.PageXmlAsync(pageId);
        var page = XDocument.Parse(beforeXml).Root!;
        var table = page.Descendants(One + "Table").First();
        var outline = new XElement(table.Ancestors(One + "Outline").First());
        var tableCopy = outline.Descendants(One + "Table").First();
        tableCopy.Element(One + "Columns")!.Add(new XElement(One + "Column", new XAttribute("index", 2), new XAttribute("width", "80.0")));
        tableCopy.Element(One + "Row")!.Add(new XElement(One + "Cell", new XElement(One + "OEChildren", new XElement(One + "OE", new XElement(One + "T", new XCData("t-3"))))));

        var attempt = await s.UpdateAsync(pageId, PageOf(pageId, One, outline), DateTime.MinValue);
        var afterXml = await s.PageXmlAsync(pageId);
        var after = XDocument.Parse(afterXml).Root!;
        var afterTable = after.Descendants(One + "Table").FirstOrDefault();
        var columns = afterTable?.Descendants(One + "Column").Count() ?? 0;
        var cells = afterTable?.Element(One + "Row")?.Elements(One + "Cell").Count() ?? 0;
        RecordIgnored(s, "ignored-table-columns", "UpdatePageContent", new { shape = "the table's Outline as read, with a third Column and a third Cell (no objectID) added" }, attempt, beforeXml, afterXml, [ObjectId(outline)!], new
        {
            applied = columns == 3 && cells == 3,
            columnsAfter = columns,
            cellsInRowAfter = cells,
            t3Present = OeByText(after, "t-3") is not null,
            tables = after.Descendants(One + "Table").Count(),
        });
    }

    private static async Task IgnoredPositionSizeAsync(ProbeSession s, string sectionId, string stamp)
    {
        var pageId = await s.CreatePageAsync(sectionId, $"probe position-size {stamp}");
        await s.MustUpdateAsync(pageId, OutlineWith(null, Position(100, 150) + Size(300, 40), Oe(null, "p-1")));
        var beforeXml = await s.PageXmlAsync(pageId);
        var page = XDocument.Parse(beforeXml).Root!;
        var oe = OeByText(page, "p-1")!;
        var outlineId = OutlineOf(oe)!;
        var before = Layout(page, outlineId);

        var attempt = await s.UpdateAsync(pageId, Page(pageId, OutlineWith(outlineId, Position(260, 320) + Size(420, 40), Oe(ObjectId(oe), "p-1"))), DateTime.MinValue);
        var afterXml = await s.PageXmlAsync(pageId);
        var after = Layout(XDocument.Parse(afterXml).Root!, outlineId);
        RecordIgnored(s, "ignored-position-size", "UpdatePageContent", new { shape = "Outline(objectID) with Position 260,320 and Size width 420 (was 100,150 / 300)" }, attempt, beforeXml, afterXml, [outlineId], new
        {
            positionApplied = Near(after.X, 260) && Near(after.Y, 320),
            sizeApplied = Near(after.Width, 420),
            before,
            after,
        });
    }

    private static async Task IgnoredQuickStyleIndexAsync(ProbeSession s, string sectionId, string stamp)
    {
        var pageId = await s.CreatePageAsync(sectionId, $"probe quickstyle {stamp}");
        await s.MustUpdateAsync(pageId, Outline(null, Oe(null, "q-1")));
        var page = await s.PageAsync(pageId);
        var next = page.Elements(One + "QuickStyleDef").Select(d => (int?)d.Attribute("index") ?? 0).DefaultIfEmpty(-1).Max() + 1;

        // Define h1 and use it on a new OE (setup), so an h1 index exists on the page.
        var h1Def = string.Create(CultureInfo.InvariantCulture, $"""<one:QuickStyleDef index="{next}" name="h1" fontColor="#1E4E79" highlightColor="automatic" font="Calibri Light" fontSize="16.0" spaceBefore="0.0" spaceAfter="0.0" />""");
        var setup = await s.UpdateAsync(pageId, Page(pageId, h1Def + Outline(null, Oe(null, "q-new", string.Create(CultureInfo.InvariantCulture, $" quickStyleIndex=\"{next}\"")))), DateTime.MinValue);
        var beforeXml = await s.PageXmlAsync(pageId);
        page = XDocument.Parse(beforeXml).Root!;
        var qNew = OeByText(page, "q-new");
        var h1Index = page.Elements(One + "QuickStyleDef").Where(d => (string?)d.Attribute("name") == "h1").Select(d => (string?)d.Attribute("index")).FirstOrDefault();
        s.Recorder.Row("ignored-quickstyleindex-setup", "Ignored edits", "UpdatePageContent", new { shape = "a new QuickStyleDef h1 and a new OE using it" }, setup, new
        {
            newOeStyle = qNew is null ? null : StyleName(page, qNew),
            h1Index,
        });
        if (h1Index is null)
        {
            return;
        }

        var q1 = OeByText(page, "q-1")!;
        var attempt = await s.UpdateAsync(pageId, Page(pageId, Outline(OutlineOf(q1), Oe(ObjectId(q1), "q-1", $" quickStyleIndex=\"{h1Index}\""))), DateTime.MinValue);
        var afterXml = await s.PageXmlAsync(pageId);
        var after = XDocument.Parse(afterXml).Root!;
        var styleAfter = StyleName(after, ById(after, ObjectId(q1)!)!);
        RecordIgnored(s, "ignored-quickstyleindex", "UpdatePageContent", new { shape = "existing OE (objectID) with quickStyleIndex changed to h1's" }, attempt, beforeXml, afterXml, [OutlineOf(q1)!], new
        {
            applied = styleAfter == "h1",
            styleBefore = StyleName(page, q1),
            styleAfter,
        });
    }

    // --- Side effects ---------------------------------------------------------------------------------------------

    private static async Task SideUntouchedChildrenAsync(ProbeSession s, string sectionId, string stamp)
    {
        const string Q = "Side effects";
        var pageId = await s.CreatePageAsync(sectionId, $"probe untouched-lmt {stamp}");
        var nested = OeRaw(null, $"<one:T>{Cdata("s-b")}</one:T><one:OEChildren>{Oe(null, "s-b1")}</one:OEChildren>");
        await s.MustUpdateAsync(pageId, Outline(null, Oe(null, "s-a"), nested, Oe(null, "s-c")));

        // Variant 1 first: the whole Outline as read, with OE a's text edited (what a read-modify-write sends). The
        // partial variant deletes the OEs it omits (merge-omit-oe), so it must run second or nothing is left untouched.
        var page = await s.PageAsync(pageId);
        var outlineId = OutlineOf(OeByText(page, "s-a")!)!;
        var aId = ObjectId(OeByText(page, "s-a"))!;
        var before = Lmts(page, outlineId);
        var outline = new XElement(page.Descendants(One + "Outline").First(o => ObjectId(o) == outlineId));
        outline.Descendants(One + "OE").First(oe => ObjectId(oe) == aId).Element(One + "T")!.ReplaceNodes(new XCData("s-a edited"));
        await Task.Delay(2500, ProbeSession.Ct);
        var attempt = await s.UpdateAsync(pageId, PageOf(pageId, One, outline), DateTime.MinValue);
        var after = await s.PageAsync(pageId);
        s.Recorder.Row("side-untouched-lmt-whole-outline", Q, "UpdatePageContent", new { shape = "the whole Outline as read, OE a's text edited" }, attempt, new
        {
            changed = Changed(before, Lmts(after, outlineId)),
            before,
            after = Lmts(after, outlineId),
        });

        // Variant 2: a partial update naming only OE a (b, b1 and c omitted).
        before = Lmts(after, outlineId);
        await Task.Delay(2500, ProbeSession.Ct);
        attempt = await s.UpdateAsync(pageId, Page(pageId, Outline(outlineId, Oe(aId, "s-a edited twice"))), DateTime.MinValue);
        var after2 = await s.PageAsync(pageId);
        s.Recorder.Row("side-untouched-lmt-partial", Q, "UpdatePageContent", new { shape = "Outline(objectID) with only OE a's text edited" }, attempt, new
        {
            changed = Changed(before, Lmts(after2, outlineId)),
            before,
            after = Lmts(after2, outlineId),
        });
    }

    private static async Task SideLocalStyleAsync(ProbeSession s, string sectionId, string stamp)
    {
        var pageId = await s.CreatePageAsync(sectionId, $"probe local-style {stamp}");
        await s.MustUpdateAsync(pageId, Outline(null, Oe(null, "st-0")));
        var page = await s.PageAsync(pageId);
        var p = page.Elements(One + "QuickStyleDef").FirstOrDefault(d => (string?)d.Attribute("name") == "p");
        var font = (string?)p?.Attribute("font") ?? "Calibri";
        var size = (string?)p?.Attribute("fontSize") ?? "11.0";
        var duplicate = $"font-family:{font};font-size:{size}pt";
        var pIndex = (string?)p?.Attribute("index");
        var styleAttr = $" style=\"{Attr(duplicate)}\"" + (pIndex is null ? string.Empty : $" quickStyleIndex=\"{pIndex}\"");

        var attempt = await s.UpdateAsync(pageId, Page(pageId, Outline(
            null,
            Oe(null, "st-oe", styleAttr),
            Oe(null, $"<span style='{duplicate}'>st-span</span>"),
            Oe(null, "st-control", " style=\"font-family:Consolas;font-size:9.0pt\""))), DateTime.MinValue);
        var after = await s.PageAsync(pageId);
        var oe = OeByText(after, "st-oe");
        var span = OeByText(after, "st-span");
        var control = OeByText(after, "st-control");
        s.Recorder.Row("side-local-style-duplicate", "Side effects", "UpdatePageContent", new { shape = "new OEs: OE@style equal to QuickStyleDef p; a T span with that style; a control OE@style that differs", pDef = new { font, size, pIndex } }, attempt, new
        {
            oeStyleKept = (string?)oe?.Attribute("style"),
            spanText = span is null ? null : Text(span),
            controlStyleKept = (string?)control?.Attribute("style"),
        });
    }

    private static async Task SideNbspAsync(ProbeSession s, string sectionId, string stamp)
    {
        var pageId = await s.CreatePageAsync(sectionId, $"probe nbsp {stamp}");
        foreach (var (probe, text) in new[] { ("side-nbsp-entity", "nb-1&nbsp;x"), ("side-nbsp-numeric", "nb-2&#160;x"), ("side-nbsp-literal", "nb-3 x") })
        {
            var marker = text[..4];
            var attempt = await s.UpdateAsync(pageId, Page(pageId, Outline(null, Oe(null, text))), DateTime.MinValue);
            var after = await s.PageAsync(pageId);
            var readBack = OeByText(after, marker) is { } oe ? Text(oe) : null;
            s.Recorder.Row(probe, "Side effects", "UpdatePageContent", new { cdata = text }, attempt, new
            {
                added = readBack is not null,
                readBack,
                containsNbspChar = readBack?.Contains(' ', StringComparison.Ordinal),
            });
        }
    }

    // --- Width and pathCache --------------------------------------------------------------------------------------

    private static async Task WidthFreshTableAsync(ProbeSession s, string sectionId, string stamp)
    {
        var pageId = await s.CreatePageAsync(sectionId, $"probe width {stamp}");
        await s.MustUpdateAsync(pageId, Outline(null, OeRaw(null, Table([(105.51, true), (80, false), (120, false)], ["w-1", "w-2", "w-3"], ["w-4", "w-5", "w-6"]))));
        await RepeatReadsAsync(s, "width-fresh-mcp-test-table", "Width", pageId);
    }

    private static async Task PathCacheAsync(ProbeSession s, string notebookId)
    {
        var ballast = TestNotebookGuard.FindSectionId((await s.TreeAsync(notebookId, OneNoteMcp.Core.HierarchyScope.Sections)).ToString(), BallastContent.SectionName);
        var pageId = ballast is null ? null : await s.PageIdByTitleAsync(ballast, BallastContent.FilesTitle);
        if (pageId is null)
        {
            s.Recorder.Row("pathcache-ballast-files", "pathCache", "GetPageContent", null, null, new { notProbed = "Ballast Files page not found in MCP Test (run the ballast scenario first)" });
            return;
        }

        await RepeatReadsAsync(s, "pathcache-ballast-files", "pathCache", pageId);
    }

    /// <summary>
    /// 20 piBasic reads of one page: per read, the column widths of its first table, a fingerprint of its
    /// InsertedFile pathCache values, and the page hash with and without unlocked widths and pathCache.
    /// </summary>
    internal static async Task RepeatReadsAsync(ProbeSession s, string probe, string question, string pageId, int reads = 20)
    {
        var widths = new List<string>();
        var paths = new List<string>();
        var hashes = new List<string>();
        var hashesNoUnlockedWidth = new List<string>();
        var hashesNoPathCache = new List<string>();
        IReadOnlyList<(string Index, string Width, bool Locked)> columns = [];
        var insertedFiles = 0;
        for (var i = 0; i < reads; i++)
        {
            var xml = await s.PageXmlAsync(pageId);
            var root = XDocument.Parse(xml).Root!;
            columns = Columns(root);
            widths.Add(string.Join('|', columns.Select(c => c.Width)));
            var files = root.Descendants(One + "InsertedFile").Select(f => (string?)f.Attribute("pathCache") ?? string.Empty).ToList();
            insertedFiles = files.Count;
            paths.Add(Fingerprint(string.Join('\n', files)));
            hashes.Add(PageHash(xml));
            hashesNoUnlockedWidth.Add(PageHashWithout(xml, "Column", "width", c => (string?)c.Attribute("isLocked") != "true"));
            hashesNoPathCache.Add(PageHashWithout(xml, "InsertedFile", "pathCache"));
        }

        s.Recorder.Row(probe, question, "GetPageContent x" + reads.ToString(CultureInfo.InvariantCulture), new { pageInfo = "piBasic", schema = "xs2013" }, null, new
        {
            columns = columns.Select(c => new { c.Index, c.Locked }).ToList(),
            widthValues = widths.Distinct(StringComparer.Ordinal).ToList(),
            widthPattern = Pattern(widths),
            insertedFiles,
            pathCachePattern = Pattern(paths),
            pageHashPattern = Pattern(hashes),
            pageHashWithoutUnlockedWidthPattern = Pattern(hashesNoUnlockedWidth),
            pageHashWithoutPathCachePattern = Pattern(hashesNoPathCache),
        });
    }

    // --- HRESULTs -------------------------------------------------------------------------------------------------

    private static async Task HResultTriggersAsync(ProbeSession s, string sectionId, string stamp)
    {
        const string Q = "HRESULTs";
        var pageId = await s.CreatePageAsync(sectionId, $"probe hresults {stamp}");
        await s.MustUpdateAsync(pageId, Outline(null, Oe(null, "h-1")) + Outline(null, OeRaw(null, Table([(80, false), (80, false)], ["h-t1", "h-t2"]))));
        var page = await s.PageAsync(pageId);
        var h1 = OeByText(page, "h-1")!;
        const string Bogus = "{00000000-0000-0000-0000-000000000000}{1}{B0}";

        var cases = new (string Probe, string Shape, string Xml, XMLSchema Schema)[]
        {
            ("hr-xs2010-edit", "edit OE h-1 in the 2010 namespace, passed as xs2010", Page(pageId, Outline(OutlineOf(h1), Oe(ObjectId(h1), "h-1 via 2010")), ProbeSession.One2010.NamespaceName), XMLSchema.xs2010),
            ("hr-xs2010-as-2013", "2010-namespace XML passed as xs2013", Page(pageId, Outline(null, Oe(null, "h-2")), ProbeSession.One2010.NamespaceName), XMLSchema.xs2013),
            ("hr-xs2007-edit", "2013-namespace XML passed as xs2007", Page(pageId, Outline(OutlineOf(h1), Oe(ObjectId(h1), "h-1 via 2007"))), XMLSchema.xs2007),
            ("hr-unknown-element", "a one:Bogus element in the page", Page(pageId, "<one:Bogus />"), XMLSchema.xs2013),
            ("hr-malformed-xml", "unclosed one:Outline", Page(pageId, "<one:Outline>"), XMLSchema.xs2013),
            ("hr-unknown-objectid", "Outline with an objectID that isn't on the page", Page(pageId, Outline(Bogus, Oe(null, "h-3"))), XMLSchema.xs2013),
        };
        foreach (var c in cases)
        {
            var attempt = await s.UpdateAsync(pageId, c.Xml, DateTime.MinValue, c.Schema);
            var after = await s.PageAsync(pageId);
            s.Recorder.Row(c.Probe, Q, "UpdatePageContent", new { shape = c.Shape, schema = c.Schema.ToString() }, attempt, new
            {
                h1Text = Text(ById(after, ObjectId(h1)!)!),
                oeCount = Oes(after).Count(),
            });
        }

        var oesBefore = Oes(await s.PageAsync(pageId)).Count();
        var delete = await s.DeleteContentAsync(pageId, Bogus, DateTime.MinValue);
        var afterDelete = await s.PageAsync(pageId);
        s.Recorder.Row("hr-delete-unknown-objectid", Q, "DeletePageContent", new { target = "an objectID that isn't on the page" }, delete, new
        {
            oeCountBefore = oesBefore,
            oeCountAfter = Oes(afterDelete).Count(),
            h1Present = ById(afterDelete, ObjectId(h1)!) is not null,
        });

        await FutureContentLossAsync(s, sectionId, stamp);

        // xs2007 is left out: on 2026-11-10 GetPageContent(xs2007) on this page crashed OneNote (APPCRASH in
        // mso20win32client.dll, the call returned 0x800706BE). Recorded in the findings; not repeated.
        foreach (var schema in new[] { XMLSchema.xs2010 })
        {
            var read = await s.TryReadAsync("GetPageContent", app => app.GetPageContent(pageId, out _, PageInfo.piBasic, schema));
            s.Recorder.Row("hr-read-" + schema, Q, "GetPageContent", new { schema = schema.ToString(), page = "Outline + table" }, read, null);
        }
    }

    /// <summary>
    /// Tries for <c>0x80042022</c> hrFutureContentLoss: a table with a merged cell (written with xs2026), then its
    /// Outline written back as read in xs2013 with xs2013 and as read in xs2010 with xs2010, each checked in xs2026.
    /// </summary>
    private static async Task FutureContentLossAsync(ProbeSession s, string sectionId, string stamp)
    {
        const string Q = "HRESULTs";
        var ns26 = ProbeSession.One2026;
        var pageId = await s.CreatePageAsync(sectionId, $"probe content-loss {stamp}");
        var table = "<one:Table bordersVisible=\"true\"><one:Columns><one:Column index=\"0\" width=\"80.0\" /><one:Column index=\"1\" width=\"80.0\" /></one:Columns>"
            + $"<one:Row><one:Cell colSpan=\"2\"><one:OEChildren>{Oe(null, "cl-merged")}</one:OEChildren></one:Cell></one:Row>"
            + $"<one:Row><one:Cell><one:OEChildren>{Oe(null, "cl-1")}</one:OEChildren></one:Cell><one:Cell><one:OEChildren>{Oe(null, "cl-2")}</one:OEChildren></one:Cell></one:Row>"
            + "</one:Table>";
        var attempt = await s.UpdateAsync(pageId, Page(pageId, Outline(null, OeRaw(null, table)), ns26.NamespaceName), DateTime.MinValue, XMLSchema.xs2026, "UpdatePageContent(xs2026)");
        var spans = CloudWriteProbes.Spans(await s.PageAsync(pageId, XMLSchema.xs2026));
        s.Recorder.Row("hr-content-loss-xs2026-merged-table", Q, "UpdatePageContent", new { schema = "xs2026", shape = "a new 2x2 table, row 1 one Cell colSpan=2" }, attempt, new { spans2026 = spans });
        if (!attempt.Ok)
        {
            return;
        }

        foreach (var (schema, ns) in new[] { (XMLSchema.xs2013, (XNamespace)One), (XMLSchema.xs2010, ProbeSession.One2010) })
        {
            var read = await s.PageAsync(pageId, schema);
            var outline = read.Descendants(ns + "Table").FirstOrDefault()?.Ancestors(ns + "Outline").FirstOrDefault();
            if (outline is null)
            {
                s.Recorder.Row($"hr-content-loss-{schema}-roundtrip", Q, "UpdatePageContent", new { schema = schema.ToString() }, null, new { notProbed = $"no table in the {schema} read" });
                continue;
            }

            attempt = await s.UpdateAsync(pageId, PageOf(pageId, ns, outline), DateTime.MinValue, schema, $"UpdatePageContent({schema})");
            var after = CloudWriteProbes.Spans(await s.PageAsync(pageId, XMLSchema.xs2026));
            s.Recorder.Row($"hr-content-loss-{schema}-roundtrip", Q, "UpdatePageContent", new { schema = schema.ToString(), shape = $"the table's Outline as read in {schema}, unchanged" }, attempt, new
            {
                spans2026After = after,
                spanSurvived = after.Count > 0,
            });
        }
    }

    // --- Dates ----------------------------------------------------------------------------------------------------

    private static async Task DatesLocalAsync(ProbeSession s, string sectionId, string stamp, bool displayed)
    {
        var label = displayed ? "local-displayed" : "local-not-displayed";
        var title = $"probe dates {(displayed ? "shown" : "hidden")} {stamp}";
        var pageId = await s.CreatePageAsync(sectionId, title);
        await s.MustUpdateAsync(pageId, Outline(null, Oe(null, "dt-0")));
        var instruction = displayed
            ? $"In OneNote, show the page \"{title}\" (MCP Test > Probes) and leave it shown. Don't type in it. Then continue."
            : $"In OneNote, make sure the page \"{title}\" is NOT the page shown (show any other MCP Test page). Then continue.";
        var viewed = (string?)(await s.HierarchyPageAsync(sectionId, pageId)).Attribute("isCurrentlyViewed") == "true";
        if (viewed != displayed && !await s.PauseAsync($"dates-{label}", instruction))
        {
            return;
        }

        await DatesProbe.RunAsync(s, label, sectionId, pageId, deleteFirst: true, deleteExisting: false);
    }

    // --- Modal dialog ---------------------------------------------------------------------------------------------

    private static async Task ModalDialogAsync(ProbeSession s, string notebookId, string sectionId, string stamp)
    {
        const string Q = "Modal dialog";
        var pageId = await s.CreatePageAsync(sectionId, $"probe modal {stamp}");
        await s.MustUpdateAsync(pageId, Outline(null, Oe(null, "md-target")));
        // The Outline, not the OE: DeletePageContent deletes page-level objects only (an OE gives 0x8004200e).
        var targetId = OutlineOf(OeByText(await s.PageAsync(pageId), "md-target")!)!;

        if (!await s.PauseAsync("modal-dialog-open", "In OneNote, open File > Options and leave that dialog open. Don't click in OneNote until asked. Then continue."))
        {
            return;
        }

        var pagesBefore = (await s.TreeAsync(sectionId)).Elements(One + "Page").Count();
        string? newPage = null;
        try
        {
            var update = await s.UpdateAsync(pageId, Page(pageId, Outline(null, Oe(null, "md-update"))), DateTime.MinValue);
            s.Recorder.Row("modal-update-page-content", Q, "UpdatePageContent", new { shape = "a new Outline" }, update, null);

            var delete = await s.DeleteContentAsync(pageId, targetId, DateTime.MinValue);
            s.Recorder.Row("modal-delete-page-content", Q, "DeletePageContent", new { target = "an Outline" }, delete, null);

            var create = await s.TryWriteAsync(sectionId, "CreateNewPage", app => app.CreateNewPage(sectionId, out newPage, NewPageStyle.npsBlankPageWithTitle));
            s.Recorder.Row("modal-create-new-page", Q, "CreateNewPage", new { target = "Probes section" }, create, new { created = newPage is not null });

            var read = await s.TryReadAsync("GetPageContent", app => app.GetPageContent(pageId, out _, PageInfo.piBasic, XMLSchema.xs2013));
            s.Recorder.Row("modal-get-page-content", Q, "GetPageContent", new { pageInfo = "piBasic" }, read, null);

            var find = await s.TryReadAsync("FindPages", app => app.FindPages(notebookId, "probe", out _, fIncludeUnindexedPages: true, fDisplay: false, XMLSchema.xs2013));
            s.Recorder.Row("modal-find-pages", Q, "FindPages", new { start = "MCP Test", includeUnindexed = true }, find, null);

            var hierarchy = await s.TryReadAsync("GetHierarchy", app => app.GetHierarchy(notebookId, HierarchyScope.hsPages, out _, XMLSchema.xs2013));
            s.Recorder.Row("modal-get-hierarchy", Q, "GetHierarchy", new { scope = "hsPages" }, hierarchy, null);
        }
        finally
        {
            await s.PauseAsync("modal-dialog-close", "Close the File > Options dialog in OneNote (Cancel). Then continue.");
        }

        // Read-back once the dialog is closed: what the blocked (or not) writes left behind.
        var after = await s.PageAsync(pageId);
        var section = await s.TreeAsync(sectionId);
        s.Recorder.Row("modal-readback", Q, "GetPageContent", new { when = "after the dialog closed" }, null, new
        {
            mdUpdateAdded = OeByText(after, "md-update") is not null,
            mdTargetGone = ById(after, targetId) is null,
            sectionPagesBefore = pagesBefore,
            sectionPagesAfter = section.Elements(One + "Page").Count(),
            createdPagePresent = newPage is not null && ById(section, newPage) is not null,
        });
    }

    // --- Helpers --------------------------------------------------------------------------------------------------

    /// <summary>A whole page document in <paramref name="ns"/> holding copies of <paramref name="children"/>, prefix <c>one</c>.</summary>
    internal static string PageOf(string pageId, XNamespace ns, params XElement[] children)
    {
        var copies = children.Select(c => new XElement(c)).ToList();
        foreach (var declaration in copies.SelectMany(c => c.DescendantsAndSelf()).Attributes().Where(a => a.IsNamespaceDeclaration).ToList())
        {
            declaration.Remove(); // the root declares one:; a copy's own declaration would repeat it
        }

        return "<?xml version=\"1.0\"?>" + new XElement(
            ns + "Page",
            new XAttribute(XNamespace.Xmlns + "one", ns.NamespaceName),
            new XAttribute("ID", pageId),
            copies).ToString(SaveOptions.DisableFormatting);
    }

    private static void RecordIgnored(ProbeSession s, string probe, string method, object args, Attempt attempt, string beforeXml, string afterXml, IEnumerable<string> watched, object detail)
    {
        var before = PageBasis.From(beforeXml);
        var after = PageBasis.From(afterXml);
        s.Recorder.Row(probe, "Ignored edits", method, args, attempt, new
        {
            result = detail,
            pageHashChanged = before.PageHash != after.PageHash,
            elementHashChanged = watched.ToDictionary(
                id => id,
                id => before.ElementHashes.TryGetValue(id, out var b) && after.ElementHashes.TryGetValue(id, out var a) ? (b != a).ToString() : "missing"),
        });
    }

    private static List<string?> OutlineTexts(XElement page, string outlineId) =>
        page.Descendants(One + "Outline").Where(o => ObjectId(o) == outlineId)
            .SelectMany(o => o.Descendants(One + "OE")).Select(Text).ToList();

    private static Dictionary<string, string?> Lmts(XElement page, string outlineId)
    {
        var outline = page.Descendants(One + "Outline").First(o => ObjectId(o) == outlineId);
        var result = new Dictionary<string, string?>(StringComparer.Ordinal) { ["outline"] = Lmt(outline) };
        foreach (var oe in outline.Descendants(One + "OE"))
        {
            var key = Text(oe) is { } t && t.StartsWith("s-", StringComparison.Ordinal) ? t.Split(' ')[0] : ObjectId(oe) ?? "?";
            result[key] = Lmt(oe);
        }

        return result;
    }

    private static List<string> Changed(Dictionary<string, string?> before, Dictionary<string, string?> after) =>
        before.Where(p => after.TryGetValue(p.Key, out var a) && a != p.Value).Select(p => p.Key).ToList();

    private static LayoutBox Layout(XElement page, string outlineId)
    {
        var outline = page.Descendants(One + "Outline").First(o => ObjectId(o) == outlineId);
        var position = outline.Element(One + "Position");
        var size = outline.Element(One + "Size");
        return new LayoutBox(D(position, "x"), D(position, "y"), D(size, "width"), D(size, "height"));

        static double D(XElement? e, string name) =>
            double.TryParse((string?)e?.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 0.5;

    private static string? StyleName(XElement page, XElement oe)
    {
        var index = (string?)oe.Attribute("quickStyleIndex");
        return page.Elements(One + "QuickStyleDef").Where(d => (string?)d.Attribute("index") == index).Select(d => (string?)d.Attribute("name")).FirstOrDefault();
    }
    private sealed record LayoutBox(double X, double Y, double Width, double Height);
}
