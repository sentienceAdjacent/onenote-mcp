using OneNoteMcp.Core.Results;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// <c>FindPages</c>, always with unindexed pages included (AD-12). OneNote then returns every page under the start node
/// (story 9's finding), so the server removes, in order: recycle-bin pages (<c>recycleBin</c>, never read), then the
/// pages where some whitespace-separated term of the query is in neither the page's <c>name</c> nor its <c>one:T</c>
/// text, ignoring case (<c>noMatch</c>). A page whose <c>name</c> already holds every term is kept without a read; the
/// others are read with piBasic in document order. A page deleted since the
/// search (<see cref="ErrorCode.ObjectNotFound"/> on its read) is removed as <c>notFound</c>; any other read error fails
/// the call. Every other byte is OneNote's (AD-4). Throws only <see cref="OneNoteException"/>.
/// </summary>
public sealed class FindPagesUseCase(IOneNoteGateway gateway) : IUseCase
{
    private const string Method = "FindPages";

    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string query, string startNodeId, bool fromRoot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var start = SearchResults.Start(startNodeId, fromRoot, Method);
        var text = query ?? string.Empty;
        var xml = await gateway.FindPagesAsync(start, text, includeUnindexed: true, ct).ConfigureAwait(false);

        var pages = SearchResults.Pages(xml, Method);
        var terms = PageText.Terms(text);
        var removals = new Dictionary<int, string>();
        foreach (var page in pages)
        {
            if (page.InRecycleBin)
            {
                removals[page.Index] = FilterReasons.RecycleBin;
                continue;
            }

            // Every term already in the name: kept, with no read (Phill, 2026-11-05).
            if (PageText.ContainsAll(terms, page.Name, string.Empty))
            {
                continue;
            }

            string pageXml;
            try
            {
                pageXml = await gateway.GetPageContentAsync(page.Id, PageInfo.Basic, ct).ConfigureAwait(false);
            }
            catch (OneNoteException ex) when (ex.Code == ErrorCode.ObjectNotFound)
            {
                removals[page.Index] = FilterReasons.NotFound;
                continue;
            }

            if (!PageText.ContainsAll(terms, page.Name, TextOf(pageXml, page.Id)))
            {
                removals[page.Index] = FilterReasons.NoMatch;
            }
        }

        return SearchResults.Result(context, xml, pages, removals, Method);
    }

    private static string TextOf(string pageXml, string pageId)
    {
        try
        {
            return PageText.Of(pageXml);
        }
        catch (OneNoteXmlException ex)
        {
            // OneNoteXmlException messages carry only positions and element names, never content (AD-13).
            throw new OneNoteException(ErrorCode.InvalidXml, $"GetPageContent returned XML for page {pageId} that the server can't read: {ex.Message}", innerException: ex);
        }
    }
}
