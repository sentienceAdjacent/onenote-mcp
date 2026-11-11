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
    /// Test Cloud (story 2.2; found, never created) or inside one of them. Runs through the gateway's raw seam with no
    /// automatic retry (AD-3).
    /// </summary>
    public static async Task<T> WriteAsync<T>(ComGateway gateway, string targetId, Func<IApplication, T> write, string method, CancellationToken ct)
    {
        await EnsureTestTargetAsync(gateway, targetId, method, ct);
        return await gateway.InvokeAsync(write, ct, retryOnDisconnect: false, method: method);
    }

    /// <summary>
    /// The guard alone: throws unless a live <c>GetHierarchy</c> shows <paramref name="targetId"/> is MCP Test, MCP Test
    /// Cloud or inside one of them. A test that writes through a port method (story 2.7) calls it first.
    /// </summary>
    public static async Task EnsureTestTargetAsync(ComGateway gateway, string targetId, string method, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        var folder = ResolveFolder(Environment.GetEnvironmentVariable);

        var notebooks = await gateway.GetHierarchyAsync(string.Empty, CoreScope.Notebooks, ct);
        var notebookIds = TestNotebookGuard.TestNotebookIds(notebooks, folder);
        if (notebookIds.Count == 0)
        {
            throw new InvalidOperationException($"Refusing {method}: neither MCP Test (at {folder}) nor MCP Test Cloud is open.");
        }

        var inside = false;
        foreach (var notebookId in notebookIds)
        {
            var tree = await gateway.GetHierarchyAsync(notebookId, CoreScope.Pages, ct);
            if (TestNotebookGuard.Contains(tree, targetId))
            {
                inside = true;
                break;
            }
        }

        if (!inside)
        {
            throw new InvalidOperationException($"Refusing {method}: {targetId} is not inside MCP Test or MCP Test Cloud.");
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
internal static class TestNotebookGuard
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
    public static bool IsCloudTestPath(string? path)
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
            && string.Equals(segments[^1], TestNotebook.CloudNotebookName, StringComparison.OrdinalIgnoreCase);
    }

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
