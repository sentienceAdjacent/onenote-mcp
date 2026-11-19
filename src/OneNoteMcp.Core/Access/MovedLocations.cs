namespace OneNoteMcp.Core.Access;

/// <summary>
/// Where a section or section-group move puts sections (story 3.12; AD-7 Moves, AD-21), so the move can be refused before
/// it puts a section on a location that holds a read grant, as <see cref="RenamedLocations"/> does for a rename (story 5.4
/// security review). A moved section keeps its file name and lands in its new parent's folder; a moved group keeps its
/// folder name, with every section beneath it. The new parent's separator is used throughout (<c>/</c> for a URL, else
/// <c>\</c>), so a move between a local and a cloud notebook is predicted too. The prediction assumes OneNote keeps the
/// file and folder names, as spike 3.1 saw IDs and names kept; whether a cloud path encodes characters a local one doesn't
/// is unprobed. A path that doesn't fit that shape is unpredictable (null), and the caller refuses rather than guess.
/// </summary>
internal static class MovedLocations
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>
    /// The path the section at <paramref name="sectionPath"/> has once moved under the notebook or section group at
    /// <paramref name="parentPath"/>, or null when either has no folder part.
    /// </summary>
    internal static string? Section(string sectionPath, string parentPath) =>
        Leaf(sectionPath) is { } leaf ? Under(parentPath, leaf) : null;

    /// <summary>
    /// For the section group at <paramref name="groupPath"/> moved under <paramref name="parentPath"/>: each of
    /// <paramref name="sectionPaths"/> (the sections beneath it) paired with its path afterwards. Null when either path has
    /// no folder part, or any section's path isn't inside the group's folder.
    /// </summary>
    internal static IReadOnlyList<(string Before, string After)>? SectionsInGroup(string groupPath, string parentPath, IReadOnlyList<string> sectionPaths)
    {
        ArgumentNullException.ThrowIfNull(sectionPaths);
        var folder = Trimmed(groupPath);
        if (Leaf(folder) is not { } name || Under(parentPath, name) is not { } moved)
        {
            return null;
        }

        var separator = SeparatorOf(parentPath);
        var result = new List<(string, string)>();
        foreach (var path in sectionPaths)
        {
            var inside = path.Length > folder.Length + 1
                && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                && Separators.Contains(path[folder.Length]);
            if (!inside)
            {
                return null;
            }

            var rest = string.Join(separator, path[(folder.Length + 1)..].Split(Separators));
            result.Add((path, moved + separator + rest));
        }

        return result;
    }

    private static string? Leaf(string? path)
    {
        var trimmed = Trimmed(path);
        var cut = trimmed.LastIndexOfAny(Separators);
        return cut <= 0 || cut == trimmed.Length - 1 ? null : trimmed[(cut + 1)..];
    }

    private static string? Under(string? parentPath, string leaf)
    {
        var parent = Trimmed(parentPath);
        return parent.Length == 0 ? null : parent + SeparatorOf(parent) + leaf;
    }

    private static char SeparatorOf(string? path) => (path ?? string.Empty).Contains("://", StringComparison.Ordinal) ? '/' : '\\';

    private static string Trimmed(string? path) => (path ?? string.Empty).TrimEnd(Separators);
}
