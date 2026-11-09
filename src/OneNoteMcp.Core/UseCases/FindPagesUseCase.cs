using OneNoteMcp.Core.Access;
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
/// <para>
/// A candidate's text comes from <see cref="PageTextCache"/> when the page was cached at the <c>lastModifiedTime</c> the
/// search XML gives it, and is read otherwise (AD-12); the result is the same either way. <c>search.cacheMaxMB</c> is
/// read once per call, and 0 turns the cache off and empties it.
/// </para>
/// <para>
/// Before any of that, pages in password-protected sections without a read grant are dropped (AD-21): never read, never
/// looked up in or added to the cache, never in <c>meta.filtered</c>; their sections go in <c>meta.withheld</c>.
/// </para>
/// </summary>
public sealed class FindPagesUseCase : IUseCase
{
    private const string Method = "FindPages";

    private readonly IOneNoteGateway gateway;
    private readonly PageTextCache cache;
    private readonly ReadAccess access;

    public FindPagesUseCase(IOneNoteGateway gateway, PageTextCache cache, ReadAccess access)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(access);
        this.gateway = gateway;
        this.cache = cache;
        this.access = access;
    }

    /// <summary>A use case with no stored read grants, for tests and tools built by hand.</summary>
    public FindPagesUseCase(IOneNoteGateway gateway, PageTextCache cache)
        : this(gateway, cache, ReadAccess.WithoutStoredGrants(gateway))
    {
    }

    /// <summary>A use case with its own cache on the default settings, for tests and tools built by hand.</summary>
    public FindPagesUseCase(IOneNoteGateway gateway)
        : this(gateway, PageTextCache.WithDefaults())
    {
    }

    public OperationClass Class => OperationClass.Read;

    public async Task<ToolSuccess> ExecuteAsync(ToolCallContext context, string query, string startNodeId, bool fromRoot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var start = SearchResults.Start(startNodeId, fromRoot, Method);
        var text = query ?? string.Empty;
        // AD-21 first: the protected sections of the searched scope, before the search or any page read.
        var broad = await access.CheckSearchScopeAsync(start, ct).ConfigureAwait(false);

        var xml = await gateway.FindPagesAsync(start, text, includeUnindexed: true, ct).ConfigureAwait(false);

        var pages = SearchResults.Pages(xml, Method);
        var terms = PageText.Terms(text);
        var removals = new Dictionary<int, string>();

        // search{} is read once per call (AD-10). A lowered cap, or 0, applies before any lookup.
        var maxBytes = cache.Settings.CacheMaxBytes;
        var cacheOn = maxBytes > 0;
        var evicted = cache.Trim(maxBytes);
        int hits = 0, misses = 0;
        foreach (var page in pages)
        {
            // Withheld before the recycle-bin step, any read and the cache (AD-12, AD-21).
            if (broad.IsWithheld(page.SectionId))
            {
                continue;
            }

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

            // Cached at the time the search gives the page: no read. A page without a usable time is never cached, and
            // neither is a page in a protected section, even under a grant (AD-12).
            var cacheable = cacheOn && !broad.IsProtected(page.SectionId);
            var modified = cacheable ? page.LastModifiedTime : null;
            string pageText;
            if (modified is { } cachedAt && cache.TryGet(page.Id, cachedAt, out var cached))
            {
                hits++;
                pageText = cached;
            }
            else
            {
                if (cacheable)
                {
                    misses++;
                }

                string pageXml;
                try
                {
                    pageXml = await gateway.GetPageContentAsync(page.Id, PageInfo.Basic, ct).ConfigureAwait(false);
                }
                catch (OneNoteException ex) when (ex.Code == ErrorCode.ObjectNotFound)
                {
                    cache.Remove(page.Id);
                    removals[page.Index] = FilterReasons.NotFound;
                    continue;
                }

                pageText = TextOf(pageXml, page.Id);
                if (modified is { } readAt)
                {
                    evicted += cache.Set(page.Id, readAt, pageText, maxBytes);
                }
            }

            if (!PageText.ContainsAll(terms, page.Name, pageText))
            {
                removals[page.Index] = FilterReasons.NoMatch;
            }
        }

        return SearchResults.Result(context, xml, pages, removals, broad, Method) with
        {
            SearchCache = new PageTextCacheUse(hits, misses, evicted, cache.Count, cache.TotalBytes),
        };
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
            throw new OneNoteException(ErrorCode.InvalidXml, $"GetPageContent returned XML for page {pageId} that the server can't read: {ex.Message}");
        }
    }
}
