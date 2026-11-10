using System.Text.Json;
using System.Xml.Linq;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Com;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 1.20 (AD-21) against a real OneNote, on MCP Test's <c>Ballast Vault</c> only, which the owner password-protects
/// by hand (story 17). Opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c>; skipped when the Vault is absent or not encrypted.
/// Every call is a read. Grants go to a temporary <c>settings.json</c>, never the user's. Run it with the Vault locked,
/// and again unlocked: both pass. It prints IDs and booleans only, never content, names or paths.
/// </summary>
public sealed class ProtectedSectionIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private static readonly XNamespace One = OneNoteXml.Namespace2013;

    private readonly string settingsDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));
    private readonly LargeResults large = new();
    private IOneNoteGateway? gateway;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Vault_IsWithheldWithoutAGrant_ReadableWithOne_AndWithheldAgainAfterRevoke()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        Directory.CreateDirectory(settingsDir);
        var settingsPath = Path.Combine(settingsDir, "settings.json");
        File.WriteAllText(settingsPath, """{ "schemaVersion": 1 }""");
        var store = new SettingsStore(settingsPath);
        using var gateway = new ComGateway(new ProcessProbe(), store);
        this.gateway = gateway;
        var notebook = await TestNotebook.EnsureAsync(gateway, Ct);

        var children = XDocument.Parse(await gateway.GetHierarchyAsync(notebook.NotebookId, HierarchyScope.Children, Ct));
        var vault = children.Root!.Elements(One + "Section").FirstOrDefault(s => (string?)s.Attribute("name") == BallastContent.VaultSectionName);
        Assert.SkipWhen(vault is null, "MCP Test has no Ballast Vault: run the Ballast scenario (story 17).");
        Assert.SkipUnless((string?)vault!.Attribute("encrypted") is "true" or "1", "The Ballast Vault isn't password-protected yet (story 17's owner step).");
        var vaultId = (string)vault.Attribute("ID")!;
        var locked = (string?)vault.Attribute("locked") is "true" or "1";
        var writer = new SettingsWriter(store, TimeProvider.System);
        var access = new ReadAccess(gateway, store.Read, writer, TimeProvider.System);

        // Hierarchy only (names and IDs, never content): which pages the Vault has, when unlocked.
        var vaultPages = XDocument.Parse(await gateway.GetHierarchyAsync(vaultId, HierarchyScope.Pages, Ct))
            .Descendants(One + "Page").Select(p => (string)p.Attribute("ID")!).ToList();
        output.WriteLine($"Vault {vaultId}: locked={locked}, pages visible={vaultPages.Count}.");

        // --- No grant -----------------------------------------------------------------------------------------
        var broad = await OkAsync(Hierarchy(access, notebook.NotebookId, HierarchyScope.Pages), "get_hierarchy(MCP Test, pages)");
        var broadXml = XDocument.Parse(await large.XmlAsync(broad, Ct));
        var keptVault = broadXml.Descendants(One + "Section").Single(s => (string?)s.Attribute("ID") == vaultId);
        Assert.Empty(keptVault.Elements(One + "Page"));
        Assert.Contains(vaultId, Withheld(broad));
        Assert.Contains(broad.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == "readNotGranted" && w.GetProperty("message").GetString()!.Contains(vaultId, StringComparison.Ordinal));

        var self = await OkAsync(Hierarchy(access, vaultId, HierarchyScope.Self), "get_hierarchy(Vault, self)");
        Assert.Empty(Withheld(self));
        NotGranted(await Hierarchy(access, vaultId, HierarchyScope.Pages), vaultId, "get_hierarchy(Vault, pages)");
        NotGranted(await Hierarchy(access, vaultId, HierarchyScope.Children), vaultId, "get_hierarchy(Vault, children)");
        await OkAsync(Scalar(new GetHierarchyParentUseCase(gateway, access), (u, c, ct) => u.ExecuteAsync(c, vaultId, ct)), "get_hierarchy_parent(Vault)");
        await OkAsync(Scalar(new GetHyperlinkToObjectUseCase(gateway, access), (u, c, ct) => u.ExecuteAsync(c, vaultId, string.Empty, ct)), "get_hyperlink_to_object(Vault)");

        var findPages = new FindPagesUseCase(gateway, PageTextCache.WithDefaults(), access);
        var search = await OkAsync(Run(findPages, (c, ct) => findPages.ExecuteAsync(c, BallastContent.VaultSectionName, notebook.NotebookId, false, ct)), "find_pages(Ballast Vault)");
        AssertNoVaultPage(await large.XmlAsync(search, Ct), search, vaultPages, vaultId);
        var findMeta = new FindMetaUseCase(gateway, access);
        var meta = await OkAsync(Run(findMeta, (c, ct) => findMeta.ExecuteAsync(c, BallastContent.CompleteMeta, notebook.NotebookId, false, ct)), "find_meta(ballast-complete)");
        AssertNoVaultPage(await large.XmlAsync(meta, Ct), meta, vaultPages, vaultId);

        // Deferred items AD-21 left to this story: does search XML mark the Vault, and is the recycle bin's section marked?
        var rawSearch = XDocument.Parse(await gateway.FindPagesAsync(notebook.NotebookId, BallastContent.VaultSectionName, includeUnindexed: true, Ct));
        var searchVault = rawSearch.Descendants(One + "Section").FirstOrDefault(s => (string?)s.Attribute("ID") == vaultId);
        output.WriteLine($"FindPages XML: Vault present={searchVault is not null}, encrypted attribute={(string?)searchVault?.Attribute("encrypted") ?? "absent"}.");
        var deletedPages = children.Root.Elements(One + "SectionGroup").Where(g => (string?)g.Attribute("isRecycleBin") == "true");
        foreach (var bin in deletedPages)
        {
            var sections = XDocument.Parse(await gateway.GetHierarchyAsync((string)bin.Attribute("ID")!, HierarchyScope.Sections, Ct)).Descendants(One + "Section");
            output.WriteLine($"Recycle bin sections encrypted: {string.Join(", ", sections.Select(s => (string?)s.Attribute("encrypted") ?? "absent"))}.");
        }

        foreach (var pageId in vaultPages.Take(1))
        {
            NotGranted(await Page(access, pageId), vaultId, "get_page_content(Vault page)");
            NotGranted(await Scalar(new GetBinaryPageContentUseCase(gateway, access), (u, c, ct) => u.ExecuteAsync(c, pageId, "{00000000-0000-0000-0000-000000000000}{1}{B0}", ct)), vaultId, "get_binary_page_content(Vault page)");
            NotGranted(await Scalar(new GetHierarchyParentUseCase(gateway, access), (u, c, ct) => u.ExecuteAsync(c, pageId, ct)), vaultId, "get_hierarchy_parent(Vault page)");
            NotGranted(await Scalar(new GetHyperlinkToObjectUseCase(gateway, access), (u, c, ct) => u.ExecuteAsync(c, pageId, string.Empty, ct)), vaultId, "get_hyperlink_to_object(Vault page)");
            NotGranted(await Scalar(new GetWebHyperlinkToObjectUseCase(gateway, access), (u, c, ct) => u.ExecuteAsync(c, pageId, string.Empty, ct)), vaultId, "get_web_hyperlink_to_object(Vault page)");
        }

        // --- A CLI-style grant, in the temporary settings ---------------------------------------------------------
        var key = GrantKeys.SectionKey((string)vault.Attribute("path")!);
        writer.SaveReadGrant(new ProtectedSectionGrant(key, BallastContent.VaultSectionName, GrantKeys.NotebookKey((string)children.Root.Attribute("path")!), vaultId, DateTimeOffset.UtcNow, GrantedVia.Cli, DateTimeOffset.UtcNow.AddHours(1)));

        var granted = await OkAsync(Hierarchy(access, vaultId, HierarchyScope.Pages), "get_hierarchy(Vault, pages) granted");
        Assert.Equal(vaultPages.Count, XDocument.Parse(await large.XmlAsync(granted, Ct)).Descendants(One + "Page").Count());
        var grantedBroad = await OkAsync(Hierarchy(access, notebook.NotebookId, HierarchyScope.Pages), "get_hierarchy(MCP Test, pages) granted");
        Assert.DoesNotContain(vaultId, Withheld(grantedBroad));

        foreach (var pageId in vaultPages.Take(1))
        {
            var result = await Page(access, pageId);
            if (locked)
            {
                // The grant check passes; the section is still locked in OneNote, so the read fails as before 1.20.
                Assert.True(result.IsError);
                Assert.Equal("SectionLocked", result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString());
            }
            else
            {
                var page = await OkAsync(Task.FromResult(result), "get_page_content(Vault page) granted");
                Assert.Contains(BallastContent.VaultSectionName, await large.XmlAsync(page, Ct), StringComparison.Ordinal);
            }
        }

        var listGrants = new ListGrantsUseCase(gateway, store.Read, TimeProvider.System);
        var listed = await OkAsync(Run(listGrants, listGrants.ExecuteAsync), "list_grants");
        var grant = Assert.Single(listed.GetProperty("meta").GetProperty("grants").EnumerateArray());
        Assert.Equal("active", grant.GetProperty("status").GetString());

        // --- Revoke ---------------------------------------------------------------------------------------------
        Assert.Equal(1, writer.RevokeReadGrant(key));
        NotGranted(await Hierarchy(access, vaultId, HierarchyScope.Pages), vaultId, "get_hierarchy(Vault, pages) revoked");
        foreach (var pageId in vaultPages.Take(1))
        {
            NotGranted(await Page(access, pageId), vaultId, "get_page_content(Vault page) revoked");
        }
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

    private Task<CallToolResult> Hierarchy(ReadAccess access, string start, HierarchyScope scope)
    {
        var useCase = new GetHierarchyUseCase(gateway!, access);
        return Run(useCase, (c, ct) => useCase.ExecuteAsync(c, start, scope, ct));
    }

    private Task<CallToolResult> Page(ReadAccess access, string pageId)
    {
        var useCase = new GetPageContentUseCase(gateway!, access);
        return Run(useCase, (c, ct) => useCase.ExecuteAsync(c, pageId, false, ct));
    }

    private Task<CallToolResult> Scalar<T>(T useCase, Func<T, ToolCallContext, CancellationToken, Task<Core.Results.ToolSuccess>> run)
        where T : IUseCase =>
        Run(useCase, (c, ct) => run(useCase, c, ct));

    private Task<CallToolResult> Run(IUseCase useCase, Func<ToolCallContext, CancellationToken, Task<Core.Results.ToolSuccess>> run) =>
        large.Runner.RunAsync("integration", useCase, run, Ct);

    private static async Task<JsonElement> OkAsync(Task<CallToolResult> call, string what)
    {
        var result = await call;
        Assert.NotNull(result.StructuredContent);
        Assert.False(result.IsError, $"{what}: {result.StructuredContent.Value.GetRawText()}");
        return result.StructuredContent.Value;
    }

    private static void NotGranted(CallToolResult result, string vaultId, string what)
    {
        Assert.True(result.IsError, $"{what} should be ReadNotGranted");
        var error = result.StructuredContent!.Value.GetProperty("error");
        Assert.Equal("ReadNotGranted", error.GetProperty("code").GetString());
        Assert.Contains(vaultId, error.GetProperty("message").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain(BallastContent.VaultSectionName, result.StructuredContent.Value.GetRawText(), StringComparison.Ordinal);
    }

    private static void AssertNoVaultPage(string xml, JsonElement root, IReadOnlyList<string> vaultPages, string vaultId)
    {
        var ids = XDocument.Parse(xml).Descendants(One + "Page").Select(p => (string)p.Attribute("ID")!).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(ids, vaultPages.Contains);
        Assert.Contains(vaultId, Withheld(root));
        var filtered = root.GetProperty("meta").GetProperty("filtered").EnumerateArray().Select(f => f.GetProperty("pageId").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(filtered, vaultPages.Contains);
    }

    private static IReadOnlyList<string> Withheld(JsonElement root) =>
        root.GetProperty("meta").TryGetProperty("withheld", out var w) ? [.. w.EnumerateArray().Select(e => e.GetString()!)] : [];
}
