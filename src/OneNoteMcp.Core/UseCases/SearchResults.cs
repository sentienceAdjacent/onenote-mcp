using OneNoteMcp.Core.Results;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// What <see cref="FindPagesUseCase"/> and <see cref="FindMetaUseCase"/> share (AD-12): the exactly-one start rule,
/// reading the result's pages, cutting the removed ones out byte for byte (AD-4) and the meta every search returns.
/// </summary>
internal static class SearchResults
{
    public const string StartRuleUserAction =
        "Pass exactly one of: a non-empty startNodeId (the notebook, section group or section to search under), or fromRoot: true to search every open notebook.";

    /// <summary>
    /// The start node to pass to the gateway: <paramref name="startNodeId"/> unchanged, or the empty string with
    /// <paramref name="fromRoot"/>. Neither or both is <see cref="ErrorCode.InvalidQuery"/>, before any COM call.
    /// </summary>
    public static string Start(string? startNodeId, bool fromRoot, string method)
    {
        var hasStart = !string.IsNullOrEmpty(startNodeId);
        if (hasStart == fromRoot)
        {
            throw new OneNoteException(
                ErrorCode.InvalidQuery,
                hasStart
                    ? $"{method} got both a startNodeId and fromRoot: true; give exactly one."
                    : $"{method} needs a start: a startNodeId or fromRoot: true.",
                userAction: StartRuleUserAction);
        }

        return fromRoot ? string.Empty : startNodeId!;
    }

    public static IReadOnlyList<SearchResultPage> Pages(string xml, string method) =>
        Readable(() => SearchResultFilter.Pages(xml), method);

    /// <summary>The XML with every page in <paramref name="removals"/> cut out, as the success result with its meta.</summary>
    public static ToolSuccess Result(
        ToolCallContext context,
        string xml,
        IReadOnlyList<SearchResultPage> pages,
        IReadOnlyDictionary<int, string> removals,
        string method)
    {
        var cut = Readable(() => SearchResultFilter.Remove(xml, p => removals.ContainsKey(p.Index)), method);
        var filtered = pages
            .Where(p => removals.ContainsKey(p.Index))
            .Select(p => new FilteredPage(p.Id, removals[p.Index]))
            .ToList();
        return ToolSuccess.WithXml(cut.Xml, new ToolMeta(context.CorrelationId, Filtered: filtered, PossiblyIncomplete: true));
    }

    private static T Readable<T>(Func<T> read, string method)
    {
        try
        {
            return read();
        }
        catch (OneNoteXmlException ex)
        {
            // OneNoteXmlException messages carry only positions and element names, never content (AD-13).
            throw new OneNoteException(ErrorCode.InvalidXml, $"{method} returned XML the server can't read: {ex.Message}", innerException: ex);
        }
    }
}
