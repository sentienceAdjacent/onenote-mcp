using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.UseCases;

/// <summary>
/// The checks a hierarchy create (story 5.3) and a hierarchy rename or reorder (story 5.4) share, so each is written once:
/// whether a node is the recycle bin or in it, and which live children of a parent already use a name. Names compare
/// ignoring case, as Windows file names do (5.3 Q4). Phrases never quote a name (AD-9).
/// </summary>
internal static class HierarchyRules
{
    /// <summary>
    /// Why a node can't be written to because of the recycle bin, as a phrase after its ID (<c>"is a notebook's recycle
    /// bin"</c>, <c>"is in the recycle bin"</c>); null when it is neither (AD-7: <c>isRecycleBin</c>, <c>isInRecycleBin</c>,
    /// <c>isDeletedPages</c>).
    /// </summary>
    internal static string? RecycleBinProblem(bool isRecycleBin, bool inRecycleBin) =>
        isRecycleBin ? "is a notebook's recycle bin" : inRecycleBin ? "is in the recycle bin" : null;

    /// <summary>
    /// The live sections and section groups under <paramref name="parent"/> named <paramref name="name"/>, ignoring case,
    /// in document order, leaving out <paramref name="exceptId"/> (a node being renamed doesn't clash with itself).
    /// </summary>
    internal static IReadOnlyList<HierarchyChild> Named(HierarchyParent parent, string name, string? exceptId = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return [.. parent.Children.Where(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)
            && (exceptId is null || !string.Equals(c.Id, exceptId, StringComparison.Ordinal)))];
    }

    /// <summary>"section" or "section group", for messages.</summary>
    internal static string Noun(HierarchyKind kind) => kind switch
    {
        HierarchyKind.Notebook => "notebook",
        HierarchyKind.SectionGroup => "section group",
        HierarchyKind.Section => "section",
        HierarchyKind.Page => "page",
        _ => "object",
    };
}
