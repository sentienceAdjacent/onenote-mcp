using System.Xml.Linq;
using OneNoteMcp.Com;
using OneNoteMcp.Com.Interop;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Xml;
using Xunit;
using CoreScope = OneNoteMcp.Core.HierarchyScope;
using PageInfo = OneNoteMcp.Core.PageInfo;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 17's synthetic size fixture, hitl and opt-in: <c>ONENOTE_MCP_INTEGRATION=1</c> plus
/// <c>ONENOTE_MCP_SCENARIO=ballast</c>, scaled by <c>ONENOTE_MCP_BALLAST_SCALE</c>. It builds, in MCP Test only and only
/// through <see cref="TestNotebook.WriteAsync{T}"/>:
/// <list type="bullet">
/// <item>section <c>Ballast</c>: an oversized page (≥ 1,000,000 chars of page XML), a page of noise PNGs and a page of
/// random attachments;</item>
/// <item>group tree <c>Ballast Tree</c>: 5 nested levels of 4 sections of 25 pages, every fifth page with a Meta;</item>
/// <item>section <c>Ballast Vault</c>, for the owner to password-protect by hand. Once it's encrypted it's never written
/// or read again.</item>
/// </list>
/// Idempotent: a page counts only once its <c>ballast-complete</c> Meta (written last) is on it. A page in a Ballast
/// section without that Meta (an interrupted build), or one built at another scale, is recycled and rebuilt. Complete
/// pages the current plan doesn't name (a duplicate title, or tree pages left from a larger scale) are left in place.
/// A rerun of a complete fixture makes no writes. It prints IDs and counts only.
/// </summary>
public sealed class BallastFixture : IDisposable
{
    public const string ScenarioName = "ballast";

    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string settingsDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-ballast", Guid.NewGuid().ToString("N"));
    private readonly string attachmentsDir;
    private readonly ITestOutputHelper output;

    private ComGateway gateway = null!;
    private string notebookId = string.Empty;
    private int scale;
    private int writes;
    private int pagesCreated;
    private int pagesRecycled;
    private int pagesChecked;

    public BallastFixture(ITestOutputHelper output)
    {
        this.output = output;
        attachmentsDir = Path.Combine(settingsDir, "attachments");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ballast()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("ONENOTE_MCP_SCENARIO") == ScenarioName,
            $"Hitl scenario: set ONENOTE_MCP_SCENARIO={ScenarioName}.");
        scale = BallastContent.ParseScale(Environment.GetEnvironmentVariable(BallastContent.ScaleVariable));

        using var gateway = Gateway();
        this.gateway = gateway;
        var notebook = await TestNotebook.EnsureAsync(gateway, Ct);
        notebookId = notebook.NotebookId;

        // Section Ballast: oversized, images, files.
        var sectionId = await EnsureSectionAsync(notebookId, BallastContent.SectionName);
        var owned = await CompletePagesAsync(sectionId);
        var oversizedId = await EnsurePageAsync(sectionId, owned, BallastContent.OversizedTitle, BallastContent.CompleteContent(scale), () => BallastContent.OversizedUpdates(scale));
        var imagesId = await EnsurePageAsync(sectionId, owned, BallastContent.ImagesTitle, BallastContent.CompleteContent(scale), ImagesUpdates);
        var filesId = await EnsurePageAsync(sectionId, owned, BallastContent.FilesTitle, BallastContent.CompleteContent(scale), FilesUpdates);

        // Group tree.
        var treePages = 0;
        foreach (var plan in BallastContent.TreePlan(scale))
        {
            var parentId = notebookId;
            foreach (var group in plan.GroupPath)
            {
                parentId = await EnsureGroupAsync(parentId, group);
            }

            var treeSectionId = await EnsureSectionAsync(parentId, plan.Name);
            var complete = await CompletePagesAsync(treeSectionId);
            foreach (var page in plan.Pages)
            {
                await EnsurePageAsync(treeSectionId, complete, page.Title, BallastContent.CompleteContentUnscaled, () => [BallastContent.TreePageBody(page)]);
                treePages++;
            }
        }

        // Vault: built while unprotected; once encrypted, untouched (no write, no content read).
        var tree = await NotebookTreeAsync();
        var vault = ChildSection(tree.Root!, BallastContent.VaultSectionName);
        var vaultProtected = vault is not null && (IsTrue(vault, "encrypted") || IsTrue(vault, "locked"));
        string vaultId;
        if (vaultProtected)
        {
            vaultId = (string)vault!.Attribute("ID")!;
        }
        else
        {
            vaultId = await EnsureSectionAsync(notebookId, BallastContent.VaultSectionName);
            var complete = await CompletePagesAsync(vaultId);
            for (var i = 0; i < BallastContent.VaultPages; i++)
            {
                var index = i;
                await EnsurePageAsync(vaultId, complete, BallastContent.VaultPageTitle(i), BallastContent.CompleteContentUnscaled, () => [BallastContent.VaultPageBody(index)]);
            }
        }

        // Sizes as OneNote returns them.
        var pagesScope = await this.gateway.GetHierarchyAsync(notebookId, CoreScope.Pages, Ct);
        var oversizedChars = (await this.gateway.GetPageContentAsync(oversizedId, PageInfo.Basic, Ct)).Length;
        var finalTree = XDocument.Parse(pagesScope).Root!;
        var finalVault = ChildSection(finalTree, BallastContent.VaultSectionName)!;

        output.WriteLine($"Ballast (scale {scale}): writes {writes}; pages created {pagesCreated}, recycled {pagesRecycled}, checked {pagesChecked}.");
        output.WriteLine($"Section {sectionId}: oversized {oversizedId} ({oversizedChars} chars piBasic), images {imagesId}, files {filesId}.");
        output.WriteLine($"Tree: {BallastContent.TreePlan(scale).Count} sections, {treePages} pages. Notebook pages-scope listing: {pagesScope.Length} chars.");
        output.WriteLine($"Vault {vaultId}: encrypted={IsTrue(finalVault, "encrypted")}, locked={IsTrue(finalVault, "locked")}{(vaultProtected ? " (protected: not written or read)" : string.Empty)}.");
        if (!IsTrue(finalVault, "encrypted"))
        {
            output.WriteLine(
                $"Owner (hitl): in OneNote, password-protect section {vaultId} (Ballast Vault in MCP Test): right-click its tab > "
                + "Password Protect This Section, set any password, then lock it (Lock All). COM can't set a password. "
                + "Rerun this scenario to confirm it reports the Vault protected, then run the stress harness on MCP Test.");
        }

        Assert.True(oversizedChars >= BallastContent.OversizedTargetChars, $"The oversized page is {oversizedChars} chars.");
        Assert.True(pagesScope.Length >= BallastContent.PagesScopeTargetChars, $"The pages-scope listing is {pagesScope.Length} chars.");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(settingsDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    // --- Pages ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The complete pages in a Ballast section, by title, with the completion Meta's content. Every other page in the
    /// section (no completion Meta: an interrupted build) is recycled, through the guard. Reads each page's piBasic.
    /// </summary>
    private async Task<Dictionary<string, (string Id, string Content)>> CompletePagesAsync(string sectionId)
    {
        var complete = new Dictionary<string, (string Id, string Content)>(StringComparer.Ordinal);
        var section = XDocument.Parse(await gateway.GetHierarchyAsync(sectionId, CoreScope.Pages, Ct)).Root!;
        foreach (var page in section.Elements(One + "Page").Where(p => !IsTrue(p, "isInRecycleBin")))
        {
            var pageId = (string)page.Attribute("ID")!;
            var title = (string?)page.Attribute("name") ?? string.Empty;
            pagesChecked++;
            var content = CompletionContent(await gateway.GetPageContentAsync(pageId, PageInfo.Basic, Ct));
            if (content is not null && !complete.ContainsKey(title))
            {
                complete[title] = (pageId, content);
                continue;
            }

            if (content is null)
            {
                await RecycleAsync(pageId);
            }
        }

        return complete;
    }

    /// <summary>
    /// The page titled <paramref name="title"/> when it's complete at <paramref name="expectedContent"/>; else recycles
    /// any stale one and builds it: <c>CreateNewPage</c>, then each update in order (the completion Meta is the last).
    /// </summary>
    private async Task<string> EnsurePageAsync(
        string sectionId,
        Dictionary<string, (string Id, string Content)> complete,
        string title,
        string expectedContent,
        Func<IEnumerable<string>> updates)
    {
        if (complete.TryGetValue(title, out var existing))
        {
            if (existing.Content == expectedContent)
            {
                return existing.Id;
            }

            await RecycleAsync(existing.Id); // built at another scale
            complete.Remove(title);
        }

        var pageId = await WriteAsync(
            sectionId,
            app =>
            {
                app.CreateNewPage(sectionId, out var id, NewPageStyle.npsBlankPageWithTitle);
                return id;
            },
            "CreateNewPage(Ballast)");
        pagesCreated++;

        foreach (var body in updates())
        {
            var xml = BallastContent.PageXml(pageId, body);
            await WriteAsync(
                pageId,
                app =>
                {
                    app.UpdatePageContent(xml, DateTime.MinValue, XMLSchema.xs2013, force: false);
                    return true;
                },
                "UpdatePageContent(Ballast)");
        }

        complete[title] = (pageId, expectedContent);
        return pageId;
    }

    /// <summary>Title, then one noise PNG per update, then the completion Meta.</summary>
    private IEnumerable<string> ImagesUpdates()
    {
        yield return BallastContent.TitleBody(BallastContent.ImagesTitle);
        for (var i = 0; i < BallastContent.ImagesPerScale * scale; i++)
        {
            yield return BallastContent.ImageOutline(BallastContent.NoisePng(i), i);
        }

        yield return BallastContent.Meta(BallastContent.CompleteMeta, BallastContent.CompleteContent(scale));
    }

    /// <summary>Title, then one random attachment per update from the temp folder (deleted in Dispose), then the completion Meta.</summary>
    private IEnumerable<string> FilesUpdates()
    {
        yield return BallastContent.TitleBody(BallastContent.FilesTitle);
        Directory.CreateDirectory(attachmentsDir);
        for (var i = 0; i < BallastContent.AttachmentsPerScale * scale; i++)
        {
            var name = BallastContent.AttachmentName(i);
            var path = Path.Combine(attachmentsDir, name);
            File.WriteAllBytes(path, BallastContent.Attachment(i));
            yield return BallastContent.InsertedFileOutline(path, name, i);
        }

        yield return BallastContent.Meta(BallastContent.CompleteMeta, BallastContent.CompleteContent(scale));
    }

    private Task RecycleAsync(string pageId)
    {
        pagesRecycled++;
        return WriteAsync(
            pageId,
            app =>
            {
                app.DeleteHierarchy(pageId, DateTime.MinValue, deletePermanently: false);
                return true;
            },
            "DeleteHierarchy(Ballast page, to the recycle bin)");
    }

    // --- Sections and groups --------------------------------------------------------------------------------------

    private async Task<string> EnsureSectionAsync(string parentId, string name)
    {
        var parent = await ParentElementAsync(parentId);
        if (ChildSection(parent, name) is { } existing)
        {
            return (string)existing.Attribute("ID")!;
        }

        return await WriteAsync(
            parentId,
            app =>
            {
                app.OpenHierarchy(name + ".one", parentId, out var id, CreateFileType.cftSection);
                return id;
            },
            "OpenHierarchy(Ballast section)");
    }

    private async Task<string> EnsureGroupAsync(string parentId, string name)
    {
        var parent = await ParentElementAsync(parentId);
        var existing = parent.Elements(One + "SectionGroup")
            .FirstOrDefault(g => (string?)g.Attribute("name") == name && !IsTrue(g, "isRecycleBin") && !IsTrue(g, "isInRecycleBin"));
        if (existing is not null)
        {
            return (string)existing.Attribute("ID")!;
        }

        return await WriteAsync(
            parentId,
            app =>
            {
                app.OpenHierarchy(name, parentId, out var id, CreateFileType.cftFolder);
                return id;
            },
            "OpenHierarchy(Ballast section group)");
    }

    /// <summary>The notebook or section group element with <paramref name="parentId"/>, from a sections-scope read of MCP Test.</summary>
    private async Task<XElement> ParentElementAsync(string parentId)
    {
        var tree = XDocument.Parse(await gateway.GetHierarchyAsync(notebookId, CoreScope.Sections, Ct)).Root!;
        return tree.DescendantsAndSelf().FirstOrDefault(e => (string?)e.Attribute("ID") == parentId)
            ?? throw new InvalidOperationException($"{parentId} is not in MCP Test.");
    }

    private async Task<XDocument> NotebookTreeAsync() =>
        XDocument.Parse(await gateway.GetHierarchyAsync(notebookId, CoreScope.Sections, Ct));

    private static XElement? ChildSection(XElement parent, string name) =>
        parent.Elements(One + "Section").FirstOrDefault(s => (string?)s.Attribute("name") == name && !IsTrue(s, "isInRecycleBin"));

    // --- Shared ---------------------------------------------------------------------------------------------------

    /// <summary>The <c>ballast-complete</c> page Meta's content, or null when the page has none.</summary>
    internal static string? CompletionContent(string pageXml) =>
        XDocument.Parse(pageXml).Root!.Elements(One + "Meta")
            .Where(m => (string?)m.Attribute("name") == BallastContent.CompleteMeta)
            .Select(m => (string?)m.Attribute("content") ?? string.Empty)
            .FirstOrDefault();

    private async Task<T> WriteAsync<T>(string targetId, Func<IApplication, T> write, string method)
    {
        writes++;
        return await TestNotebook.WriteAsync(gateway, targetId, write, method, Ct);
    }

    private static bool IsTrue(XElement e, string attribute) => (string?)e.Attribute(attribute) is "true" or "1";

    private ComGateway Gateway()
    {
        Directory.CreateDirectory(settingsDir);
        var path = Path.Combine(settingsDir, "settings.json");
        File.WriteAllText(path, """{ "schemaVersion": 1 }""");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        return new ComGateway(new ProcessProbe(), new SettingsStore(path));
    }
}
