using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>FindMeta</c>, always with unindexed pages included (AD-12). OneNote then returns every page under the start node
/// (story 9's finding), so the server removes, in order: recycle-bin pages (<c>recycleBin</c>), then pages with no child
/// <c>one:Meta</c> whose <c>name</c> is <c>name</c>, compared ordinally (<c>noMatch</c>). That check uses the result's own
/// metadata, with no page reads. Every other byte is OneNote's (AD-4). Throws only <see cref="OneNoteException"/>.
/// Before either, pages in password-protected sections without a read grant are dropped (AD-21): never matched, never
/// in <c>meta.filtered</c>; their sections go in <c>meta.withheld</c>.
/// </summary>
public sealed class FindMetaUseCase(IOneNoteGateway gateway, ReadAccess access) : IUseCase
{
    public FindMetaUseCase(IOneNoteGateway gateway)
        : this(gateway, ReadAccess.WithoutStoredGrants(gateway))
    {
    }

    private const string Method = "FindMeta";

    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string name, string startNodeId, bool fromRoot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var start = SearchResults.Start(startNodeId, fromRoot, Method);
        var metaName = name ?? string.Empty;
        // AD-21 first: the protected sections of the searched scope, before the search or any page read.
        var broad = await access.CheckSearchScopeAsync(start, ct).ConfigureAwait(false);

        var xml = await gateway.FindMetaAsync(start, metaName, includeUnindexed: true, ct).ConfigureAwait(false);

        var pages = SearchResults.Pages(xml, Method);
        var removals = new Dictionary<int, string>();
        foreach (var page in pages)
        {
            if (broad.IsWithheld(page.SectionId))
            {
                continue;
            }

            if (page.InRecycleBin)
            {
                removals[page.Index] = FilterReasons.RecycleBin;
            }
            else if (!page.MetaNames.Any(n => string.Equals(n, metaName, StringComparison.Ordinal)))
            {
                removals[page.Index] = FilterReasons.NoMatch;
            }
        }

        return SearchResults.Result(context, xml, pages, removals, broad, Method);
    }
}
