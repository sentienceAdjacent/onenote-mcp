using OneNoteMcp.Core.Writes;
using OneNoteMcp.Xml;

namespace OneNoteMcp.Core.Access;

/// <summary>
/// Entry 15, Phill's Q1 (2026-11-17): an export root must not make the user's own notebooks protected. A root is a problem
/// when an open local notebook lies under it that <c>open_package</c> didn't make (it marks each folder it creates with
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
    private readonly Lock gate = new();
    private (string Key, DateTimeOffset At, string? Problem)? last;

    /// <param name="gateway">Reads the open notebooks.</param>
    /// <param name="clock">The clock the cache runs on; the system clock when null.</param>
    /// <param name="fileExists">Tells whether a marker file exists; <see cref="File.Exists"/> when null.</param>
    public ExportRootGuard(IOneNoteGateway gateway, TimeProvider? clock = null, Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        this.gateway = gateway;
        this.clock = clock ?? TimeProvider.System;
        this.fileExists = fileExists ?? File.Exists;
    }

    /// <summary>
    /// The first problem with <paramref name="exportRoots"/> given the open notebooks in <paramref name="notebooksXml"/>,
    /// as a sentence naming the root (the user's own setting) and why, or null when there is none. Cloud notebooks
    /// (a URL path) never count.
    /// </summary>
    public static string? Problem(IReadOnlyList<string> exportRoots, string notebooksXml, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(exportRoots);
        ArgumentNullException.ThrowIfNull(fileExists);
        var notebooks = HierarchyNode.Notebooks(notebooksXml)
            .Select(n => PathUnder.Full(n.Path))
            .OfType<string>()
            .ToList();
        foreach (var root in exportRoots)
        {
            if (PathUnder.Full(root) is not { } fullRoot)
            {
                continue;
            }

            foreach (var notebook in notebooks)
            {
                if (PathUnder.IsUnder(notebook, [fullRoot]) && !fileExists(Path.Combine(notebook, MarkerFileName)))
                {
                    return $"The export root {root} holds a notebook of the user's, so every section in it counts as password-protected. "
                        + "Change exportRoots in settings.json to a folder that holds no notebooks.";
                }

                if (string.Equals(Trim(fullRoot), Trim(notebook), StringComparison.OrdinalIgnoreCase) || PathUnder.IsUnder(fullRoot, [notebook]))
                {
                    return $"The export root {root} is inside a notebook's folder, so that notebook's sections count as password-protected. "
                        + "Change exportRoots in settings.json to a folder outside every notebook.";
                }
            }
        }

        return null;
    }

    /// <summary>The problem with <paramref name="exportRoots"/> now, or null; one <c>GetHierarchy</c> per <see cref="CacheFor"/>.</summary>
    public async Task<string?> ProblemAsync(IReadOnlyList<string> exportRoots, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exportRoots);
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
            problem = Problem(exportRoots, xml, fileExists);
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
