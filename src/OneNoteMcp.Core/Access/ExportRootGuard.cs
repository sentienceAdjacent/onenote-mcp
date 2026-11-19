using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Access;

/// <summary>
/// Entry 15, Phill's Q1 (2026-11-17): an export root must not make the user's own notebooks protected. A root is a problem
/// when an open notebook (local, or by web address: backlog 13) lies under it that <c>open_package</c> didn't make (it marks each folder it creates with
/// <see cref="MarkerFileName"/>), or when the root lies inside an open notebook's folder. Reads go on with a warning and the
/// root still protects (fails closed); writes fail with <c>SettingsInvalid</c> until the user fixes the setting. The check
/// reads the open notebooks with one <c>GetHierarchy</c>, and keeps its verdict for <see cref="CacheFor"/> per set of
/// roots, so a run of calls costs one read. A failed read gives no verdict: the call itself will meet the same error.
/// </summary>
public sealed class ExportRootGuard
{
    /// <summary>The file <c>open_package</c> writes into each folder it creates (3.8), so its notebooks don't trip the guard.</summary>
    public const string MarkerFileName = ".onenote-mcp-opened";

    /// <summary>How long a verdict is kept.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly IOneNoteGateway gateway;
    private readonly TimeProvider clock;
    private readonly Func<string, bool> fileExists;
    private readonly Func<OneDriveFolders> oneDrive;
    private readonly Lock gate = new();
    private (string Key, DateTimeOffset At, string? Problem)? last;

    /// <param name="gateway">Reads the open notebooks.</param>
    /// <param name="clock">The clock the cache runs on; the system clock when null.</param>
    /// <param name="fileExists">Tells whether a marker file exists; <see cref="File.Exists"/> when null.</param>
    /// <param name="oneDrive">The OneDrive folders on this machine (backlog 13); none known when null.</param>
    public ExportRootGuard(IOneNoteGateway gateway, TimeProvider? clock = null, Func<string, bool>? fileExists = null, Func<OneDriveFolders>? oneDrive = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        this.gateway = gateway;
        this.clock = clock ?? TimeProvider.System;
        this.fileExists = fileExists ?? File.Exists;
        this.oneDrive = oneDrive ?? (() => OneDriveFolders.None);
    }

    /// <summary>
    /// The first problem with <paramref name="exportRoots"/> given the open notebooks in <paramref name="notebooksXml"/>,
    /// as a sentence naming the root (the user's own setting) and why, or null when there is none. A notebook OneNote
    /// reports by web address (backlog 13, Phill's Q4 (i), 2026-11-18) counts by the same rule as <see cref="Protection"/>:
    /// one under a root's tail is a problem unless its local folder, mapped back through its account's address in
    /// <paramref name="oneDrive"/> or else through the root's tail, holds the marker; and one whose folder, mapped back
    /// through its account, holds a root is a problem too.
    /// </summary>
    public static string? Problem(IReadOnlyList<string> exportRoots, string notebooksXml, Func<string, bool> fileExists, OneDriveFolders? oneDrive = null)
    {
        ArgumentNullException.ThrowIfNull(exportRoots);
        ArgumentNullException.ThrowIfNull(fileExists);
        oneDrive ??= OneDriveFolders.None;
        if (ShallowProblem(exportRoots, oneDrive) is { } shallow)
        {
            return shallow;
        }

        var notebooks = HierarchyNode.Notebooks(notebooksXml).Select(n => n.Path).ToList();
        foreach (var root in exportRoots)
        {
            if (PathUnder.Full(root) is not { } fullRoot)
            {
                continue;
            }

            var byRoot = new Protection([fullRoot], oneDrive: oneDrive);
            foreach (var path in notebooks)
            {
                var local = PathUnder.Full(path);
                var url = local is null && UrlPath.TryParse(path, out _, out _);
                if (url)
                {
                    local = oneDrive.LocalPathOf(path);
                    if (byRoot.UnderRoot(path))
                    {
                        // Under the root by its address: exempt only when the folder it maps back to is under the root and marked.
                        var mapped = local is { } exact && PathUnder.IsUnder(exact, [fullRoot]) ? exact : byRoot.LocalPathOfUrl(path);
                        if (mapped is null || !fileExists(Path.Combine(mapped, MarkerFileName)))
                        {
                            return Holds(root);
                        }

                        continue;
                    }
                }

                if (local is null)
                {
                    continue;
                }

                if (PathUnder.IsUnder(local, [fullRoot]) && !fileExists(Path.Combine(local, MarkerFileName)))
                {
                    return Holds(root);
                }

                if (string.Equals(Trim(fullRoot), Trim(local), StringComparison.OrdinalIgnoreCase) || PathUnder.IsUnder(fullRoot, [local]))
                {
                    return $"The export root {root} is inside a notebook's folder, so that notebook's sections count as password-protected. "
                        + "Change exportRoots in settings.json to a folder outside every notebook.";
                }
            }
        }

        return null;

        static string Holds(string root) =>
            $"The export root {root} holds a notebook of the user's, so every section in it counts as password-protected. "
            + "Change exportRoots in settings.json to a folder that holds no notebooks.";
    }

    /// <summary>
    /// Backlog 13, Phill's Q3 (ii) (2026-11-18): the first export root that is a OneDrive folder in
    /// <paramref name="oneDrive"/>, or one folder below one, as a sentence naming it and why, or null. Such a root keeps
    /// protecting (its tail is empty or one segment, so it protects broadly: fails closed); reads warn
    /// <c>settingsInvalid</c> and writes fail with <c>SettingsInvalid</c> until the user changes it. Reads no OneNote.
    /// </summary>
    public static string? ShallowProblem(IReadOnlyList<string> exportRoots, OneDriveFolders oneDrive)
    {
        ArgumentNullException.ThrowIfNull(exportRoots);
        ArgumentNullException.ThrowIfNull(oneDrive);
        return exportRoots.FirstOrDefault(r => oneDrive.ShallowRoot(r) is not null) is { } root
            ? $"The export root {root} {OneDriveFolders.ShallowRootReason}"
            : null;
    }

    /// <summary>The problem with <paramref name="exportRoots"/> now, or null; one <c>GetHierarchy</c> per <see cref="CacheFor"/>.</summary>
    public async Task<string?> ProblemAsync(IReadOnlyList<string> exportRoots, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exportRoots);
        if (ShallowProblem(exportRoots, oneDrive()) is { } shallow)
        {
            return shallow;
        }

        var key = string.Join('|', exportRoots);
        var now = clock.GetUtcNow();
        lock (gate)
        {
            if (last is { } cached && cached.Key == key && now - cached.At < CacheFor)
            {
                return cached.Problem;
            }
        }

        string xml;
        try
        {
            xml = await gateway.GetHierarchyAsync(string.Empty, HierarchyScope.Notebooks, ct).ConfigureAwait(false);
        }
        catch (OneNoteException)
        {
            return null;
        }

        string? problem;
        try
        {
            problem = Problem(exportRoots, xml, fileExists, oneDrive());
        }
        catch (OneNoteXmlException)
        {
            return null;
        }

        lock (gate)
        {
            last = (key, now, problem);
        }

        return problem;
    }

    private static string Trim(string path) => path.TrimEnd(Path.DirectorySeparatorChar);
}
