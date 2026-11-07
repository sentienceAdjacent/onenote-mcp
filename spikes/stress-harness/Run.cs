using OneNoteMcp.Com;
using OneNoteMcp.Core;

namespace StressHarness;

/// <summary>
/// The five phases. Every comparison reads raw (ComGateway) then tool (the server). On a difference it reads once
/// more, raw then tool: two raw reads that differ make it <see cref="Match.ChangedDuringRun"/>, not a mismatch.
/// </summary>
internal sealed class Run(ToolClient tools, ComGateway gateway, Recorder recorder, Options options, string notebookId)
{
    private const int BinaryObjectDoesNotExist = unchecked((int)0x8004200F);
    private const int MiscSample = 20;
    private const int MaxMetaNames = 10;

    private readonly Random _rng = new(options.Seed);
    private readonly Dictionary<string, PageFacts> _facts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _unreadable = new(StringComparer.Ordinal);
    private NotebookModel _model = new(notebookId, [], []);
    private Dictionary<string, PageNode> _pagesById = new(StringComparer.Ordinal);

    public string Phase { get; private set; } = "startup";

    public async Task ExecuteAsync()
    {
        Phase = "hierarchy";
        await HierarchyAsync();
        Phase = "pages";
        await PagesAsync();
        Phase = "binaries";
        await BinariesAsync();
        Phase = "search";
        await SearchAsync();
        Phase = "misc";
        await MiscAsync();
        Phase = "errors";
        await ErrorsAsync();
        Phase = "done";
    }

    // ---- hierarchy ----

    private async Task HierarchyAsync()
    {
        (string Arg, HierarchyScope Scope)[] scopes =
            [("self", HierarchyScope.Self), ("children", HierarchyScope.Children), ("sections", HierarchyScope.Sections), ("pages", HierarchyScope.Pages)];
        RawOutcome? pagesRaw = null;
        var done = 0;
        foreach (var (arg, scope) in scopes)
        {
            var (_, raw, _) = await HierarchyCallAsync(notebookId, arg, scope, "notebook");
            if (scope == HierarchyScope.Pages)
            {
                pagesRaw = raw;
            }

            Progress.Report(Phase, ++done, scopes.Length);
        }

        await HierarchyCallAsync(null, "notebooks", HierarchyScope.Notebooks, "root");
        if (options.RootPages)
        {
            await HierarchyCallAsync(null, "pages", HierarchyScope.Pages, "root");
        }

        if (pagesRaw is not { Ok: true, Value: { } pagesXml })
        {
            throw new RefusalException(Exit.Precondition, $"the notebook's pages-scope hierarchy could not be read raw ({pagesRaw?.Code})");
        }

        _model = XmlFacts.Model(notebookId, pagesXml);
        _pagesById = _model.Pages.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var n = recorder.NotebookCounts;
        n["sections"] = _model.Sections.Count;
        n["sectionsLocked"] = _model.Sections.Count(s => s.Locked);
        n["sectionsEncrypted"] = _model.Sections.Count(s => s.Encrypted);
        n["sectionsInRecycleBin"] = _model.Sections.Count(s => s.InRecycleBin);
        n["pages"] = _model.Pages.Count;
        n["pagesInRecycleBin"] = _model.Pages.Count(p => p.InRecycleBin);
        n["hierarchyPagesChars"] = pagesXml.Length;
    }

    private async Task<(CallRecord, RawOutcome, ToolOutcome)> HierarchyCallAsync(string? startId, string arg, HierarchyScope scope, string at)
    {
        var args = new Dictionary<string, object?> { ["scope"] = arg };
        if (startId is not null)
        {
            args["startNodeId"] = startId;
        }

        var result = await CompareAsync(
            "hierarchy",
            "get_hierarchy",
            $"get_hierarchy:{at}:{arg}",
            startId,
            () => Raw.RunAsync(ct => gateway.GetHierarchyAsync(startId ?? string.Empty, scope, ct)),
            () => tools.CallAsync("get_hierarchy", args),
            (r, t) => Same(r, t, t.Xml));
        var (rec, raw, tool) = result;
        if (raw is { Ok: true, Value: { } rawXml } && tool is { Ok: true, Xml: { } toolXml })
        {
            Guard(rec, () =>
            {
                var rawSections = XmlFacts.Ids(rawXml, "Section");
                var rawPages = XmlFacts.Ids(rawXml, "Page");
                rec.Counts["sections"] = rawSections.Count;
                rec.Counts["pages"] = rawPages.Count;
                rec.Counts["sectionIdsEqual"] = rawSections.SetEquals(XmlFacts.Ids(toolXml, "Section")) ? 1 : 0;
                rec.Counts["pageIdsEqual"] = rawPages.SetEquals(XmlFacts.Ids(toolXml, "Page")) ? 1 : 0;
            });
        }

        recorder.Add(rec);
        return result;
    }

    // ---- pages ----

    private async Task PagesAsync()
    {
        var pages = _model.Pages.Where(p => !p.InRecycleBin).ToList();
        var done = 0;
        foreach (var page in pages)
        {
            var id = page.Id;
            var (rec, raw, _) = await CompareAsync(
                "pages",
                "get_page_content",
                "get_page_content:basic",
                id,
                () => Raw.RunAsync(ct => gateway.GetPageContentAsync(id, PageInfo.Basic, ct)),
                () => tools.CallAsync("get_page_content", new() { ["pageId"] = id, ["includeBinary"] = false }),
                (r, t) => Same(r, t, t.Xml));

            PageFacts? facts = null;
            if (raw is { Ok: true, Value: { } basic })
            {
                Guard(rec, () => facts = XmlFacts.Facts(basic));
            }

            if (facts is null)
            {
                _unreadable[id] = raw.Code ?? "xmlParse";
            }
            else
            {
                _facts[id] = facts;
                rec.Counts["textChars"] = facts.Text.Length;
                rec.Counts["images"] = facts.Images;
                rec.Counts["insertedFiles"] = facts.InsertedFiles;
                rec.Counts["callbackIds"] = facts.CallbackIds.Count;
                rec.Counts["metas"] = facts.MetaNames.Count;
            }

            recorder.Add(rec);

            if (facts is { } f && f.Images + f.InsertedFiles > 0)
            {
                var (brec, braw, btool) = await CompareAsync(
                    "pages",
                    "get_page_content",
                    "get_page_content:binary",
                    id,
                    () => Raw.RunAsync(ct => gateway.GetPageContentAsync(id, PageInfo.BinaryData, ct)),
                    () => tools.CallAsync("get_page_content", new() { ["pageId"] = id, ["includeBinary"] = true }),
                    (r, t) => Same(r, t, t.Xml));
                if (braw is { Ok: true, Value: { } binXml })
                {
                    Guard(brec, () =>
                    {
                        var (images, files, dataChars) = XmlFacts.BinaryCounts(binXml);
                        brec.Counts["images"] = images;
                        brec.Counts["insertedFiles"] = files;
                        brec.Counts["dataChars"] = dataChars;
                    });
                }

                if (btool.MetaInt("binaryUnavailable") is { } missing)
                {
                    brec.Counts["binaryUnavailable"] = missing;
                }

                recorder.Add(brec);
            }

            Progress.Report(Phase, ++done, pages.Count);
        }

        var n = recorder.NotebookCounts;
        n["pagesRead"] = _facts.Count;
        n["pagesUnreadable"] = _unreadable.Count;
        n["pagesWithImages"] = _facts.Values.Count(f => f.Images > 0);
        n["pagesWithInsertedFiles"] = _facts.Values.Count(f => f.InsertedFiles > 0);
        n["images"] = _facts.Values.Sum(f => f.Images);
        n["insertedFiles"] = _facts.Values.Sum(f => f.InsertedFiles);
        n["callbackIds"] = _facts.Values.Sum(f => f.CallbackIds.Count);
        n["pagesWithMeta"] = _facts.Values.Count(f => f.MetaNames.Count > 0);
    }

    // ---- binaries ----

    private async Task BinariesAsync()
    {
        var all = _facts.SelectMany(kv => kv.Value.CallbackIds.Select(cb => (Page: kv.Key, Callback: cb))).OrderBy(x => x.Page, StringComparer.Ordinal).ToList();
        Shuffle(all);
        var chosen = all.Take(options.MaxBinaries).ToList();
        var done = 0;
        int returned = 0, unavailable = 0, notFound = 0;
        foreach (var (page, callback) in chosen)
        {
            var (rec, raw, tool) = await CompareAsync(
                "binaries",
                "get_binary_page_content",
                "get_binary_page_content",
                page,
                () => Raw.RunAsync(ct => gateway.GetBinaryPageContentAsync(page, callback, ct)),
                () => tools.CallAsync("get_binary_page_content", new() { ["pageId"] = page, ["callbackId"] = callback }),
                (r, t) => r.HResult == BinaryObjectDoesNotExist
                    ? t.Ok && t.MetaString("binary") is null && t.Warnings.Contains("binaryUnavailable")
                    : Same(r, t, t.MetaString("binary")));
            rec.Counts["binaryUnavailable"] = raw.HResult == BinaryObjectDoesNotExist ? 1 : 0;
            if (tool.MetaString("binary") is { } b)
            {
                rec.Counts["base64Chars"] = b.Length;
            }

            // Counted from the oracle's outcome, so a tool that disagrees shows up as a mismatch, not here.
            if (raw is { Ok: true, Value.Length: > 0 })
            {
                returned++;
            }
            else if (raw.HResult == BinaryObjectDoesNotExist)
            {
                unavailable++;
            }
            else if (raw.Code == nameof(ErrorCode.ObjectNotFound))
            {
                notFound++;
            }

            recorder.Add(rec);
            Progress.Report(Phase, ++done, chosen.Count);
        }

        recorder.NotebookCounts["callbackIdsFound"] = all.Count;
        recorder.NotebookCounts["binariesCompared"] = chosen.Count;
        recorder.NotebookCounts["binariesReturned"] = returned;
        recorder.NotebookCounts["binariesUnavailable"] = unavailable;
        recorder.NotebookCounts["binariesNotFound"] = notFound;
    }

    // ---- search ----

    private async Task SearchAsync()
    {
        // Queries are drawn from page text and kept in memory only; records carry the query's index and term count.
        var withWords = _facts
            .Where(kv => !_pagesById[kv.Key].InRecycleBin)
            .Select(kv => (Id: kv.Key, Words: XmlFacts.Words(kv.Value.Text)))
            .Where(x => x.Words.Count > 0)
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .ToList();
        var total = withWords.Count == 0 ? 0 : options.Queries;
        for (var q = 0; q < total; q++)
        {
            var (_, words) = withWords[_rng.Next(withWords.Count)];
            var n = words.Count >= 2 ? _rng.Next(1, 3) : 1;
            var start = _rng.Next(words.Count - n + 1);
            var terms = words.Skip(start).Take(n).ToList();
            await FindPagesAsync(q, terms);
            Progress.Report(Phase + ":find_pages", q + 1, total);
        }

        var metaNames = _facts
            .Where(kv => !_pagesById[kv.Key].InRecycleBin)
            .SelectMany(kv => kv.Value.MetaNames)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        Shuffle(metaNames);
        var chosen = metaNames.Take(MaxMetaNames).ToList();
        for (var i = 0; i < chosen.Count; i++)
        {
            await FindMetaAsync(i, chosen[i]);
            Progress.Report(Phase + ":find_meta", i + 1, chosen.Count);
        }

        recorder.NotebookCounts["distinctMetaNames"] = metaNames.Count;
    }

    private async Task FindPagesAsync(int index, List<string> terms)
    {
        var query = string.Join(' ', terms);
        var unknownIds = new HashSet<string>(StringComparer.Ordinal);
        var unknown = 0;
        var needsUnreadable = false;
        HashSet<string> predicted = [];
        List<string> candidates = [];

        bool Evaluate(RawOutcome r, ToolOutcome t)
        {
            unknown = 0;
            unknownIds.Clear();
            needsUnreadable = false;
            predicted = [];
            if (!r.Ok || r.Value is null)
            {
                return !t.Ok && t.Code == r.Code;
            }

            candidates = XmlFacts.SearchPageIds(r.Value);
            foreach (var id in candidates)
            {
                if (!_pagesById.TryGetValue(id, out var node))
                {
                    unknown++;
                    unknownIds.Add(id);
                    continue;
                }

                if (node.InRecycleBin)
                {
                    continue;
                }

                if (XmlFacts.ContainsAll(terms, node.Name, string.Empty))
                {
                    predicted.Add(id);
                }
                else if (_facts.TryGetValue(id, out var f))
                {
                    if (XmlFacts.ContainsAll(terms, node.Name, f.Text))
                    {
                        predicted.Add(id);
                    }
                }
                else
                {
                    needsUnreadable = true;
                }
            }

            if (needsUnreadable)
            {
                // The server must read a page the harness couldn't, so the call is expected to fail.
                return !t.Ok;
            }

            return t.Ok && t.Xml is { } xml && predicted.SetEquals(XmlFacts.SearchPageIds(xml));
        }

        var (rec, raw, tool) = await CompareAsync(
            "search",
            "find_pages",
            "find_pages",
            notebookId,
            () => Raw.RunAsync(ct => gateway.FindPagesAsync(notebookId, query, includeUnindexed: true, ct)),
            () => tools.CallAsync("find_pages", new() { ["query"] = query, ["startNodeId"] = notebookId }),
            Evaluate);
        Evaluate(raw, tool);
        await ReclassifyAsync(rec, tool, predicted, unknownIds);

        var everywhere = _model.Pages.Count(p => !p.InRecycleBin
            && (XmlFacts.ContainsAll(terms, p.Name, string.Empty) || (_facts.TryGetValue(p.Id, out var f) && XmlFacts.ContainsAll(terms, p.Name, f.Text))));
        var candidateSet = candidates.ToHashSet(StringComparer.Ordinal);
        rec.Counts["queryIndex"] = index;
        rec.Counts["terms"] = terms.Count;
        rec.Counts["candidates"] = candidates.Count;
        rec.Counts["predicted"] = predicted.Count;
        rec.Counts["matchingInHierarchy"] = everywhere;
        rec.Counts["missingFromFindPages"] = _model.Pages.Count(p => !p.InRecycleBin && !candidateSet.Contains(p.Id)
            && (XmlFacts.ContainsAll(terms, p.Name, string.Empty) || (_facts.TryGetValue(p.Id, out var f) && XmlFacts.ContainsAll(terms, p.Name, f.Text))));
        rec.Counts["unknownCandidates"] = unknown;
        rec.Counts["needsUnreadablePage"] = needsUnreadable ? 1 : 0;
        if (tool is { Ok: true, Xml: { } x })
        {
            rec.Counts["toolPages"] = XmlFacts.SearchPageIds(x).Count;
            rec.Counts["filteredNoMatch"] = tool.FilteredCount("noMatch");
            rec.Counts["filteredRecycleBin"] = tool.FilteredCount("recycleBin");
            rec.Counts["filteredNotFound"] = tool.FilteredCount("notFound");
        }

        recorder.Add(rec);
    }

    private async Task FindMetaAsync(int index, string name)
    {
        HashSet<string> predicted = [];
        List<string> candidates = [];
        var unknownIds = new HashSet<string>(StringComparer.Ordinal);
        var unknown = 0;
        var unreadable = 0;

        bool Evaluate(RawOutcome r, ToolOutcome t)
        {
            predicted = [];
            unknown = 0;
            unknownIds.Clear();
            unreadable = 0;
            if (!r.Ok || r.Value is null)
            {
                return !t.Ok && t.Code == r.Code;
            }

            candidates = XmlFacts.SearchPageIds(r.Value);
            var excluded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in candidates)
            {
                if (!_pagesById.TryGetValue(id, out var node))
                {
                    unknown++;
                    unknownIds.Add(id);
                }
                else if (node.InRecycleBin)
                {
                }
                else if (!_facts.TryGetValue(id, out var f))
                {
                    // Predicted from the page's own one:Meta, which the harness couldn't read: left out of both sets.
                    unreadable++;
                    excluded.Add(id);
                }
                else if (f.MetaNames.Contains(name, StringComparer.Ordinal))
                {
                    predicted.Add(id);
                }
            }

            if (!t.Ok || t.Xml is null)
            {
                return false;
            }

            var got = XmlFacts.SearchPageIds(t.Xml).Where(id => !excluded.Contains(id)).ToHashSet(StringComparer.Ordinal);
            return predicted.SetEquals(got);
        }

        var (rec, raw, tool) = await CompareAsync(
            "search",
            "find_meta",
            "find_meta",
            notebookId,
            () => Raw.RunAsync(ct => gateway.FindMetaAsync(notebookId, name, includeUnindexed: true, ct)),
            () => tools.CallAsync("find_meta", new() { ["name"] = name, ["startNodeId"] = notebookId }),
            Evaluate);
        Evaluate(raw, tool);
        await ReclassifyAsync(rec, tool, predicted, unknownIds);

        var candidateSet = candidates.ToHashSet(StringComparer.Ordinal);
        rec.Counts["nameIndex"] = index;
        rec.Counts["candidates"] = candidates.Count;
        rec.Counts["predicted"] = predicted.Count;
        rec.Counts["unknownCandidates"] = unknown;
        rec.Counts["unreadableCandidates"] = unreadable;
        rec.Counts["missingFromFindMeta"] = _model.Pages.Count(p => !p.InRecycleBin && !candidateSet.Contains(p.Id)
            && _facts.TryGetValue(p.Id, out var f) && f.MetaNames.Contains(name, StringComparer.Ordinal));
        if (tool is { Ok: true, Xml: { } x })
        {
            rec.Counts["toolPages"] = XmlFacts.SearchPageIds(x).Count;
            rec.Counts["filteredNoMatch"] = tool.FilteredCount("noMatch");
            rec.Counts["filteredRecycleBin"] = tool.FilteredCount("recycleBin");
        }

        recorder.Add(rec);
    }

    /// <summary>
    /// A search mismatch becomes <see cref="Match.ChangedDuringRun"/> only when every disagreeing page is one the
    /// pages phase never saw (it appeared since), or a re-read proves a disagreeing page changed. A match is never touched.
    /// </summary>
    private async Task ReclassifyAsync(CallRecord rec, ToolOutcome tool, HashSet<string> predicted, HashSet<string> unknownIds)
    {
        if (rec.Match != Match.Mismatch || tool is not { Ok: true, Xml: { } toolXml })
        {
            return;
        }

        var diff = new HashSet<string>(predicted, StringComparer.Ordinal);
        diff.SymmetricExceptWith(XmlFacts.SearchPageIds(toolXml));
        if ((diff.Count > 0 && diff.IsSubsetOf(unknownIds)) || await AnyChangedAsync(diff))
        {
            rec.Match = Match.ChangedDuringRun;
        }
    }

    private async Task<bool> AnyChangedAsync(IEnumerable<string> pageIds)
    {
        foreach (var id in pageIds)
        {
            // No prior hash (a recycle-bin or unseen page): no evidence of change, so the mismatch stands.
            if (!_facts.TryGetValue(id, out var f))
            {
                continue;
            }

            var again = await Raw.RunAsync(ct => gateway.GetPageContentAsync(id, PageInfo.Basic, ct));
            var before = f.BasicHash;
            var after = again is { Ok: true, Value: { } v } ? XmlFacts.Hash(v) : null;
            if (before != after)
            {
                return true;
            }
        }

        return false;
    }

    // ---- misc ----

    private async Task MiscAsync()
    {
        var pool = new List<string> { notebookId };
        pool.AddRange(_model.Sections.Where(s => !s.InRecycleBin).Select(s => s.Id));
        pool.AddRange(_model.Pages.Where(p => !p.InRecycleBin).Select(p => p.Id));
        Shuffle(pool);
        var sample = pool.Take(MiscSample).ToList();
        var done = 0;
        foreach (var id in sample)
        {
            var (prec, _, _) = await CompareAsync(
                "misc",
                "get_hierarchy_parent",
                "get_hierarchy_parent",
                id,
                () => Raw.RunAsync(ct => gateway.GetHierarchyParentAsync(id, ct)),
                () => tools.CallAsync("get_hierarchy_parent", new() { ["objectId"] = id }),
                (r, t) => Same(r, t, t.MetaString("parentId")));
            recorder.Add(prec);

            var (lrec, lraw, _) = await CompareAsync(
                "misc",
                "get_hyperlink_to_object",
                "get_hyperlink_to_object",
                id,
                () => Raw.RunAsync(ct => gateway.GetHyperlinkToObjectAsync(id, string.Empty, ct)),
                () => tools.CallAsync("get_hyperlink_to_object", new() { ["hierarchyId"] = id }),
                (r, t) => Same(r, t, t.MetaString("link")));
            if (lraw.Value is { } link)
            {
                lrec.Counts["linkChars"] = link.Length;
            }

            recorder.Add(lrec);

            var (wrec, wraw, _) = await CompareAsync(
                "misc",
                "get_web_hyperlink_to_object",
                "get_web_hyperlink_to_object",
                id,
                () => Raw.RunAsync(ct => gateway.GetWebHyperlinkToObjectAsync(id, string.Empty, ct)),
                () => tools.CallAsync("get_web_hyperlink_to_object", new() { ["hierarchyId"] = id }),
                (r, t) => Same(r, t, t.MetaString("link")) && (r.Value is not "" || t.Warnings.Contains("webLinkUnavailable")));
            if (wraw.Value is { } web)
            {
                wrec.Counts["linkChars"] = web.Length;
            }

            recorder.Add(wrec);
            Progress.Report(Phase, ++done, sample.Count);
        }

        (string Arg, SpecialLocation Location)[] locations =
            [("backUpFolder", SpecialLocation.BackUpFolder), ("unfiledNotesSection", SpecialLocation.UnfiledNotesSection), ("defaultNotebookFolder", SpecialLocation.DefaultNotebookFolder)];
        foreach (var (arg, location) in locations)
        {
            var (rec, raw, _) = await CompareAsync(
                "misc",
                "get_special_location",
                "get_special_location:" + arg,
                null,
                () => Raw.RunAsync(ct => gateway.GetSpecialLocationAsync(location, ct)),
                () => tools.CallAsync("get_special_location", new() { ["location"] = arg }),
                (r, t) => Same(r, t, t.MetaString("path")));
            if (raw.Value is { } path)
            {
                rec.Counts["pathChars"] = path.Length;
            }

            recorder.Add(rec);
        }
    }

    // ---- errors ----

    /// <summary>
    /// The "tool error" row, exercised read-only: a well-formed ID that doesn't exist (the notebook ID with the last
    /// digit of its GUID changed). Raw and tool must fail with the same code; ObjectNotFound is expected.
    /// </summary>
    private async Task ErrorsAsync()
    {
        var at = notebookId.IndexOf('}') - 1;
        var bogus = at > 0
            ? notebookId[..at] + (notebookId[at] == '0' ? '1' : '0') + notebookId[(at + 1)..]
            : notebookId + "0";

        var (prec, praw, _) = await CompareAsync(
            "errors",
            "get_page_content",
            "get_page_content:nonexistent",
            bogus,
            () => Raw.RunAsync(ct => gateway.GetPageContentAsync(bogus, PageInfo.Basic, ct)),
            () => tools.CallAsync("get_page_content", new() { ["pageId"] = bogus, ["includeBinary"] = false }),
            (r, t) => !r.Ok && Same(r, t, null));
        prec.Counts["rawObjectNotFound"] = praw.Code == nameof(ErrorCode.ObjectNotFound) ? 1 : 0;
        recorder.Add(prec);

        var (hrec, hraw, _) = await CompareAsync(
            "errors",
            "get_hierarchy_parent",
            "get_hierarchy_parent:nonexistent",
            bogus,
            () => Raw.RunAsync(ct => gateway.GetHierarchyParentAsync(bogus, ct)),
            () => tools.CallAsync("get_hierarchy_parent", new() { ["objectId"] = bogus }),
            (r, t) => !r.Ok && Same(r, t, null));
        hrec.Counts["rawObjectNotFound"] = hraw.Code == nameof(ErrorCode.ObjectNotFound) ? 1 : 0;
        recorder.Add(hrec);
        Progress.Report(Phase, 2, 2);
    }

    // ---- shared ----

    private static bool Same(RawOutcome r, ToolOutcome t, string? toolValue) =>
        r.Ok ? t.Ok && string.Equals(toolValue, r.Value, StringComparison.Ordinal) : !t.Ok && t.Code == r.Code;

    internal static async Task<(CallRecord Record, RawOutcome Raw, ToolOutcome Tool)> CompareAsync(
        string phase,
        string tool,
        string kind,
        string? targetId,
        Func<Task<RawOutcome>> raw,
        Func<Task<ToolOutcome>> call,
        Func<RawOutcome, ToolOutcome, bool> equal)
    {
        var raw1 = await raw();
        var tool1 = await call();
        var rec = new CallRecord { Phase = phase, Tool = tool, Kind = kind, TargetId = targetId, RawResult = raw1, ToolResult = tool1 };
        if (equal(raw1, tool1))
        {
            rec.Match = Match.Same;
            return (rec, raw1, tool1);
        }

        rec.Retried = true;

        // Two raw reads back to back, with no tool call between them: OneNote can alternate a page's XML on every
        // read (one:Column/@width, spike 1.15), which a raw/tool/raw/tool order would line up with and hide.
        var raw2 = await raw();
        var raw3 = await raw();
        if (raw2.Key != raw3.Key)
        {
            rec.Match = Match.UnstableRead;
            rec.RawResult = raw3;
            rec.Counts["rawReads"] = 3;
            if (raw2 is { Ok: true, Value: { } v2 } && raw3 is { Ok: true, Value: { } v3 })
            {
                var at = FirstDifference(v2, v3);
                var (path, attr) = Diag.PathAt(v2, at);
                rec.Counts["unstableOffset"] = at;
                rec.Labels["unstablePath"] = path;
                if (attr is not null)
                {
                    rec.Labels["unstableAttr"] = attr;
                }
            }

            return (rec, raw3, tool1);
        }

        var tool2 = await call();
        rec.Match = raw1.Key != raw2.Key
            ? Match.ChangedDuringRun
            : equal(raw3, tool2) ? Match.Recovered : Match.Mismatch;
        // The record carries the second attempt, the one the match state describes; the first's size and time stay as counts.
        rec.RawResult = raw3;
        rec.ToolResult = tool2;
        rec.Counts["firstBytes"] = tool1.Bytes;
        rec.Counts["firstMs"] = tool1.Ms;
        return (rec, raw3, tool2);
    }

    internal static int FirstDifference(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
        {
            i++;
        }

        return i;
    }

    /// <summary>Runs an XML fact-gathering step; a parse failure is counted by exception type, never its message.</summary>
    private static void Guard(CallRecord rec, Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            rec.Counts["xmlParseErrors"] = rec.Counts.GetValueOrDefault("xmlParseErrors") + 1;
        }
    }

    private void Shuffle<T>(List<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
