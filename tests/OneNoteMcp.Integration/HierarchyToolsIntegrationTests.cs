using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Com;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Server.Tools;
using OneNoteMcp.Xml;
using Xunit;

namespace OneNoteMcp.Integration;

/// <summary>
/// Story 7's hierarchy read tools against a real OneNote, through tool, use case and the real <see cref="ComGateway"/>.
/// Opt-in with <c>ONENOTE_MCP_INTEGRATION=1</c>; OneNote must already be running. Every call is a read. The only
/// write is <see cref="TestNotebook.EnsureAsync"/> seeding MCP Test if it is missing, so nothing outside MCP Test changes.
/// </summary>
public sealed class HierarchyToolsIntegrationTests : IDisposable
{
    private readonly string settingsDir = Path.Combine(Path.GetTempPath(), "onenote-mcp-integration", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Reads back a result over the inline threshold (AD-20): with the Ballast fixture, MCP Test at pages scope is one.
    private readonly LargeResults large = new();

    private ToolRunner Runner => large.Runner;

    [Fact]
    public async Task HierarchyReadTools_OverMcpTest()
    {
        Assert.SkipUnless(VtableSmokeTests.OneNoteFixture.Enabled, "Set ONENOTE_MCP_INTEGRATION=1 to run against a real OneNote.");
        Assert.True(new ProcessProbe().IsOneNoteRunning(), "OneNote desktop must already be running.");
        using var gateway = Gateway();
        var notebook = await TestNotebook.EnsureAsync(gateway, Ct);

        // get_hierarchy: MCP Test at every scope, and the root at notebooks; each XSD-valid.
        var hierarchy = new GetHierarchyTool(Runner, new GetHierarchyUseCase(gateway));
        foreach (var scope in Enum.GetValues<HierarchyScopeArgument>())
        {
            var root = Ok(await hierarchy.GetHierarchy(notebook.NotebookId, scope, Ct), $"get_hierarchy({scope})");
            var xml = await large.XmlAsync(root, Ct);
            Assert.Empty(SchemaValidation.Validate(xml));
            AssertLockedWarnings(xml, root.GetProperty("warnings"), $"get_hierarchy({scope})");
        }

        var notebooks = Ok(await hierarchy.GetHierarchy(cancellationToken: Ct), "get_hierarchy(root)");
        var notebooksXml = await large.XmlAsync(notebooks, Ct);
        Assert.Empty(SchemaValidation.Validate(notebooksXml));
        Assert.Equal(notebook.NotebookId, TestNotebookGuard.FindNotebookId(notebooksXml, notebook.Folder));

        // get_hierarchy_parent: page -> section -> notebook -> "".
        var parent = new GetHierarchyParentTool(Runner, new GetHierarchyParentUseCase(gateway));
        Assert.Equal(notebook.SectionId, Meta(Ok(await parent.GetHierarchyParent(notebook.PageId, Ct), "parent(page)"), "parentId"));
        Assert.Equal(notebook.NotebookId, Meta(Ok(await parent.GetHierarchyParent(notebook.SectionId, Ct), "parent(section)"), "parentId"));
        Assert.Equal(string.Empty, Meta(Ok(await parent.GetHierarchyParent(notebook.NotebookId, Ct), "parent(notebook)"), "parentId"));

        // get_special_location: all three return a path.
        var special = new GetSpecialLocationTool(Runner, new GetSpecialLocationUseCase(gateway));
        foreach (var location in Enum.GetValues<SpecialLocationArgument>())
        {
            Assert.False(string.IsNullOrWhiteSpace(Meta(Ok(await special.GetSpecialLocation(location, Ct), location.ToString()), "path")), location.ToString());
        }

        // get_hyperlink_to_object: a onenote: link to the seed page.
        var link = new GetHyperlinkToObjectTool(Runner, new GetHyperlinkToObjectUseCase(gateway));
        Assert.StartsWith(
            "onenote:",
            Meta(Ok(await link.GetHyperlinkToObject(notebook.PageId, cancellationToken: Ct), "hyperlink(page)"), "link"),
            StringComparison.OrdinalIgnoreCase);

        // get_web_hyperlink_to_object: MCP Test is local, so the link is empty, with the warning.
        var web = Ok(
            await new GetWebHyperlinkToObjectTool(Runner, new GetWebHyperlinkToObjectUseCase(gateway)).GetWebHyperlinkToObject(notebook.PageId, cancellationToken: Ct),
            "web hyperlink(page)");
        Assert.Equal(string.Empty, Meta(web, "link"));
        Assert.Equal("webLinkUnavailable", Assert.Single(web.GetProperty("warnings").EnumerateArray()).GetProperty("code").GetString());
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

    private ComGateway Gateway()
    {
        Directory.CreateDirectory(settingsDir);
        var path = Path.Combine(settingsDir, "settings.json");
        File.WriteAllText(path, """{ "schemaVersion": 1 }""");
        return new ComGateway(new ProcessProbe(), new SettingsStore(path));
    }

    private static JsonElement Ok(CallToolResult result, string call)
    {
        Assert.NotNull(result.StructuredContent);
        var root = result.StructuredContent.Value;
        Assert.False(result.IsError, $"{call} failed: {root.GetRawText()}");
        Assert.True(root.GetProperty("ok").GetBoolean());
        return root;
    }

    /// <summary>
    /// MCP Test's only lockable section is story 17's <c>Ballast Vault</c>, which the owner password-protects by hand: the
    /// warnings are exactly one <c>sectionLocked</c> per locked section in the XML, each naming its ID, and every
    /// locked section is the Vault. Passes with the Vault locked, unlocked or absent.
    /// </summary>
    private static void AssertLockedWarnings(string xml, JsonElement warnings, string call)
    {
        var locked = XDocument.Parse(xml).Descendants(OneNoteXml.One + "Section")
            .Where(s => (string?)s.Attribute("locked") is "true" or "1")
            .ToList();
        Assert.All(locked, s => Assert.Equal(BallastContent.VaultSectionName, (string?)s.Attribute("name")));

        // Story 1.20: a pages-scope read also carries one readNotGranted per withheld protected section (AD-21).
        Assert.All(warnings.EnumerateArray(), w => Assert.Contains(w.GetProperty("code").GetString(), new[] { "sectionLocked", "readNotGranted" }));
        var list = warnings.EnumerateArray().Where(w => w.GetProperty("code").GetString() == "sectionLocked").ToList();
        Assert.True(list.Count == locked.Count, $"{call}: {list.Count} sectionLocked warnings for {locked.Count} locked sections");
        foreach (var section in locked)
        {
            var id = (string)section.Attribute("ID")!;
            Assert.Single(list, w => w.GetProperty("message").GetString()!.Contains(id, StringComparison.Ordinal));
        }
    }

    private static string? Meta(JsonElement root, string name) => root.GetProperty("meta").GetProperty(name).GetString();
}
