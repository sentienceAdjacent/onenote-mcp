namespace OneNoteMcp.Core.Access;

/// <summary>
/// The grant keys (AD-6, AD-21): the one place a stored grant's key is computed and compared. Every unit that keys a
/// grant uses it: the access check, elicitation, the CLI, <c>list_grants</c> and the settings reader.
/// </summary>
public static class GrantKeys
{
    /// <summary>Keys compare ignoring case (AD-6). Two keys in <c>settings.json</c> that differ only in case make it invalid.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// <c>NotebookKey</c> (AD-6). Its only input is a <c>one:Notebook@path</c> read live from <c>GetHierarchy</c>. The
    /// transform trims trailing <c>\</c> and <c>/</c>, and nothing else: no decoding, no <c>GetFullPath</c>.
    /// </summary>
    public static string NotebookKey(string notebookPath) => Transform(notebookPath);

    /// <summary>
    /// <c>SectionKey</c> (AD-21). Its only input is a <c>one:Section@path</c> read live from <c>GetHierarchy</c>, and it
    /// uses <see cref="NotebookKey"/>'s transform and comparison. A renamed or moved section gets a new key.
    /// </summary>
    public static string SectionKey(string sectionPath) => Transform(sectionPath);

    /// <summary>
    /// The one name matcher (AD-6, AD-21), used by the grant dialog and the CLI alike: the typed text equals the
    /// section's displayed name, both trimmed, ignoring case. An empty name never matches.
    /// </summary>
    public static bool NameMatches(string? typed, string? name)
    {
        var a = typed?.Trim() ?? string.Empty;
        var b = name?.Trim() ?? string.Empty;
        return b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string Transform(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.TrimEnd('\\', '/');
    }
}
