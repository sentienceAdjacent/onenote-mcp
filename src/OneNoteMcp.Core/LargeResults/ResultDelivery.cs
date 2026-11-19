using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.LargeResults;

/// <summary>A result the store took, for the log (AD-13: the ID and sizes only).</summary>
public sealed record StoredEvent(string ResultId, int TotalChars, StoreChange Change);

/// <summary>What <see cref="ResultDelivery.Deliver"/> returns: the success to send, and the store event when it stored one.</summary>
public sealed record DeliveryOutcome(ToolSuccess Success, StoredEvent? Stored);

/// <summary>
/// AD-20's over-threshold decision, made once for every tool in the runner: a success whose text block (AD-9) is
/// over <c>results.inlineMaxChars</c> is stored, and the tool returns <c>meta.result</c> and the first index page
/// instead of the body. Every other success passes through unchanged.
/// </summary>
public sealed class ResultDelivery
{
    private readonly ResultStore store;
    private readonly ITextBlockMeasure measure;
    private readonly ResultResponses responses;

    public ResultDelivery(ResultStore store, ITextBlockMeasure measure)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(measure);
        this.store = store;
        this.measure = measure;
        responses = new ResultResponses(measure);
    }

    public ResultStore Store => store;

    /// <summary>
    /// Throws only <see cref="OneNoteException"/>. <paramref name="settings"/> is the call's one read of <c>results{}</c>;
    /// omitted, the settings are read now.
    /// </summary>
    public DeliveryOutcome Deliver(ToolSuccess success, ResultSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(success);

        // Only a read body is ever stored: never a read_result response or a scalar result.
        if (success.Source is not { } source || success.Body is not { } body)
        {
            return new DeliveryOutcome(success, null);
        }

        settings ??= store.Settings;
        var inlineMax = settings.InlineMaxChars;
        if (measure.TextBlock(success) <= inlineMax)
        {
            return new DeliveryOutcome(success, null);
        }

        var (tree, warning) = Spans(source.Kind, body);
        var original = warning is null ? success : success with { Warnings = [.. success.Warnings, warning] };
        var resultId = Guid.NewGuid().ToString("N");
        var hashChars = SubtreeHashChars(tree, success.Meta.Basis);
        var grants = success.Grants ?? ResultGrants.None;
        var entry = new StoredResult(resultId, source.Kind, tree, source.PiBasicXml, original, hashChars, grants);

        // Backlog 15 (Phill, 2026-11-19, Q1 a, Q3 a): a header too long for the first index loses meta.filtered to a second
        // stored result, JSON text read with read_result, and keeps its count and resultId. Every index of the result shows
        // the same header, so the stored original carries the moved form. Nothing is dropped (AD-20, no loss).
        if (original.Meta.Filtered is { Count: > 0 } filtered
            && measure.TextBlock(responses.FirstIndex(entry, DateTimeOffset.MaxValue, inlineMax)) > inlineMax)
        {
            var listId = StoreFilteredList(original.Meta.CorrelationId, filtered, grants, settings);
            original = original with { Meta = original.Meta with { Filtered = null, FilteredCount = filtered.Count, FilteredResultId = listId } };
            entry = new StoredResult(resultId, source.Kind, tree, source.PiBasicXml, original, hashChars, grants);
        }

        var (expiresAt, change) = store.Add(entry, settings);
        return new DeliveryOutcome(responses.FirstIndex(entry, expiresAt, inlineMax), new StoredEvent(entry.ResultId, body.Length, change));
    }

    /// <summary>The camelCase of the envelope, so the stored list reads as <c>meta.filtered</c> would have.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions ListJson = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    /// <summary>Stores <paramref name="filtered"/> as JSON text, under the parent's grants, and returns its resultId.</summary>
    private string StoreFilteredList(string correlationId, IReadOnlyList<FilteredPage> filtered, ResultGrants grants, ResultSettings settings)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(filtered, ListJson);
        var tree = ElementSpans.Text(json);
        var list = new StoredResult(
            Guid.NewGuid().ToString("N"),
            ResultKind.Text,
            tree,
            null,
            new ToolSuccess(null, new ToolMeta(correlationId, Binary: json), []),
            new int[tree.Nodes.Count],
            grants);
        store.Add(list, settings);
        return list.ResultId;
    }

    /// <summary>
    /// The body's span tree. XML that can't be indexed is never lost (AD-20): it is stored as one span, <c>/</c>, with an
    /// empty index, so offset reads still reach every character, and the result carries a <c>resultNotIndexed</c> warning.
    /// </summary>
    private static (SpanTree Tree, ToolWarning? Warning) Spans(ResultKind kind, string body)
    {
        try
        {
            return (kind switch
            {
                ResultKind.PageXml => ElementSpans.Page(body),
                ResultKind.HierarchyXml => ElementSpans.Hierarchy(body),
                _ => ElementSpans.Text(body),
            }, null);
        }
        catch (OneNoteXmlException ex)
        {
            // OneNoteXmlException messages carry only positions and element names, never content (AD-13).
            return (ElementSpans.Text(body), new ToolWarning(
                ToolWarnings.ResultNotIndexed,
                $"The result is too large to return whole, and its XML couldn't be indexed by element ({ex.Message}), so its index is empty.",
                "Read it with read_result, ref / and offset, from 0 up to meta.result.totalChars."));
        }
    }

    /// <summary>By node order: the serialized size of the <c>elementHashes</c> members for the node's subtree.</summary>
    private int[] SubtreeHashChars(SpanTree tree, PageBasisResult? basis)
    {
        var sizes = new int[tree.Nodes.Count];
        if (basis?.ElementHashes is not { Count: > 0 } hashes)
        {
            return sizes;
        }

        // Descendants come after their ancestors in document order, so walking backwards sums each subtree first.
        for (var i = tree.Nodes.Count - 1; i >= 0; i--)
        {
            var node = tree.Nodes[i];
            if (node.HashId is { } id && hashes.TryGetValue(id, out var hash))
            {
                sizes[node.Order] += measure.Json(id) + 1 + measure.Json(hash) + 1; // "id":"hash",
            }

            if (node.Parent is { } parent)
            {
                sizes[parent.Order] += sizes[node.Order];
            }
        }

        return sizes;
    }
}
