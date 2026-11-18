namespace OneNoteMcp.Core.Writes;

/// <summary>
/// Entry 15: the one "under a root" test, shared by exports (AD-11: where a file may be written) and the protected-by-path
/// rule (AD-21: which sections count as protected), so the two can't drift apart. A path is under a root when it sits
/// strictly inside it, at a path-segment boundary (<c>…\exports2\x.one</c> is not under <c>…\exports</c>),
/// case-insensitively, after <see cref="Path.GetFullPath(string)"/> (separators and <c>..</c> normalised). A root counts in
/// both its <see cref="FilePathGate.Canonical"/> form (links followed) and its plain full path, so a root on a mapped drive,
/// or one that doesn't exist yet, still counts. A path that isn't a fully qualified local path (a URL, a relative path,
/// empty) is under nothing. No file I/O happens for the path itself.
/// </summary>
public static class PathUnder
{
    /// <summary>True when <paramref name="path"/> is under any of <paramref name="roots"/>.</summary>
    public static bool IsUnder(string? path, IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (Full(path) is not { } full)
        {
            return false;
        }

        foreach (var root in roots)
        {
            foreach (var prefix in Prefixes(root))
            {
                if (IsUnderPrefix(full, prefix))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The prefixes <paramref name="root"/> stands for, each ending in a separator: its canonical form and its plain full
    /// path (one entry when they agree). Empty for a root that isn't a fully qualified local path.
    /// </summary>
    public static IReadOnlyList<string> Prefixes(string? root)
    {
        if (Full(root) is not { } full)
        {
            return [];
        }

        var prefixes = new List<string>(2) { WithSeparator(full) };
        if (FilePathGate.Canonical(full, out _) is { } canonical && !prefixes.Contains(WithSeparator(canonical), StringComparer.OrdinalIgnoreCase))
        {
            prefixes.Add(WithSeparator(canonical));
        }

        return prefixes;
    }

    /// <summary>True when the full path <paramref name="fullPath"/> sits strictly inside <paramref name="prefix"/> (which ends in a separator).</summary>
    public static bool IsUnderPrefix(string fullPath, string prefix) =>
        fullPath.Length > prefix.Length && fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary><see cref="Path.GetFullPath(string)"/> of a fully qualified local path, else null. Never throws.</summary>
    public static string? Full(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains("://", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string WithSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
