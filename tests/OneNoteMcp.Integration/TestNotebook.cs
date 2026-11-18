using System.Text.RegularExpressions;
using System.Xml.Linq;
using OneNoteMcp.Com;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Xml;
using CoreScope = OneNoteMcp.Core.HierarchyScope;

namespace OneNoteMcp.Integration;

/// <summary>
/// The integration suite's only write path (AD-16). It creates or reuses the <c>MCP Test</c> notebook in a scratch
/// folder, seeds one section and one page, and runs any other write only after a live <c>GetHierarchy</c> shows the
/// target inside that notebook or, from story 2.2, inside the OneDrive notebook <c>MCP Test Cloud</c>, which it finds
/// but never creates. It never writes to any other notebook, never closes a notebook and never navigates.
/// The folder sits outside <c>%LOCALAPPDATA%\OneNoteMcp\</c> so <c>uninstall --purge</c> never touches it; the
/// notebook stays open in OneNote for later stories' fixtures.
/// </summary>
internal sealed class TestNotebook
{
    public const string FolderOverrideVariable = "ONENOTE_MCP_TEST_DIR";
    public const string NotebookName = "MCP Test";
    public const string SeedSectionName = "Seed";

    /// <summary>Story 2.2's OneDrive test notebook. Phill created it; the suite only finds it (never creates or opens it).</summary>
    public const string CloudNotebookName = "MCP Test Cloud";

    /// <summary>
    /// Story 5.1's local throwaway notebooks: <c>MCP Test Throwaway &lt;stamp&gt;…</c>, created by the probes in the folder
    /// beside MCP Test's (<see cref="TestNotebookGuard.ThrowawayRoot"/>) and deleted by them. Only these take
    /// notebook-level writes and deletes.
    /// </summary>
    public const string ThrowawayNamePrefix = "MCP Test Throwaway ";

    /// <summary>Story 5.1's OneDrive throwaway notebook. Phill creates it; the suite only finds it, then deletes it.</summary>
    public const string CloudThrowawayName = "MCP Test Throwaway Cloud";

    /// <summary>
    /// Spike 3.1 (Q5): the one notebook the label probe may touch on Phill's Office 365 tenant. Phill creates it; the
    /// suite adopts it by this exact name and an <c>https</c> path off OneDrive personal (<see cref="TestNotebookGuard.IsTenantTest"/>).
    /// </summary>
    public const string TenantNotebookName = "MCP Test Tenant";

    private TestNotebook(string folder, string notebookId, string sectionId, string pageId)
    {
        Folder = folder;
        NotebookId = notebookId;
        SectionId = sectionId;
        PageId = pageId;
    }

    public string Folder { get; }

    public string NotebookId { get; }

    public string SectionId { get; }

    public string PageId { get; }

    /// <summary><see cref="FolderOverrideVariable"/> when set, else <c>%LOCALAPPDATA%\OneNoteMcp-Test\MCP Test</c>.</summary>
    public static string ResolveFolder(Func<string, string?> getEnvironmentVariable)
    {
        var overridden = getEnvironmentVariable(FolderOverrideVariable);
        var folder = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneNoteMcp-Test", NotebookName)
            : overridden;
        return Path.GetFullPath(TestNotebookGuard.TrimPath(folder));
    }

    /// <summary>Opens (creating if needed) MCP Test, then seeds the <c>Seed</c> section and one page if they're missing.</summary>
    public static async Task<TestNotebook> EnsureAsync(ComGateway gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        var folder = ResolveFolder(Environment.GetEnvironmentVariable);

        var notebooks = await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, ct);
        var notebookId = TestNotebookGuard.FindNotebookId(notebooks, folder);
        if (notebookId is null)
        {
            TestNotebookGuard.EnsureFolderIsOurs(folder);
            Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
            notebookId = await gateway.InvokeAsync(
                app =>
                {
                    app.OpenHierarchy(folder, string.Empty, out var id, CreateFileType.cftNotebook);
                    return id;
                },
                ct,
                retryOnDisconnect: false,
                method: "OpenHierarchy(MCP Test)");

            // Read the notebook back by its path, never trusting the returned ID alone.
            notebooks = await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, ct);
            if (TestNotebookGuard.FindNotebookId(notebooks, folder) != notebookId)
            {
                throw new InvalidOperationException($"OpenHierarchy returned a notebook that isn't at {folder}.");
            }
        }

        var tree = await gateway.GetHierarchyAsync(notebookId, CoreScope.Pages, ct);
        var sectionId = TestNotebookGuard.FindSectionId(tree, SeedSectionName);
        if (sectionId is null)
        {
            sectionId = await WriteAsync(
                gateway,
                notebookId,
                app =>
                {
                    app.OpenHierarchy(SeedSectionName + ".one", notebookId, out var id, CreateFileType.cftSection);
                    return id;
                },
                "OpenHierarchy(Seed section)",
                ct);
            tree = await gateway.GetHierarchyAsync(notebookId, CoreScope.Pages, ct);
        }

        var pageId = TestNotebookGuard.FirstPageId(tree, sectionId);
        if (pageId is null)
        {
            pageId = await WriteAsync(
                gateway,
                sectionId,
                app =>
                {
                    app.CreateNewPage(sectionId, out var id, NewPageStyle.npsBlankPageWithTitle);
                    return id;
                },
                "CreateNewPage(Seed page)",
                ct);
        }

        return new TestNotebook(folder, notebookId, sectionId, pageId);
    }

    /// <summary>
    /// The guarded write: refuses unless a live <c>GetHierarchy</c> shows <paramref name="targetId"/> is MCP Test, MCP
    /// Test Cloud (story 2.2; found, never created) or inside one of them. From story 5.1 it also accepts a throwaway
    /// notebook named in <paramref name="throwawayNotebookIds"/>, but only while a live read still shows that ID as a
    /// throwaway (<see cref="TestNotebookGuard.ThrowawayNotebookIds"/>). Runs through the gateway's raw seam with no
    /// automatic retry (AD-3).
    /// </summary>
    public static Task<T> WriteAsync<T>(
        ComGateway gateway,
        string targetId,
        Func<IApplication, T> write,
        string method,
        CancellationToken ct,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? throwawayNotebookIds = null) =>
        GuardedAsync(gateway, [targetId], write, method, ct, throwawayNotebookIds, created: null);

    /// <summary>
    /// Story 5.1's guarded <c>DeleteHierarchy</c>. Before the <see cref="WriteAsync{T}"/> checks, it refuses the MCP Test
    /// and MCP Test Cloud notebook IDs themselves and any ID not in <paramref name="created"/> (the objects this run
    /// made), so no fixture can be deleted (<see cref="TestNotebookGuard.CheckDelete"/>).
    /// </summary>
    public static async Task DeleteHierarchyAsync(
        ComGateway gateway,
        string targetId,
        DateTime expected,
        bool permanently,
        IReadOnlySet<string> created,
        IReadOnlyDictionary<string, IReadOnlySet<string>> throwawayNotebookIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        var method = permanently ? "DeleteHierarchy(permanent)" : "DeleteHierarchy(soft)";
        var notebooks = await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, ct);
        TestNotebookGuard.CheckDelete(method, targetId, TestNotebookGuard.TestNotebookIds(notebooks, ResolveFolder(Environment.GetEnvironmentVariable)), created);
        await GuardedAsync(
            gateway,
            [targetId],
            app =>
            {
                app.DeleteHierarchy(targetId, expected, permanently);
                return true;
            },
            method,
            ct,
            throwawayNotebookIds,
            created);
    }

    /// <summary>
    /// Story 5.1's guarded <c>UpdateHierarchy</c>. Every <c>ID</c> in <paramref name="xml"/> must be an object this run
    /// made, never the MCP Test or MCP Test Cloud notebook (<see cref="TestNotebookGuard.CheckHierarchyXml"/>), and each
    /// must be inside a test or throwaway notebook, or gone from every notebook (a probe of a stale ID).
    /// </summary>
    /// <remarks>
    /// Spike 3.1: with <paramref name="allowRecycleBins"/> (only R1's second call and R5), the XML may also name MCP
    /// Test's and MCP Test Cloud's own <c>OneNote_RecycleBin</c> group and Deleted Pages sections, read live here
    /// (<see cref="TestNotebookGuard.RecycleBinIds"/>); never a throwaway's, never anything else.
    /// </remarks>
    public static async Task UpdateHierarchyAsync(
        ComGateway gateway,
        string xml,
        IReadOnlySet<string> created,
        IReadOnlyDictionary<string, IReadOnlySet<string>> throwawayNotebookIds,
        string method,
        CancellationToken ct,
        bool allowRecycleBins = false)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        var notebooks = await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, ct);
        var testIds = TestNotebookGuard.TestNotebookIds(notebooks, ResolveFolder(Environment.GetEnvironmentVariable));
        HashSet<string>? recycleBins = null;
        if (allowRecycleBins)
        {
            recycleBins = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in testIds)
            {
                recycleBins.UnionWith(TestNotebookGuard.RecycleBinIds(await gateway.GetHierarchyAsync(id, CoreScope.Sections, ct)));
            }
        }

        var ids = TestNotebookGuard.CheckHierarchyXml(method, xml, testIds, created, recycleBins);
        await GuardedAsync(
            gateway,
            ids,
            app =>
            {
                app.UpdateHierarchy(xml, XMLSchema.xs2013);
                return true;
            },
            method,
            ct,
            throwawayNotebookIds,
            created);
    }

    /// <summary>
    /// Story 5.1: a write aimed at an object this run made and then deleted (a stale-ID probe). Refuses unless
    /// <paramref name="goneId"/> is in <paramref name="created"/> and a full live hierarchy read finds it in no notebook.
    /// </summary>
    public static async Task<T> WriteToGoneAsync<T>(ComGateway gateway, string goneId, Func<IApplication, T> write, string method, IReadOnlySet<string> created, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(created);
        if (!created.Contains(goneId))
        {
            throw new InvalidOperationException($"Refusing {method}: {goneId} was not created by this run.");
        }

        if (TestNotebookGuard.AnyContains(await gateway.GetHierarchyAsync(string.Empty, CoreScope.Pages, ct), goneId))
        {
            throw new InvalidOperationException($"Refusing {method}: {goneId} still exists.");
        }

        return await gateway.InvokeAsync(write, ct, retryOnDisconnect: false, method: method);
    }

    /// <summary>
    /// Story 5.1's guarded <c>Publish</c>: the source must pass the <see cref="WriteAsync{T}"/> checks, and
    /// <paramref name="targetPath"/> must be under <paramref name="publishRoot"/>.
    /// </summary>
    /// <remarks>
    /// Spike 3.1 (X3): <paramref name="callGateway"/>, when given, makes the <c>Publish</c> call itself through a second
    /// gateway with a shorter call limit, after <paramref name="gateway"/> ran the guard's reads at the normal limit.
    /// </remarks>
    public static Task PublishAsync(
        ComGateway gateway,
        string sourceId,
        string targetPath,
        PublishFormat format,
        string publishRoot,
        IReadOnlyDictionary<string, IReadOnlySet<string>> throwawayNotebookIds,
        CancellationToken ct,
        ComGateway? callGateway = null)
    {
        var method = $"Publish({format})";
        TestNotebookGuard.EnsureUnder(method, targetPath, publishRoot);
        return GuardedAsync(
            gateway,
            [sourceId],
            app =>
            {
                app.Publish(sourceId, targetPath, format, string.Empty);
                return true;
            },
            method,
            ct,
            throwawayNotebookIds,
            created: null,
            callGateway);
    }

    /// <summary>
    /// Spike 3.1's guarded <c>MergeSections</c>. The destination must be an object this run made, inside a test or
    /// throwaway notebook. The source must be one too, or else an ID in <paramref name="opened"/> (recorded only when
    /// <see cref="OpenBackupAsync"/> opened a file under <paramref name="publishRoot"/>) whose live <c>path</c> is still
    /// under <paramref name="publishRoot"/>. Neither may be a test notebook itself (<see cref="TestNotebookGuard.CheckMerge"/>).
    /// </summary>
    public static async Task MergeSectionsAsync(
        ComGateway gateway,
        string sourceId,
        string destinationId,
        IReadOnlySet<string> created,
        IReadOnlySet<string> opened,
        IReadOnlyDictionary<string, IReadOnlySet<string>> throwawayNotebookIds,
        string publishRoot,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        const string Method = "MergeSections";
        var notebooks = await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, ct);
        var fromOpened = TestNotebookGuard.CheckMerge(Method, sourceId, destinationId, TestNotebookGuard.TestNotebookIds(notebooks, ResolveFolder(Environment.GetEnvironmentVariable)), created, opened);
        if (fromOpened)
        {
            TestNotebookGuard.EnsureOpenedFrom(Method, await gateway.GetHierarchyAsync(sourceId, CoreScope.Self, ct), publishRoot);
        }

        await GuardedAsync(
            gateway,
            fromOpened ? [destinationId] : [sourceId, destinationId],
            app =>
            {
                app.MergeSections(sourceId, destinationId);
                return true;
            },
            Method,
            ct,
            throwawayNotebookIds,
            created: null);
    }

    /// <summary>Spike 3.1's guarded <c>MergeFiles</c>: all four paths must be under <paramref name="publishRoot"/>.</summary>
    public static Task MergeFilesAsync(ComGateway gateway, string baseFile, string clientFile, string serverFile, string targetFile, string publishRoot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        const string Method = "MergeFiles";
        TestNotebookGuard.CheckMergeFiles(Method, [baseFile, clientFile, serverFile, targetFile], publishRoot);
        return gateway.InvokeAsync(
            app =>
            {
                app.MergeFiles(baseFile, clientFile, serverFile, targetFile);
                return true;
            },
            ct,
            retryOnDisconnect: false,
            method: Method);
    }

    /// <summary>
    /// Spike 3.1 (Q3 a): adopts an earlier run's local throwaway notebook that is still open, by its exact name. Refuses
    /// (throws) unless exactly one open notebook has that <c>name</c> and its folder, directly in
    /// <see cref="ThrowawayRoot"/>, has that name too (<see cref="TestNotebookGuard.FindThrowawayByExactName"/>).
    /// Returns its ID, or null when no notebook of that name is open.
    /// </summary>
    public static async Task<string?> AdoptThrowawayNotebookAsync(ComGateway gateway, string exactName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        return TestNotebookGuard.FindThrowawayByExactName(await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, ct), ThrowawayRoot, exactName);
    }

    /// <summary>
    /// Story 5.1: opens a backup that <see cref="PublishAsync"/> wrote, with <c>OpenHierarchy</c> and
    /// <c>cftNone</c> (an existing path only, never a create). Refuses a path outside <paramref name="publishRoot"/>.
    /// </summary>
    public static Task<string> OpenBackupAsync(ComGateway gateway, string path, string publishRoot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        const string Method = "OpenHierarchy(backup)";
        TestNotebookGuard.EnsureUnder(Method, path, publishRoot);
        return gateway.InvokeAsync(
            app =>
            {
                app.OpenHierarchy(path, string.Empty, out var id, CreateFileType.cftNone);
                return id;
            },
            ct,
            retryOnDisconnect: false,
            method: Method);
    }

    /// <summary>
    /// Story 5.1: unpacks a <c>pfOneNotePackage</c> backup with <c>OpenPackage</c> into <paramref name="destination"/>,
    /// both under <paramref name="publishRoot"/>, and returns the path OneNote reports, which must be there too.
    /// </summary>
    public static async Task<string> OpenPackageAsync(ComGateway gateway, string package, string destination, string publishRoot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        const string Method = "OpenPackage(backup)";
        TestNotebookGuard.EnsureUnder(Method, package, publishRoot);
        TestNotebookGuard.EnsureUnder(Method, destination, publishRoot);
        var opened = await gateway.InvokeAsync(
            app =>
            {
                app.OpenPackage(package, destination, out var path);
                return path;
            },
            ct,
            retryOnDisconnect: false,
            method: Method);
        TestNotebookGuard.EnsureUnder(Method, opened, publishRoot);
        return opened;
    }

    /// <summary>
    /// Story 3.8's probe P2: <c>CloseNotebook</c> (not forced) of a notebook this run opened from <paramref name="publishRoot"/>.
    /// Refuses (throws, nothing sent) unless <paramref name="notebookId"/> is in <paramref name="openedThisRun"/>, is open now,
    /// and its live <c>path</c> lies under <paramref name="publishRoot"/>, so neither a test notebook nor a throwaway (which
    /// live under <see cref="ThrowawayRoot"/>, outside the publish folder) can ever be closed.
    /// </summary>
    public static async Task CloseOpenedCopyAsync(ComGateway gateway, string notebookId, IReadOnlySet<string> openedThisRun, string publishRoot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(openedThisRun);
        const string Method = "CloseNotebook(copy)";
        if (!openedThisRun.Contains(notebookId))
        {
            throw new InvalidOperationException($"Refusing {Method}: {notebookId} wasn't opened by this run.");
        }

        var notebooks = XDocument.Parse(await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, ct)).Root!;
        var live = notebooks.Elements(OneNoteXml.Namespace2013 + "Notebook").FirstOrDefault(n => (string?)n.Attribute("ID") == notebookId)
            ?? throw new InvalidOperationException($"Refusing {Method}: {notebookId} isn't open.");
        TestNotebookGuard.EnsureUnder(Method, (string?)live.Attribute("path") ?? string.Empty, publishRoot);
        await gateway.InvokeAsync(
            app =>
            {
                app.CloseNotebook(notebookId, false);
                return true;
            },
            ct,
            retryOnDisconnect: false,
            method: Method);
    }

    /// <summary>
    /// Story 5.1 (Q1 a): the one folder local throwaway notebooks are created and accepted in,
    /// <c>%LOCALAPPDATA%\OneNoteMcp-Test</c>. Fixed: it never follows <see cref="FolderOverrideVariable"/> (review finding 8).
    /// </summary>
    public static string ThrowawayRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OneNoteMcp-Test");

    /// <summary>
    /// Story 5.1: creates a throwaway local notebook, <c>MCP Test Throwaway …</c>, in <see cref="ThrowawayRoot"/> with
    /// <c>OpenHierarchy</c> cftNotebook, then reads it back by ID and refuses (throws) unless it is a throwaway with
    /// exactly that name at that path. With <paramref name="requireEmpty"/> the folder must be absent or empty (AD-7's
    /// create rule); without it, the folder may hold only <see cref="ProbeMarkerFile"/> (the non-empty-path probe).
    /// </summary>
    public static async Task<string> CreateThrowawayNotebookAsync(ComGateway gateway, string leafName, bool requireEmpty, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        TestNotebookGuard.EnsureThrowawayLeaf(leafName);
        var root = ThrowawayRoot;
        var path = Path.Combine(root, leafName);
        if (requireEmpty)
        {
            TestNotebookGuard.EnsureFolderIsOurs(path);
        }
        else
        {
            TestNotebookGuard.EnsureOnlyProbeMarker(path);
        }

        Directory.CreateDirectory(root);
        var id = await gateway.InvokeAsync(
            app =>
            {
                app.OpenHierarchy(path, string.Empty, out var created, CreateFileType.cftNotebook);
                return created;
            },
            ct,
            retryOnDisconnect: false,
            method: "OpenHierarchy(throwaway notebook)");

        var notebooks = await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, ct);
        if (TestNotebookGuard.ThrowawayNotebookIds(notebooks, root, new Dictionary<string, IReadOnlySet<string>> { [id] = new HashSet<string>([leafName]) }).Count != 1)
        {
            throw new InvalidOperationException($"OpenHierarchy returned {id}, which isn't a throwaway notebook named {leafName} at {path}.");
        }

        return id;
    }

    /// <summary>
    /// Story 5.1: <c>OpenHierarchy</c> cftNotebook on the path of a throwaway notebook that is already open (the
    /// existing-path probe). Refuses unless <paramref name="notebookId"/> is an open throwaway in
    /// <see cref="ThrowawayRoot"/> with one of <paramref name="names"/>.
    /// </summary>
    public static async Task<string> ReopenThrowawayNotebookAsync(ComGateway gateway, string notebookId, IReadOnlySet<string> names, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        var notebooks = await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, ct);
        if (TestNotebookGuard.ThrowawayNotebookIds(notebooks, ThrowawayRoot, new Dictionary<string, IReadOnlySet<string>> { [notebookId] = names }).Count != 1)
        {
            throw new InvalidOperationException($"Refusing OpenHierarchy: {notebookId} is not an open throwaway notebook.");
        }

        var path = TestNotebookGuard.NotebookPath(notebooks, notebookId)!;
        return await gateway.InvokeAsync(
            app =>
            {
                app.OpenHierarchy(path, string.Empty, out var id, CreateFileType.cftNotebook);
                return id;
            },
            ct,
            retryOnDisconnect: false,
            method: "OpenHierarchy(existing throwaway path)");
    }

    /// <summary>The one file a non-empty throwaway folder may hold before the non-empty-path probe.</summary>
    public const string ProbeMarkerFile = "probe-marker.txt";

    /// <summary>
    /// The shared check: every target must be inside MCP Test, MCP Test Cloud or a live-verified throwaway, or, when
    /// <paramref name="created"/> is given, be an object this run made that is now gone from every notebook.
    /// </summary>
    private static async Task<T> GuardedAsync<T>(
        ComGateway gateway,
        IReadOnlyList<string> targetIds,
        Func<IApplication, T> write,
        string method,
        CancellationToken ct,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? throwawayNotebookIds,
        IReadOnlySet<string>? created,
        ComGateway? callGateway = null)
    {
        await EnsureTargetsAsync(gateway, targetIds, method, ct, throwawayNotebookIds, created);
        return await (callGateway ?? gateway).InvokeAsync(write, ct, retryOnDisconnect: false, method: method);
    }

    /// <summary>
    /// The guard alone: throws unless a live <c>GetHierarchy</c> shows <paramref name="targetId"/> is MCP Test, MCP Test
    /// Cloud or inside one of them. A test that writes through a port method (story 2.7) calls it first.
    /// </summary>
    public static Task EnsureTestTargetAsync(ComGateway gateway, string targetId, string method, CancellationToken ct) =>
        EnsureTargetsAsync(gateway, [targetId], method, ct, throwawayNotebookIds: null, created: null);

    /// <summary>
    /// Story 3.7: as <see cref="EnsureTestTargetAsync(ComGateway, string, string, CancellationToken)"/>, and also accepts a
    /// target inside (or being) one of <paramref name="throwawayNotebookIds"/>, each re-checked as a throwaway by name and folder.
    /// </summary>
    public static Task EnsureTestTargetAsync(
        ComGateway gateway,
        string targetId,
        string method,
        IReadOnlyDictionary<string, IReadOnlySet<string>> throwawayNotebookIds,
        CancellationToken ct) =>
        EnsureTargetsAsync(gateway, [targetId], method, ct, throwawayNotebookIds, created: null);

    private static async Task EnsureTargetsAsync(
        ComGateway gateway,
        IReadOnlyList<string> targetIds,
        string method,
        CancellationToken ct,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? throwawayNotebookIds,
        IReadOnlySet<string>? created)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        var folder = ResolveFolder(Environment.GetEnvironmentVariable);

        var notebooks = await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, ct);
        var notebookIds = TestNotebookGuard.TestNotebookIds(notebooks, folder).ToList();
        if (throwawayNotebookIds is { Count: > 0 })
        {
            notebookIds.AddRange(TestNotebookGuard.ThrowawayNotebookIds(notebooks, ThrowawayRoot, throwawayNotebookIds));
        }

        if (notebookIds.Count == 0)
        {
            throw new InvalidOperationException($"Refusing {method}: neither MCP Test (at {folder}) nor MCP Test Cloud is open.");
        }

        var trees = new List<string>(notebookIds.Count);
        foreach (var notebookId in notebookIds)
        {
            trees.Add(await gateway.GetHierarchyAsync(notebookId, CoreScope.Pages, ct));
        }

        string? everything = null;
        foreach (var targetId in targetIds)
        {
            if (trees.Any(tree => TestNotebookGuard.Contains(tree, targetId)))
            {
                continue;
            }

            if (created is not null && created.Contains(targetId))
            {
                everything ??= await gateway.GetHierarchyAsync(string.Empty, CoreScope.Pages, ct);
                if (!TestNotebookGuard.AnyContains(everything, targetId))
                {
                    continue; // made by this run and now gone everywhere: a stale-ID probe
                }
            }

            throw new InvalidOperationException($"Refusing {method}: {targetId} is not inside MCP Test, MCP Test Cloud or a throwaway notebook.");
        }
    }

    /// <summary>MCP Test Cloud's ID from a live read, or null when it isn't open. Never creates it.</summary>
    public static async Task<string?> FindCloudAsync(ComGateway gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        var notebooks = await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, ct);
        return TestNotebookGuard.FindCloudNotebookId(notebooks);
    }
}

/// <summary>The guard's pure logic, unit-tested against XML strings in <c>TestNotebookGuardTests</c>.</summary>
internal static partial class TestNotebookGuard
{
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    public static string TrimPath(string path) => path.TrimEnd('\\', '/');

    /// <summary>
    /// The ID of the open notebook whose <c>path</c> is <paramref name="folder"/> (trailing separators ignored,
    /// case-insensitive), or null when none is open there. Throws when the notebook at that path isn't named
    /// <c>MCP Test</c>, so a folder override pointing at a real notebook is refused, never written to.
    /// </summary>
    public static string? FindNotebookId(string notebooksXml, string folder)
    {
        var wanted = TrimPath(folder);
        var matches = XDocument.Parse(notebooksXml).Root!
            .Elements(One + "Notebook")
            .Where(n => string.Equals(TrimPath((string?)n.Attribute("path") ?? string.Empty), wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count > 1 || (string?)matches[0].Attribute("name") != TestNotebook.NotebookName)
        {
            throw new InvalidOperationException($"Refusing: the notebook open at {folder} is not the one MCP Test notebook.");
        }

        return (string?)matches[0].Attribute("ID");
    }

    /// <summary>The OneDrive host every MCP Test Cloud path must have.</summary>
    public const string CloudHost = "d.docs.live.net";

    /// <summary>
    /// The ID of MCP Test Cloud, or null when it isn't open. A notebook counts only when its <c>name</c> is exactly
    /// <c>MCP Test Cloud</c> and its <c>path</c> is an <c>https</c> URL on <see cref="CloudHost"/> whose last folder is
    /// <c>/MCP Test Cloud/</c>, compared ignoring case (OneNote spells the account ID in different cases on the notebook
    /// and its sections). Throws when that path belongs to a notebook with another name, or to more than one notebook.
    /// Lookup only: nothing creates or opens it.
    /// </summary>
    public static string? FindCloudNotebookId(string notebooksXml)
    {
        var matches = XDocument.Parse(notebooksXml).Root!
            .Elements(One + "Notebook")
            .Where(n => IsCloudTestPath((string?)n.Attribute("path")))
            .ToList();
        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count > 1 || (string?)matches[0].Attribute("name") != TestNotebook.CloudNotebookName)
        {
            throw new InvalidOperationException("Refusing: the notebook open at the MCP Test Cloud path is not the one MCP Test Cloud notebook.");
        }

        return (string?)matches[0].Attribute("ID");
    }

    /// <summary>True for an <c>https://d.docs.live.net/…/MCP Test Cloud/</c> path (case-insensitive, trailing slash optional).</summary>
    public static bool IsCloudTestPath(string? path) => IsCloudPathNamed(path, TestNotebook.CloudNotebookName);

    /// <summary>True for an <c>https://d.docs.live.net/&lt;account&gt;/…/&lt;leaf&gt;/</c> path (case-insensitive, trailing slash optional).</summary>
    public static bool IsCloudPathNamed(string? path, string leaf)
    {
        if (string.IsNullOrEmpty(path)
            || !Uri.TryCreate(path, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, CloudHost, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        var segments = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/').Split('/');
        return segments.Length >= 3 // "", the account ID, …, the notebook folder
            && string.Equals(segments[^1], leaf, StringComparison.OrdinalIgnoreCase);
    }

    // --- Story 5.1: throwaway notebooks, deletes and hierarchy XML ------------------------------------------------

    /// <summary>
    /// A local throwaway: its <c>name</c> starts with <see cref="TestNotebook.ThrowawayNamePrefix"/>, and its <c>path</c>
    /// is a folder directly in <paramref name="root"/> whose own name starts with the prefix too (ignoring case).
    /// </summary>
    public static bool IsLocalThrowaway(string? name, string? path, string root)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)
            || !name.StartsWith(TestNotebook.ThrowawayNamePrefix, StringComparison.Ordinal)
            || name == TestNotebook.CloudThrowawayName
            || !Path.IsPathFullyQualified(path)
            || path.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var trimmed = TrimPath(path);
        return string.Equals(Path.GetDirectoryName(trimmed), TrimPath(root), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(trimmed).StartsWith(TestNotebook.ThrowawayNamePrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The cloud throwaway: <c>name</c> exactly <see cref="TestNotebook.CloudThrowawayName"/> at a OneDrive path ending in that folder.</summary>
    public static bool IsCloudThrowaway(string? name, string? path) =>
        name == TestNotebook.CloudThrowawayName && IsCloudPathNamed(path, TestNotebook.CloudThrowawayName);

    /// <summary>
    /// Those of <paramref name="candidates"/> (ID → the exact names this run gave it) that are open throwaway notebooks
    /// (Q1 a, review finding 8). A local one counts only when its <c>name</c> is one of its names and its folder, directly
    /// in <paramref name="root"/>, is named one of them too (ignoring case); the cloud one only by
    /// <see cref="IsCloudThrowaway"/> with that exact name. A candidate that is open but fails this is refused (throws);
    /// one that isn't open is left out.
    /// </summary>
    public static IReadOnlyList<string> ThrowawayNotebookIds(string notebooksXml, string root, IReadOnlyDictionary<string, IReadOnlySet<string>> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var ids = new List<string>();
        foreach (var notebook in XDocument.Parse(notebooksXml).Root!.Elements(One + "Notebook"))
        {
            var id = (string?)notebook.Attribute("ID");
            if (id is null || !candidates.TryGetValue(id, out var names))
            {
                continue;
            }

            var name = (string?)notebook.Attribute("name");
            var path = (string?)notebook.Attribute("path");
            var leaf = path is null ? null : Path.GetFileName(TrimPath(path));
            var local = IsLocalThrowaway(name, path, root)
                && names.Contains(name!)
                && names.Any(n => string.Equals(n, leaf, StringComparison.OrdinalIgnoreCase));
            var cloud = IsCloudThrowaway(name, path) && names.Contains(name!);
            var tenant = IsTenantTest(name, path) && names.Contains(name!); // spike 3.1 (Q5): adopted like a throwaway
            if (!local && !cloud && !tenant)
            {
                throw new InvalidOperationException($"Refusing: notebook {id} is not a throwaway notebook with a name this run gave it.");
            }

            ids.Add(id);
        }

        return ids;
    }

    // --- Spike 3.1: the tenant notebook, adoption by exact name, merges and recycle bins ------------------------------

    /// <summary>
    /// The tenant notebook of the label probe (Q5): <c>name</c> exactly <see cref="TestNotebook.TenantNotebookName"/>, and
    /// an absolute <c>https</c> path with no user info whose host isn't <see cref="CloudHost"/> (OneDrive personal). Its
    /// tenant's other notebooks never match, since the name is exact.
    /// </summary>
    public static bool IsTenantTest(string? name, string? path) =>
        name == TestNotebook.TenantNotebookName
        && !string.IsNullOrEmpty(path)
        && Uri.TryCreate(path, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && !string.IsNullOrEmpty(uri.Host)
        && !string.Equals(uri.Host, CloudHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Q3 a: the ID of the open local throwaway whose <c>name</c> is exactly <paramref name="exactName"/> and whose
    /// folder, directly in <paramref name="root"/>, is named <paramref name="exactName"/> too (ignoring case, as Windows
    /// paths do). Null when no notebook has that name. Throws when the name is not a throwaway name, when the notebook
    /// with that name sits anywhere else, or when more than one notebook has it.
    /// </summary>
    public static string? FindThrowawayByExactName(string notebooksXml, string root, string exactName)
    {
        EnsureThrowawayLeaf(exactName);
        var named = XDocument.Parse(notebooksXml).Root!.Elements(One + "Notebook")
            .Where(n => (string?)n.Attribute("name") == exactName)
            .ToList();
        if (named.Count == 0)
        {
            return null;
        }

        var path = (string?)named[0].Attribute("path");
        if (named.Count > 1
            || !IsLocalThrowaway(exactName, path, root)
            || !string.Equals(Path.GetFileName(TrimPath(path!)), exactName, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty((string?)named[0].Attribute("ID")))
        {
            throw new InvalidOperationException($"Refusing to adopt \"{exactName}\": it is not one local throwaway notebook in its own folder of that name.");
        }

        return (string?)named[0].Attribute("ID");
    }

    /// <summary>
    /// Refuses a <c>MergeSections</c> unless the destination is in <paramref name="created"/>, and the source is in
    /// <paramref name="created"/> or in <paramref name="opened"/>; neither may be empty, the same, or a test notebook.
    /// Returns true when the source is allowed only as an opened backup (its path must then be checked live).
    /// </summary>
    public static bool CheckMerge(string method, string sourceId, string destinationId, IReadOnlyCollection<string> testNotebookIds, IReadOnlySet<string> created, IReadOnlySet<string> opened)
    {
        ArgumentNullException.ThrowIfNull(testNotebookIds);
        ArgumentNullException.ThrowIfNull(created);
        ArgumentNullException.ThrowIfNull(opened);
        if (string.IsNullOrEmpty(sourceId) || string.IsNullOrEmpty(destinationId) || sourceId == destinationId)
        {
            throw new InvalidOperationException($"Refusing {method}: the source and destination must be two different IDs.");
        }

        if (testNotebookIds.Contains(sourceId) || testNotebookIds.Contains(destinationId))
        {
            throw new InvalidOperationException($"Refusing {method}: it names the MCP Test or MCP Test Cloud notebook itself.");
        }

        if (!created.Contains(destinationId))
        {
            throw new InvalidOperationException($"Refusing {method}: destination {destinationId} was not created by this run.");
        }

        if (created.Contains(sourceId))
        {
            return false;
        }

        if (opened.Contains(sourceId))
        {
            return true;
        }

        throw new InvalidOperationException($"Refusing {method}: source {sourceId} was neither created nor opened from the publish folder by this run.");
    }

    /// <summary>A <c>MergeFiles</c> call's paths: exactly four (base, client, server, target), each strictly under <paramref name="publishRoot"/>.</summary>
    public static void CheckMergeFiles(string method, IReadOnlyList<string> paths, string publishRoot)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count != 4)
        {
            throw new InvalidOperationException($"Refusing {method}: it takes four paths.");
        }

        foreach (var path in paths)
        {
            EnsureUnder(method, path, publishRoot);
        }
    }

    /// <summary>Refuses unless the root of <paramref name="selfXml"/> (a self-scope read) has a <c>path</c> under <paramref name="publishRoot"/>.</summary>
    public static void EnsureOpenedFrom(string method, string selfXml, string publishRoot)
    {
        var path = (string?)XDocument.Parse(selfXml).Root?.Attribute("path");
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException($"Refusing {method}: the opened source has no path.");
        }

        EnsureUnder(method, path, publishRoot);
    }

    /// <summary>
    /// The IDs of a notebook's own recycle bin: its <c>isRecycleBin</c> section group and the <c>isDeletedPages</c>
    /// sections inside it. Nothing else (not the recycled sections themselves).
    /// </summary>
    public static IReadOnlySet<string> RecycleBinIds(string notebookTreeXml)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var root = XDocument.Parse(notebookTreeXml).Root!;
        if (root.Name != One + "Notebook")
        {
            return ids;
        }

        foreach (var bin in root.Elements(One + "SectionGroup").Where(g => (string?)g.Attribute("isRecycleBin") == "true"))
        {
            if ((string?)bin.Attribute("ID") is { Length: > 0 } binId)
            {
                ids.Add(binId);
            }

            foreach (var deleted in bin.Elements(One + "Section").Where(s => (string?)s.Attribute("isDeletedPages") == "true"))
            {
                if ((string?)deleted.Attribute("ID") is { Length: > 0 } id)
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    /// <summary>The <c>path</c> of the open notebook <paramref name="notebookId"/>, or null.</summary>
    public static string? NotebookPath(string notebooksXml, string notebookId) =>
        XDocument.Parse(notebooksXml).Root!.Elements(One + "Notebook")
            .Where(n => (string?)n.Attribute("ID") == notebookId)
            .Select(n => (string?)n.Attribute("path"))
            .FirstOrDefault();

    /// <summary>A throwaway folder name: starts with the prefix, one path segment, no relative parts.</summary>
    public static void EnsureThrowawayLeaf(string leafName)
    {
        if (string.IsNullOrWhiteSpace(leafName)
            || !leafName.StartsWith(TestNotebook.ThrowawayNamePrefix, StringComparison.Ordinal)
            || leafName == TestNotebook.CloudThrowawayName
            || leafName.Contains("..", StringComparison.Ordinal)
            || leafName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"Refusing: \"{leafName}\" is not a throwaway notebook folder name.");
        }
    }

    /// <summary>Before the non-empty-path probe: the folder must exist and hold only <see cref="TestNotebook.ProbeMarkerFile"/>.</summary>
    public static void EnsureOnlyProbeMarker(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
        {
            throw new InvalidOperationException($"Refusing: {folder} doesn't exist.");
        }

        var entries = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).ToList();
        if (entries.Count != 1 || entries[0] != TestNotebook.ProbeMarkerFile)
        {
            throw new InvalidOperationException($"Refusing: {folder} holds more than the probe marker.");
        }
    }

    /// <summary>
    /// Refuses a <c>DeleteHierarchy</c> target that is empty, is the MCP Test or MCP Test Cloud notebook itself, or isn't
    /// in <paramref name="created"/> (an object this run made). Fixtures are never in that set.
    /// </summary>
    public static void CheckDelete(string method, string targetId, IReadOnlyCollection<string> testNotebookIds, IReadOnlySet<string> created)
    {
        ArgumentNullException.ThrowIfNull(testNotebookIds);
        ArgumentNullException.ThrowIfNull(created);
        if (string.IsNullOrEmpty(targetId))
        {
            throw new InvalidOperationException($"Refusing {method}: no target.");
        }

        if (testNotebookIds.Contains(targetId))
        {
            throw new InvalidOperationException($"Refusing {method}: {targetId} is the MCP Test or MCP Test Cloud notebook itself.");
        }

        if (!created.Contains(targetId))
        {
            throw new InvalidOperationException($"Refusing {method}: {targetId} was not created by this run.");
        }
    }

    /// <summary>
    /// Refuses <c>UpdateHierarchy</c> XML unless it names at least one <c>ID</c> and every <c>ID</c> in it (found by
    /// pattern, so malformed XML is checked too) is in <paramref name="created"/> and is neither test notebook. A
    /// well-formed document's root must carry an <c>ID</c>. Returns the IDs in document order. Spike 3.1: an ID in
    /// <paramref name="recycleBins"/> (a test notebook's recycle-bin group or Deleted Pages, read live) passes too.
    /// </summary>
    public static IReadOnlyList<string> CheckHierarchyXml(string method, string xml, IReadOnlyCollection<string> testNotebookIds, IReadOnlySet<string> created, IReadOnlySet<string>? recycleBins = null)
    {
        ArgumentNullException.ThrowIfNull(xml);
        ArgumentNullException.ThrowIfNull(testNotebookIds);
        ArgumentNullException.ThrowIfNull(created);
        var ids = IdAttribute().Matches(xml).Select(m => m.Groups["id"].Value).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
        {
            throw new InvalidOperationException($"Refusing {method}: the XML names no ID.");
        }

        foreach (var id in ids)
        {
            if (testNotebookIds.Contains(id))
            {
                throw new InvalidOperationException($"Refusing {method}: the XML names the MCP Test or MCP Test Cloud notebook ({id}).");
            }

            if (!created.Contains(id) && recycleBins?.Contains(id) != true)
            {
                throw new InvalidOperationException($"Refusing {method}: {id} was not created by this run.");
            }
        }

        XElement? root = null;
        try
        {
            root = XDocument.Parse(xml).Root;
        }
        catch (System.Xml.XmlException)
        {
            // Malformed on purpose (an HRESULT probe): the pattern check above is the guard.
        }

        if (root is not null && string.IsNullOrEmpty((string?)root.Attribute("ID")))
        {
            throw new InvalidOperationException($"Refusing {method}: the XML root has no ID.");
        }

        return ids;
    }

    /// <summary>True when any element of <paramref name="hierarchyXml"/> has <c>ID</c> <paramref name="id"/> (exact).</summary>
    public static bool AnyContains(string hierarchyXml, string id) =>
        !string.IsNullOrEmpty(id) && XDocument.Parse(hierarchyXml).Root!.DescendantsAndSelf().Any(e => (string?)e.Attribute("ID") == id);

    /// <summary>Refuses a file path that, made absolute, isn't strictly inside <paramref name="root"/>.</summary>
    public static void EnsureUnder(string method, string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
        {
            throw new InvalidOperationException($"Refusing {method}: {path} is not an absolute path.");
        }

        var full = Path.GetFullPath(path);
        var prefix = TrimPath(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing {method}: {path} is outside {root}.");
        }
    }

    [GeneratedRegex("""\bID\s*=\s*["'](?<id>[^"']*)["']""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdAttribute();

    /// <summary>The IDs of the open test notebooks: MCP Test at <paramref name="folder"/>, then MCP Test Cloud. Either may be absent.</summary>
    public static IReadOnlyList<string> TestNotebookIds(string notebooksXml, string folder)
    {
        var ids = new List<string>(2);
        if (FindNotebookId(notebooksXml, folder) is { Length: > 0 } local)
        {
            ids.Add(local);
        }

        if (FindCloudNotebookId(notebooksXml) is { Length: > 0 } cloud)
        {
            ids.Add(cloud);
        }

        return ids;
    }

    /// <summary>The ID of the non-recycle-bin section named <paramref name="name"/> directly in the notebook.</summary>
    public static string? FindSectionId(string notebookTreeXml, string name) =>
        XDocument.Parse(notebookTreeXml).Root!
            .Elements(One + "Section")
            .Where(s => (string?)s.Attribute("name") == name && (string?)s.Attribute("isInRecycleBin") != "true")
            .Select(s => (string?)s.Attribute("ID"))
            .FirstOrDefault();

    public static string? FirstPageId(string notebookTreeXml, string sectionId) =>
        XDocument.Parse(notebookTreeXml).Root!
            .Descendants(One + "Section")
            .Where(s => (string?)s.Attribute("ID") == sectionId)
            .Elements(One + "Page")
            .Where(p => (string?)p.Attribute("isInRecycleBin") != "true")
            .Select(p => (string?)p.Attribute("ID"))
            .FirstOrDefault();

    /// <summary>True when <paramref name="targetId"/> is the tree's root notebook or any object inside it. IDs compare exactly.</summary>
    public static bool Contains(string notebookTreeXml, string targetId)
    {
        if (string.IsNullOrEmpty(targetId))
        {
            return false;
        }

        var root = XDocument.Parse(notebookTreeXml).Root!;
        return root.Name == One + "Notebook"
            && root.DescendantsAndSelf().Any(e => (string?)e.Attribute("ID") == targetId);
    }

    /// <summary>Before creating MCP Test: the folder must be absent or empty, so creation never opens or adopts existing files.</summary>
    public static void EnsureFolderIsOurs(string folder)
    {
        if (!Path.IsPathFullyQualified(folder))
        {
            throw new InvalidOperationException($"Refusing to create MCP Test: {folder} is not an absolute path.");
        }

        if (!Directory.Exists(folder))
        {
            return;
        }

        if (Directory.EnumerateFileSystemEntries(folder).Any())
        {
            throw new InvalidOperationException($"Refusing to create MCP Test: {folder} is not empty. Open it in OneNote, or empty it.");
        }
    }
}
