namespace OneNoteMcp.Core.Results;

/// <summary>
/// One page a search removed from OneNote's result, in <c>meta.filtered</c> (AD-4, AD-12). <see cref="Reason"/> is one
/// of <see cref="FilterReasons"/>. It carries the page's ID only, never its name or content (AD-9).
/// </summary>
public sealed record FilteredPage(string PageId, string Reason);

/// <summary>Why a search removed a page, in AD-12's order: recycle bin first, then the metadata or text check.</summary>
public static class FilterReasons
{
    /// <summary>The page, or a section or section group around it, is in the recycle bin.</summary>
    public const string RecycleBin = "recycleBin";

    /// <summary>The page doesn't match: <c>find_meta</c> found no Meta of that name, or <c>find_pages</c> lacked a query term.</summary>
    public const string NoMatch = "noMatch";

    /// <summary>The page was deleted between the search and the text check's read of it.</summary>
    public const string NotFound = "notFound";
}
