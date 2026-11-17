using OneNoteMcp.Core.Results;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// The basis check every <c>UpdateHierarchy</c> write shares (AD-17): renames and reorders (<see cref="HierarchyWrite"/>,
/// story 5.4) and the page move (<see cref="MovePageWrite"/>, story 3.4). A hierarchy write's basis is one
/// <c>lastModifiedTime</c>, the node's own from <c>GetHierarchy</c> (for a moved page, its hierarchy date). Step 1 refuses a
/// basis that carries a hash or a date OneNote can't have written; step 5 compares the date with step 3's as UTC instants.
/// <c>UpdateHierarchy</c> takes no date, so this comparison is the only check, and a weak one. Messages carry IDs only (AD-9).
/// </summary>
internal static class HierarchyBasis
{
    /// <summary>
    /// Step 1: <see cref="ErrorCode.InvalidArgument"/>, with <paramref name="userAction"/>, for a basis carrying a page or
    /// element hash, or a <c>lastModifiedTime</c> that isn't a date OneNote writes. No basis, or a date alone, passes.
    /// </summary>
    internal static void CheckArgument(PageWriteBasis? basis, string userAction)
    {
        if (basis is null)
        {
            return;
        }

        if (basis.PageHash is not null || basis.ElementHashes is { Count: > 0 })
        {
            throw new OneNoteException(
                ErrorCode.InvalidArgument,
                "A hierarchy write's basis carries only lastModifiedTime (AD-17), and this one carries a page hash, so nothing was written.",
                userAction: userAction,
                retryable: false);
        }

        if (basis.LastModifiedTime is not null && OneNoteDates.ToUtc(basis.LastModifiedTime) is null)
        {
            throw new OneNoteException(
                ErrorCode.InvalidArgument,
                "The basis lastModifiedTime isn't a date OneNote writes, so nothing was written.",
                userAction: userAction,
                retryable: false);
        }
    }

    /// <summary>
    /// Step 5: compares <paramref name="basis"/> with <paramref name="current"/>, step 3's <c>lastModifiedTime</c> of
    /// <paramref name="noun"/> <paramref name="id"/>. Returns the warning to report (<c>noBasis</c>, or <c>partialBasis</c>
    /// when the basis or OneNote has no date), or null when the dates match; a different date is
    /// <see cref="ErrorCode.StaleWrite"/>, saying nothing was <paramref name="done"/>. The basis date was checked in step 1.
    /// </summary>
    internal static ToolWarning? Compare(
        PageWriteBasis? basis,
        string? current,
        string noun,
        string id,
        string done,
        string noBasisUserAction,
        string staleUserAction)
    {
        if (basis is null)
        {
            return new ToolWarning(
                ToolWarnings.NoBasis,
                $"No basis was passed, so {noun} {id} was checked against its current version (read internally; nothing of it is returned): a change made since your own read could be overwritten.",
                noBasisUserAction);
        }

        if (basis.LastModifiedTime is null)
        {
            return new ToolWarning(
                ToolWarnings.PartialBasis,
                $"The basis for {noun} {id} is partial: it has no lastModifiedTime, so its current version (read internally; nothing of it is returned) was used.",
                noBasisUserAction);
        }

        if (OneNoteDates.ToUtc(current) is not { } now)
        {
            return new ToolWarning(
                ToolWarnings.PartialBasis,
                $"OneNote reported no lastModifiedTime for {noun} {id}, so the basis couldn't be compared.",
                noBasisUserAction);
        }

        if (OneNoteDates.ToUtc(basis.LastModifiedTime) != now)
        {
            throw new OneNoteException(
                ErrorCode.StaleWrite,
                $"{Capitalized(noun)} {id} changed since the basis was read (its lastModifiedTime differs), so nothing was {done}.",
                userAction: staleUserAction);
        }

        return null;
    }

    internal static string Capitalized(string noun) =>
        string.IsNullOrEmpty(noun) ? noun : char.ToUpperInvariant(noun[0]) + noun[1..];
}
