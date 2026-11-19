using OneNoteMcp.Core.Writes;

namespace OneNoteMcp.Core.Access;

/// <summary>
/// One OneDrive account on this machine: its local <see cref="Folder"/>, and the web address OneNote reports its notebooks
/// under (<c>https://d.docs.live.net/&lt;cid&gt;/</c> for a personal account) when known.
/// </summary>
public sealed record OneDriveAccount(string Folder, string? UrlPrefix = null);

/// <summary>
/// Backlog 13's seam (Phill, 2026-11-18, D1 (c)): the OneDrive folders on this machine, which <see cref="Protection"/>
/// uses to recognise a protected root's notebooks when OneNote reports them by web address. The server fills it from the
/// registry and the <c>%OneDrive*%</c> variables (<c>OneDriveAccounts</c>); tests build it by hand; code that knows none
/// uses <see cref="None"/>, under which every root's last two segments stand for it (Q2 (ii)). Accounts are de-duplicated
/// by folder, case-insensitively, keeping the first address any source gave. A folder that isn't a fully qualified local
/// path is dropped.
/// </summary>
public sealed class OneDriveFolders
{
    /// <summary>Why an export root too close to a OneDrive folder is refused (Q3 (ii)), after the root's own path.</summary>
    public const string ShallowRootReason =
        "is a OneDrive folder, or one folder below one, so the web addresses OneNote reports under it can't be told apart from the user's own cloud notebooks. "
        + "It still counts as protected, but writes are disabled until exportRoots in settings.json names a folder at least two levels inside OneDrive, or outside it.";

    private readonly Lazy<IReadOnlyList<(OneDriveAccount Account, IReadOnlyList<string> Prefixes)>> prefixes;

    /// <param name="accounts">The accounts, from any number of sources.</param>
    public OneDriveFolders(IEnumerable<OneDriveAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var byFolder = new Dictionary<string, OneDriveAccount>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var account in accounts)
        {
            if (PathUnder.Full(account.Folder) is not { } full)
            {
                continue;
            }

            var key = full.TrimEnd(Path.DirectorySeparatorChar);
            var prefix = string.IsNullOrWhiteSpace(account.UrlPrefix) ? null : account.UrlPrefix.TrimEnd('/') + "/";
            if (byFolder.TryGetValue(key, out var known))
            {
                byFolder[key] = known with { UrlPrefix = known.UrlPrefix ?? prefix };
            }
            else
            {
                byFolder[key] = new OneDriveAccount(key, prefix);
                order.Add(key);
            }
        }

        Accounts = [.. order.Select(k => byFolder[k])];

        // Canonical forms touch the file system, so they're made once, and only when a rule needs them.
        prefixes = new(() => [.. Accounts.Select(a => (a, PathUnder.Prefixes(a.Folder)))]);
    }

    /// <summary>
    /// True when the local path <paramref name="path"/> is a known OneDrive folder or lies inside one, in any of its forms
    /// (plain and canonical), at a segment boundary, case-insensitively (story 3.8 Q3: nothing is unpacked there). A path
    /// that isn't a fully qualified local path is held by none.
    /// </summary>
    public bool Holds(string? path) =>
        PathUnder.Prefixes(path).Any(form => FolderPrefixes.Any(f => f.Prefixes.Any(folder => form.StartsWith(folder, StringComparison.OrdinalIgnoreCase))));

    /// <summary>No OneDrive folder known.</summary>
    public static OneDriveFolders None { get; } = new([]);

    /// <summary>The accounts, one per folder.</summary>
    public IReadOnlyList<OneDriveAccount> Accounts { get; }

    /// <summary>Each account with its folder's prefixes (<see cref="PathUnder.Prefixes"/>, each ending in a separator).</summary>
    internal IReadOnlyList<(OneDriveAccount Account, IReadOnlyList<string> Prefixes)> FolderPrefixes => prefixes.Value;

    /// <summary>
    /// The local path the web address <paramref name="url"/> stands for, through the account whose address it starts with
    /// (same host, its path segments first), or null when no account's address covers it.
    /// </summary>
    public string? LocalPathOf(string? url)
    {
        if (!UrlPath.TryParse(url, out var host, out var segments))
        {
            return null;
        }

        foreach (var account in Accounts)
        {
            if (!UrlPath.TryParse(account.UrlPrefix, out var prefixHost, out var prefixSegments)
                || !string.Equals(host, prefixHost, StringComparison.OrdinalIgnoreCase)
                || segments.Count < prefixSegments.Count
                || !prefixSegments.Select((s, i) => string.Equals(s, segments[i], StringComparison.OrdinalIgnoreCase)).All(same => same))
            {
                continue;
            }

            return UrlPath.Below(account.Folder, segments.Skip(prefixSegments.Count));
        }

        return null;
    }

    /// <summary>
    /// <see cref="ShallowRootReason"/> when <paramref name="root"/> is a known OneDrive folder or one segment below one (in
    /// any of its forms), else null.
    /// </summary>
    public string? ShallowRoot(string? root)
    {
        foreach (var form in PathUnder.Prefixes(root))
        {
            foreach (var (_, folderForms) in FolderPrefixes)
            {
                foreach (var folder in folderForms)
                {
                    if (form.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && UrlPath.LocalSegments(form[folder.Length..]).Count <= 1)
                    {
                        return ShallowRootReason;
                    }
                }
            }
        }

        return null;
    }
}
