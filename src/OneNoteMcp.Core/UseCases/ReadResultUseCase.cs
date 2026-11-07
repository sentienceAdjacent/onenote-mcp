using OneNoteMcp.Core.LargeResults;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>read_result</c> (AD-20): a server-only <see cref="OperationClass.Read"/> tool that serves parts of a stored
/// oversized result and never calls COM. It takes exactly one of <c>ref</c>, <c>run</c> or <c>expand</c>;
/// <c>offset</c> and <c>length</c> go with <c>ref</c>, and <c>page</c> with <c>expand</c> or <c>run</c>. Every part
/// is an exact substring of the stored body (AD-4). Throws only <see cref="OneNoteException"/>.
/// </summary>
public sealed class ReadResultUseCase(ResultStore store, ITextBlockMeasure measure) : IUseCase
{
    public const string ModeUserAction =
        "Call read_result with exactly one of ref, run or expand. offset and length go only with ref; page goes only with expand or run.";

    public const string UnknownRefUserAction =
        "Use a ref from this result's index (read_result with expand), or / for the whole result.";

    private readonly ResultResponses responses = new(measure);

    public OperationClass Class => OperationClass.Read;

    public Task<ToolSuccess> ExecuteAsync(
        ToolCallContext context,
        string resultId,
        string? @ref,
        IReadOnlyList<string>? run,
        string? expand,
        int? page,
        int? offset,
        int? length,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var hasRef = !string.IsNullOrEmpty(@ref);
        var hasRun = run is { Count: > 0 };
        var hasExpand = !string.IsNullOrEmpty(expand);
        var modes = (hasRef ? 1 : 0) + (hasRun ? 1 : 0) + (hasExpand ? 1 : 0);
        if (modes != 1)
        {
            throw Invalid(modes == 0 ? "read_result needs one of ref, run or expand." : "read_result got more than one of ref, run and expand.");
        }

        if (offset is not null && !hasRef)
        {
            throw Invalid("read_result's offset goes only with ref.");
        }

        if (length is not null && offset is null)
        {
            throw Invalid("read_result's length goes only with ref and offset.");
        }

        if (page is not null && !hasExpand && !hasRun)
        {
            throw Invalid("read_result's page goes only with expand or run.");
        }

        if (hasRun && run!.Count != 2)
        {
            throw Invalid("read_result's run is [firstRef, lastRef]: exactly two refs.");
        }

        var settings = context.Results ?? store.Settings;
        var (entry, expiresAt, _) = store.Get(resultId ?? string.Empty, settings);
        var inlineMax = settings.InlineMaxChars;
        var id = context.CorrelationId;

        if (hasExpand)
        {
            return Task.FromResult(responses.Expand(entry, id, expiresAt, Find(entry, expand!), page ?? 0, inlineMax));
        }

        if (hasRun)
        {
            return Task.FromResult(Run(entry, id, expiresAt, run![0], run[1], page ?? 0, inlineMax));
        }

        var node = Find(entry, @ref!);
        if (offset is { } start)
        {
            return Task.FromResult(Offset(entry, id, expiresAt, node, start, length, inlineMax));
        }

        // A part is never cut: one that doesn't fit returns the element's index instead.
        var part = ResultResponses.Part(entry, id, expiresAt, node.Start, node.End);
        return Task.FromResult(responses.Measure(part) <= inlineMax ? part : responses.Expand(entry, id, expiresAt, node, 0, inlineMax));
    }

    private ToolSuccess Run(StoredResult entry, string id, DateTimeOffset expiresAt, string firstRef, string lastRef, int page, int inlineMax)
    {
        var first = Find(entry, firstRef);
        var last = Find(entry, lastRef);
        if (first.Parent is null || !ReferenceEquals(first.Parent, last.Parent) || first.IndexInParent > last.IndexInParent)
        {
            throw Invalid("read_result's run must name two siblings, the first not after the last.");
        }

        var part = ResultResponses.Part(entry, id, expiresAt, first.Start, last.End);
        var size = responses.Measure(part);
        if (size <= inlineMax && page == 0)
        {
            return part;
        }

        return responses.RunIndex(entry, id, expiresAt, first, last, page, inlineMax, Math.Max(0, size - inlineMax));
    }

    private ToolSuccess Offset(StoredResult entry, string id, DateTimeOffset expiresAt, SpanNode node, int offset, int? length, int inlineMax)
    {
        if (offset < 0 || offset >= node.Length)
        {
            throw new OneNoteException(
                ErrorCode.InvalidQuery,
                $"read_result's offset {offset} is outside ref {node.Ref}, whose span is {node.Length} characters.",
                userAction: "Pass an offset from 0 to the span's length minus 1 (the ref's chars in the index; meta.result.totalChars for /).");
        }

        if (length is <= 0)
        {
            throw Invalid("read_result's length must be greater than 0.");
        }

        var start = node.Start + offset;
        var end = length is { } l ? (int)Math.Min((long)start + l, node.End) : node.End;
        return responses.FittedPart(entry, id, expiresAt, start, end, inlineMax);
    }

    private static SpanNode Find(StoredResult entry, string reference) =>
        entry.Tree.Find(reference)
        ?? throw new OneNoteException(
            ErrorCode.UnknownObjectId,
            $"Ref {reference} is not in result {entry.ResultId}.",
            userAction: UnknownRefUserAction);

    private static OneNoteException Invalid(string message) =>
        new(ErrorCode.InvalidQuery, message, userAction: ModeUserAction);
}
