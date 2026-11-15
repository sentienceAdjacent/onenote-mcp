namespace OneNoteMcp.Core.Access;

/// <summary>
/// Where a rename moves sections, so the rename can be refused before it hands one section's read grant to another
/// (AD-21; story 5.4 security review, 2026-11-13). A read grant is keyed by the section's path
/// (<see cref="GrantKeys.SectionKey"/>), and a rename changes paths: a section's file becomes <c>&lt;new name&gt;.one</c>
/// beside the old one, and a section group's folder becomes <c>&lt;new name&gt;</c>, moving every section beneath it. Paths
/// are local (<c>\</c>) or URLs (<c>/</c>); the separator before the renamed part is kept. The prediction assumes OneNote
/// names the file or folder after the new name, as it did in spike 5.1; a node whose path doesn't fit that shape is
/// unpredictable (null), and the caller refuses the rename rather than guess.
/// </summary>
internal static class RenamedLocations
{
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>
    /// The path the section at <paramref name="sectionPath"/> has once renamed to <paramref name="name"/> (as OneNote
    /// shows it, without <c>.one</c>), or null when its path has no folder part.
    /// </summary>
    internal static string? Section(string sectionPath, string name) => Beside(sectionPath, name + SectionFileNames.Extension);

    /// <summary>
    /// For the section group at <paramref name="groupPath"/> renamed to <paramref name="name"/>: each of
    /// <paramref name="sectionPaths"/> (the sections beneath it) paired with its path afterwards. Null when the group's
    /// path has no folder part, or any section's path isn't inside the group's folder.
    /// </summary>
    internal static IReadOnlyList<(string Before, string After)>? SectionsInGroup(string groupPath, string name, IReadOnlyList<string> sectionPaths)
    {
        ArgumentNullException.ThrowIfNull(sectionPaths);
        var folder = Trimmed(groupPath);
        if (Beside(folder, name) is not { } renamed)
        {
            return null;
        }

        var moved = new List<(string, string)>();
        foreach (var path in sectionPaths)
        {
            var inside = path.Length > folder.Length + 1
                && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                && Separators.Contains(path[folder.Length]);
            if (!inside)
            {
                return null;
            }

            moved.Add((path, renamed + path[folder.Length..]));
        }

        return moved;
    }

    private static string? Beside(string path, string leaf)
    {
        var trimmed = Trimmed(path);
        var cut = trimmed.LastIndexOfAny(Separators);
        return cut <= 0 ? null : trimmed[..(cut + 1)] + leaf;
    }

    private static string Trimmed(string? path) => (path ?? string.Empty).TrimEnd(Separators);
}
