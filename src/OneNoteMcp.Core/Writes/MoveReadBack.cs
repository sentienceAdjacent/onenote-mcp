using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// The read-back pieces a page or container move shares with <c>restore_from_recycle_bin</c> (stories 3.4, 3.12, 3.5),
/// extracted from the move writes unchanged: finding a page OneNote gave a new ID, and naming why a re-read failed.
/// </summary>
internal static class MoveReadBack
{
    /// <summary>
    /// The page that arrived in <paramref name="after"/>: the one page whose ID wasn't among <paramref name="before"/>,
    /// preferring <paramref name="name"/> (ordinal), then the one at the 0-based <paramref name="index"/> it was sent to;
    /// null when there is none (AD-7 Moves read-back).
    /// </summary>
    internal static string? NewPageId(IReadOnlyList<HierarchyMember> before, HierarchyTarget after, string name, int index)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var known = before.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var added = after.Children.Where(c => !known.Contains(c.Id)).ToList();
        var named = added.Where(c => string.Equals(c.Name, name, StringComparison.Ordinal)).ToList();
        var candidates = named.Count > 0 ? named : added;
        if (candidates.Count <= 1)
        {
            return candidates.SingleOrDefault()?.Id;
        }

        var atSent = index < after.Children.Count ? after.Children[index] : null;
        return atSent is not null && candidates.Contains(atSent) ? atSent.Id : candidates[0].Id;
    }

    /// <summary>Why a re-read failed, for a message: the error code, the deadline or a cancel, or the exception's type. Never content (AD-9).</summary>
    internal static string Why(Exception exception) => exception switch
    {
        OneNoteException one => one.Code.ToString(),
        OperationCanceledException => "the write's deadline or a cancel",
        _ => exception.GetType().Name,
    };
}
