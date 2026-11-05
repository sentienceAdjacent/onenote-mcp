using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>FindMeta</c>, always with unindexed pages included (AD-12). OneNote then returns every page under the start node
/// (story 9's finding), so the server removes, in order: recycle-bin pages (<c>recycleBin</c>), then pages with no child
/// <c>one:Meta</c> whose <c>name</c> is <c>name</c>, compared ordinally (<c>noMatch</c>). That check uses the result's own
/// metadata, with no page reads. Every other byte is OneNote's (AD-4). Throws only <see cref="OneNoteException"/>.
/// </summary>
public sealed class FindMetaUseCase(IOneNoteGateway gateway) : IUseCase
{
    private const string Method = "FindMeta";

    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string name, string startNodeId, bool fromRoot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var start = SearchResults.Start(startNodeId, fromRoot, Method);
        var metaName = name ?? string.Empty;
        var xml = await gateway.FindMetaAsync(start, metaName, includeUnindexed: true, ct).ConfigureAwait(false);

        var pages = SearchResults.Pages(xml, Method);
        var removals = new Dictionary<int, string>();
        foreach (var page in pages)
        {
            if (page.InRecycleBin)
            {
                removals[page.Index] = FilterReasons.RecycleBin;
            }
            else if (!page.MetaNames.Any(n => string.Equals(n, metaName, StringComparison.Ordinal)))
            {
                removals[page.Index] = FilterReasons.NoMatch;
            }
        }

        return SearchResults.Result(context, xml, pages, removals, Method);
    }
}
