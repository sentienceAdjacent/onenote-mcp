using System.Text.RegularExpressions;
using OneNoteMcp.Core.Access;

namespace OneNoteMcp.Core.Writes;

/// <summary>
/// The path rule <c>open_notebook</c> applies to a notebook folder before anything else (round-3 retrospective F5), kept in
/// one place so that what <c>close_notebook</c> says about reopening, and any later check of a notebook's folder, follow the
/// same rule: the path is made canonical by <see cref="FilePathGate"/> (network, device, relative, hidden-character and
/// too-many-link paths are refused) and must not lie inside a OneDrive-synced folder. Reads the file system only.
/// </summary>
public static partial class NotebookFolderPath
{
    /// <summary>The canonical form <c>open_notebook</c> works on, or null with the <paramref name="refusal"/> (<see cref="FilePathGate.Canonical(string, out FilePathRefusal)"/>).</summary>
    public static string? Canonical(string path, out FilePathRefusal refusal) => FilePathGate.Canonical(path, out refusal);

    /// <summary><see cref="Canonical(string, out FilePathRefusal)"/> over <paramref name="links"/> (the real file system when null), the seam tests use.</summary>
    internal static string? Canonical(string path, FilePathGate.ILinkReader? links, out FilePathRefusal refusal) =>
        links is null ? FilePathGate.Canonical(path, out refusal) : FilePathGate.Canonical(path, links, out refusal);

    /// <summary>True when the canonical path lies inside a OneDrive-synced folder, which <c>open_notebook</c> refuses (3.8 Q3).</summary>
    public static bool InOneDrive(string canonical, OneDriveFolders oneDrive)
    {
        ArgumentNullException.ThrowIfNull(oneDrive);
        return oneDrive.Holds(canonical);
    }

    /// <summary>
    /// True when <c>open_notebook</c>'s path rule takes <paramref name="path"/>: it has a canonical form and that form isn't in
    /// OneDrive. Whether a notebook is really there is <c>open_notebook</c>'s later check, not this rule's.
    /// </summary>
    public static bool Openable(string? path, OneDriveFolders oneDrive) => Openable(path, oneDrive, links: null);

    /// <summary><see cref="Openable(string?, OneDriveFolders)"/> over <paramref name="links"/>, the seam tests use.</summary>
    internal static bool Openable(string? path, OneDriveFolders oneDrive, FilePathGate.ILinkReader? links) =>
        !string.IsNullOrWhiteSpace(path) && Canonical(path, links, out _) is { } canonical && !InOneDrive(canonical, oneDrive);

    /// <summary>
    /// The forms an open notebook's <paramref name="notebookPath"/> names its folder by (round-3 retrospective F12), for
    /// <c>open_notebook</c>'s Already-open match and <c>create_notebook</c>'s open-folder check alike: its <c>NotebookKey</c>,
    /// its <c>\\localhost\X$</c> form read as <c>X:\</c>, and each of those made canonical over <paramref name="links"/>
    /// (the real file system when null) when it resolves (spike 4.1 O9, O10). Empty for a URL or a blank path.
    /// </summary>
    internal static IReadOnlyList<string> OpenForms(string? notebookPath, FilePathGate.ILinkReader? links)
    {
        if (string.IsNullOrWhiteSpace(notebookPath) || CloudNotebookCreate.IsUrl(notebookPath))
        {
            return [];
        }

        var forms = new List<string> { GrantKeys.NotebookKey(notebookPath) };
        if (MapLocalhostAdminShare(notebookPath) is { } mapped)
        {
            forms.Add(GrantKeys.NotebookKey(mapped));
        }

        foreach (var form in forms.ToList())
        {
            if (Canonical(form, links, out _) is { } resolved)
            {
                forms.Add(GrantKeys.NotebookKey(resolved));
            }
        }

        return forms;
    }

    /// <summary><c>\\localhost\X$\rest</c> as <c>X:\rest</c>, ignoring case; null for anything else (other servers or shares).</summary>
    internal static string? MapLocalhostAdminShare(string path)
    {
        var match = LocalhostAdminShare().Match(path.Replace('/', '\\'));
        return match.Success
            ? char.ToUpperInvariant(match.Groups["drive"].Value[0]) + ":" + (match.Groups["rest"].Value.Length == 0 ? "\\" : match.Groups["rest"].Value)
            : null;
    }

    [GeneratedRegex(@"^\\\\localhost\\(?<drive>[A-Za-z])\$(?<rest>\\.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalhostAdminShare();
}
