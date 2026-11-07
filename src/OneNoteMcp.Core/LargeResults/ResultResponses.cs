using System.Globalization;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.LargeResults;

/// <summary>
/// Builds every response served from a stored result (AD-20): the index pages, runs and parts, sized against
/// <c>results.inlineMaxChars</c> with <see cref="ITextBlockMeasure"/>. Shared by <see cref="ResultDelivery"/> (the
/// first index) and <see cref="UseCases.ReadResultUseCase"/>, so the first index is exactly page 0 of
/// <c>read_result(resultId, expand: "/")</c>.
/// </summary>
internal sealed class ResultResponses(ITextBlockMeasure measure)
{
    /// <summary>
    /// Room left for what a template can't know exactly: digit counts of page numbers and the like. Templates already
    /// use the longest values (see <see cref="TemplateNumber"/>), so this is a margin, not an estimate.
    /// </summary>
    internal const int Slack = 64;

    private const string TemplateCorrelationId = "00000000000000000000000000000000";
    private const int TemplateNumber = int.MaxValue;

    public static string FormatExpiry(DateTimeOffset expiresAt) =>
        expiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The first index, returned instead of the body: the original meta (basis without <c>elementHashes</c>) and warnings, plus page 0 of <c>expand: "/"</c>.</summary>
    public ToolSuccess FirstIndex(StoredResult entry, DateTimeOffset expiresAt, int inlineMax)
    {
        var pages = Paginate(Group(entry, entry.Tree.Root.Children, inlineMax), RootOverhead(entry, expiresAt), inlineMax);
        return OriginalIndex(entry, entry.Original.Meta.CorrelationId, expiresAt, pages[0], pages.Count);
    }

    /// <summary><c>expand: node</c>, page <paramref name="page"/>. A page past the last is <see cref="ErrorCode.InvalidQuery"/>.</summary>
    public ToolSuccess Expand(StoredResult entry, string correlationId, DateTimeOffset expiresAt, SpanNode node, int page, int inlineMax)
    {
        var overhead = node == entry.Tree.Root
            ? RootOverhead(entry, expiresAt)
            : measure.TextBlock(ExpandResponse(entry, TemplateCorrelationId, expiresAt, node, TemplateNumber, [], TemplateNumber)) + Slack;
        var pages = Paginate(Group(entry, node.Children, inlineMax), overhead, inlineMax);
        CheckPage(page, pages.Count);
        return ExpandResponse(entry, correlationId, expiresAt, node, page, pages[page], pages.Count);
    }

    /// <summary>
    /// The index of a run's members, for a run whose part would exceed <paramref name="inlineMax"/>. The members are
    /// grouped with the budget cut by <paramref name="overflow"/>, and never back into the one run asked for.
    /// </summary>
    public ToolSuccess RunIndex(StoredResult entry, string correlationId, DateTimeOffset expiresAt, SpanNode first, SpanNode last, int page, int inlineMax, int overflow)
    {
        var parent = first.Parent!;
        var members = parent.Children.Skip(first.IndexInParent).Take(last.IndexInParent - first.IndexInParent + 1).ToList();
        var entries = Group(entry, members, inlineMax - overflow);
        if (entries.Count == 1 && entries[0].Run is not null)
        {
            entries = [.. members.Select(ElementEntry)];
        }

        var overhead = measure.TextBlock(RunIndexResponse(entry, TemplateCorrelationId, expiresAt, first, last, TemplateNumber, [], TemplateNumber)) + Slack;
        var pages = Paginate(entries, overhead, inlineMax);
        CheckPage(page, pages.Count);
        return RunIndexResponse(entry, correlationId, expiresAt, first, last, page, pages[page], pages.Count);
    }

    /// <summary>The exact substring [<paramref name="start"/>, <paramref name="end"/>) as a part (AD-20), never re-serialised (AD-4).</summary>
    public static ToolSuccess Part(StoredResult entry, string correlationId, DateTimeOffset expiresAt, int start, int end)
    {
        var body = entry.Body.Substring(start, end - start);
        var meta = new ToolMeta(correlationId, Basis: PartBasis(entry, start, end), Result: ResultMeta(entry, expiresAt, pages: null));
        return entry.Kind == ResultKind.Text
            ? new ToolSuccess(null, meta with { Binary = body }, [])
            : new ToolSuccess(body, meta, []);
    }

    /// <summary>
    /// The longest part from <paramref name="start"/>, up to <paramref name="end"/>, whose text block fits
    /// <paramref name="inlineMax"/> (AD-20's <c>bodyMax</c>). Never ends inside a surrogate pair.
    /// </summary>
    public ToolSuccess FittedPart(StoredResult entry, string correlationId, DateTimeOffset expiresAt, int start, int end, int inlineMax)
    {
        // The body can never be longer than the threshold minus a header with no hashes in it.
        var emptyHeader = measure.TextBlock(Part(entry, correlationId, expiresAt, start, start));
        var length = Math.Min(end - start, inlineMax - emptyHeader);
        while (true)
        {
            length = NoSplitPair(entry.Body, start, length);
            if (length <= 0)
            {
                throw new OneNoteException(ErrorCode.Internal, "A read_result part has no room for any body under results.inlineMaxChars.");
            }

            var part = Part(entry, correlationId, expiresAt, start, start + length);
            var size = measure.TextBlock(part);
            if (size <= inlineMax)
            {
                return part;
            }

            // Fewer characters can only drop element hashes from the header, so this converges. Where hashes outweigh
            // the text (many tiny OEs), cutting by the overflow alone would overshoot, so halve instead.
            var next = length - (size - inlineMax);
            length = next > 0 ? next : length / 2;
        }
    }

    public int Measure(ToolSuccess success) => measure.TextBlock(success);

    public static ResultMeta ResultMeta(StoredResult entry, DateTimeOffset expiresAt, int? pages) =>
        new(entry.ResultId, entry.Body.Length, FormatExpiry(expiresAt), entry.Tree.Namespaces, pages);

    /// <summary>
    /// AD-20's index entries for consecutive siblings: runs of siblings whose part fits the run budget, and an entry
    /// of its own for every sibling that doesn't fit with a neighbour. The run budget is
    /// <paramref name="inlineMax"/> minus a part header with the run's own element hashes.
    /// </summary>
    private List<IndexEntry> Group(StoredResult entry, IReadOnlyList<SpanNode> members, int inlineMax)
    {
        var header = measure.TextBlock(Part(entry, TemplateCorrelationId, DateTimeOffset.MaxValue, 0, 0)) + Slack;
        var budget = inlineMax - header;
        var entries = new List<IndexEntry>();
        var i = 0;
        while (i < members.Count)
        {
            var first = members[i];
            var hashes = entry.SubtreeHashChars[first.Order];
            var j = i;
            while (j + 1 < members.Count)
            {
                var next = members[j + 1];
                var withNext = hashes + entry.SubtreeHashChars[next.Order];
                if (next.End - first.Start + withNext > budget)
                {
                    break;
                }

                hashes = withNext;
                j++;
            }

            entries.Add(j == i
                ? ElementEntry(first)
                : new IndexEntry(
                    Chars: members[j].End - first.Start,
                    Run: [first.Ref, members[j].Ref],
                    Count: j - i + 1,
                    FirstLabel: first.Label,
                    LastLabel: members[j].Label));
            i = j + 1;
        }

        return entries;
    }

    private static IndexEntry ElementEntry(SpanNode node) => new(
        Chars: node.Length,
        Ref: node.Ref,
        Element: node.Element,
        Children: node.Children.Count,
        Contains: node.Tables + node.Images + node.InsertedFiles == 0
            ? null
            : new ContainsCounts(Zero(node.Tables), Zero(node.Images), Zero(node.InsertedFiles)),
        Label: node.Label);

    private static int? Zero(int count) => count == 0 ? null : count;

    /// <summary>Splits entries into pages whose whole envelope, entries measured as serialized JSON, fits. A page always holds at least one entry.</summary>
    private List<List<IndexEntry>> Paginate(List<IndexEntry> entries, int overhead, int inlineMax)
    {
        var pages = new List<List<IndexEntry>>();
        var current = new List<IndexEntry>();
        var used = overhead;
        foreach (var e in entries)
        {
            var size = measure.Json(e) + 1; // the comma
            if (current.Count > 0 && used + size > inlineMax)
            {
                pages.Add(current);
                current = [];
                used = overhead;
            }

            current.Add(e);
            used += size;
        }

        pages.Add(current);
        return pages;
    }

    /// <summary>Both responses that can carry page 0 of the root's index are measured, so the first index and <c>expand: "/"</c> page alike.</summary>
    private int RootOverhead(StoredResult entry, DateTimeOffset expiresAt) =>
        Math.Max(
            measure.TextBlock(OriginalIndex(entry, entry.Original.Meta.CorrelationId, expiresAt, [], TemplateNumber, TemplateNumber)),
            measure.TextBlock(ExpandResponse(entry, TemplateCorrelationId, expiresAt, entry.Tree.Root, TemplateNumber, [], TemplateNumber)))
        + Slack;

    private static ToolSuccess OriginalIndex(StoredResult entry, string correlationId, DateTimeOffset expiresAt, List<IndexEntry> entries, int pages, int page = 0)
    {
        var original = entry.Original.Meta;
        var meta = original with
        {
            Binary = null,
            Basis = original.Basis is { } b ? b with { ElementHashes = null } : null,
            Result = ResultMeta(entry, expiresAt, pages),
            CorrelationId = correlationId,
        };
        return new ToolSuccess(null, meta, entry.Original.Warnings) { Index = IndexOf(entry.Tree.Root, page, entries) };
    }

    private static ToolSuccess ExpandResponse(StoredResult entry, string correlationId, DateTimeOffset expiresAt, SpanNode node, int page, List<IndexEntry> entries, int pages)
    {
        var meta = new ToolMeta(correlationId, Basis: ExpandBasis(entry, node), Result: ResultMeta(entry, expiresAt, pages));
        return new ToolSuccess(null, meta, []) { Index = IndexOf(node, page, entries) };
    }

    private static ToolSuccess RunIndexResponse(StoredResult entry, string correlationId, DateTimeOffset expiresAt, SpanNode first, SpanNode last, int page, List<IndexEntry> entries, int pages)
    {
        var meta = new ToolMeta(
            correlationId,
            Basis: entry.Basis is { } b ? new PageBasisResult(b.PageHash, b.LastModifiedTime) : null,
            Result: ResultMeta(entry, expiresAt, pages));
        var index = new ResultIndex(first.Parent!.Ref, last.End - first.Start, page, entries, Run: [first.Ref, last.Ref]);
        return new ToolSuccess(null, meta, []) { Index = index };
    }

    private static ResultIndex IndexOf(SpanNode node, int page, List<IndexEntry> entries) =>
        new(node.Ref, node.Length, page, entries, Element: node.Element);

    /// <summary>An expand response carries the expanded element's own hash (AD-17, AD-20), when it is an Outline or OE.</summary>
    private static PageBasisResult? ExpandBasis(StoredResult entry, SpanNode node)
    {
        if (entry.Basis is not { } b)
        {
            return null;
        }

        IReadOnlyDictionary<string, string>? own = node.HashId is { } id && b.ElementHashes is { } all && all.TryGetValue(id, out var hash)
            ? new Dictionary<string, string>(StringComparer.Ordinal) { [id] = hash }
            : null;
        return new PageBasisResult(b.PageHash, b.LastModifiedTime, own);
    }

    /// <summary>A page part's basis: the hashes, from the piBasic read, of the Outlines and OEs wholly inside the part (AD-20).</summary>
    private static PageBasisResult? PartBasis(StoredResult entry, int start, int end)
    {
        if (entry.Basis is not { } b)
        {
            return null;
        }

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (b.ElementHashes is { } all && end > start)
        {
            foreach (var node in entry.Tree.Within(start, end))
            {
                if (node.HashId is { } id && all.TryGetValue(id, out var hash))
                {
                    hashes.TryAdd(id, hash);
                }
            }
        }

        return new PageBasisResult(b.PageHash, b.LastModifiedTime, hashes);
    }

    private static int NoSplitPair(string text, int start, int length)
    {
        var end = start + length;
        return length > 1 && end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]) ? length - 1 : length;
    }

    private static void CheckPage(int page, int pages)
    {
        if (page < 0 || page >= pages)
        {
            throw new OneNoteException(
                ErrorCode.InvalidQuery,
                $"read_result page {page.ToString(CultureInfo.InvariantCulture)} is out of range: this index has {pages.ToString(CultureInfo.InvariantCulture)} page(s), from 0.",
                userAction: "Pass a page from 0 to meta.result.pages minus 1.");
        }
    }
}
