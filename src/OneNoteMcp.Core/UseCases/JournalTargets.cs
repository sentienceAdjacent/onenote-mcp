using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// Finding a journal entry's original parent among a notebook's live nodes by its <c>parentNamePath</c> (AD-8), shared by
/// <c>restore_from_journal</c> (story 2.11, extracted unchanged) and <c>restore_from_recycle_bin</c> (story 3.5). A path
/// compares segment by segment, ignoring case, as Windows names compare; names can't hold <c>/</c>, so the split is safe.
/// An empty path matches nothing here: for a section's entry it means the notebook itself, which the caller decides.
/// </summary>
internal static class JournalTargets
{
    /// <summary>The live sections of <paramref name="live"/> whose groups and name equal <paramref name="namePath"/> (a page entry's <c>parentNamePath</c>).</summary>
    internal static IReadOnlyList<NotebookSection> SectionsAt(IReadOnlyList<NotebookSection> live, string namePath)
    {
        ArgumentNullException.ThrowIfNull(live);
        return [.. live.Where(s => SamePath([.. s.Groups, s.Name], namePath))];
    }

    /// <summary>The live section groups of <paramref name="live"/> whose groups and name equal <paramref name="namePath"/> (a section entry's <c>parentNamePath</c>).</summary>
    internal static IReadOnlyList<NotebookSectionGroup> GroupsAt(IReadOnlyList<NotebookSectionGroup> live, string namePath)
    {
        ArgumentNullException.ThrowIfNull(live);
        return [.. live.Where(g => SamePath([.. g.Groups, g.Name], namePath))];
    }

    private static bool SamePath(IReadOnlyList<string> own, string namePath)
    {
        if (string.IsNullOrEmpty(namePath))
        {
            return false;
        }

        var segments = namePath.Split('/');
        return segments.Length == own.Count
            && segments.Zip(own).All(pair => string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));
    }
}
