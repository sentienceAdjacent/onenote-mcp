using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using OneNoteMcp.Core.UseCases;
using OneNoteMcp.Server;
using OneNoteMcp.Xml;
using Xunit;
using V = OneNoteMcp.Tests.VaultGateway;

namespace OneNoteMcp.Tests;

/// <summary>
/// Entry 15's matrix rows that the shared vault matrix (<see cref="ProtectedByPathToolTests"/>) doesn't cover, through the
/// real read tools: a look-alike root, case and separator variants of a root, and a notebook's folder under a root
/// beside marked sections, with the withheld warning saying why.
/// </summary>
public sealed class ProtectedByPathTests
{
    [Fact]
    public async Task ALookAlikeRoot_ProtectsNothing()
    {
        using var h = new VaultHarness(byPath: true);
        TestSettings.SetExportRoots(h.SettingsPath, @"C:\vault"); // C:\vaults isn't under C:\vault
        h.Access.Protection();

        var root = Grants.Ok(await h.PageContent(V.VaultPageId));

        Assert.Contains(V.Secret, Grants.Xml(root), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"c:/VAULTS/")]
    [InlineData(@"C:\Vaults\")]
    [InlineData(@"C:\vaults\.\")]
    public async Task ARootWrittenWithOtherCaseOrSeparators_StillProtects(string root)
    {
        using var h = new VaultHarness(byPath: true);
        TestSettings.SetExportRoots(h.SettingsPath, root);

        Grants.NotGranted(await h.PageContent(V.VaultPageId), V.VaultId);
    }

    [Fact]
    public async Task ANotebookFolderUnderARoot_WithholdsItsUnmarkedSections_BesideTheMarkedOnes_SayingWhy()
    {
        // Marker mode: the vaults are password-protected; the notebook's own folder is also an export root, so Open is
        // protected by its path.
        using var h = new VaultHarness();
        TestSettings.SetExportRoots(h.SettingsPath, @"C:\notebooks");

        var root = Grants.Ok(await h.Hierarchy(V.NotebookId, HierarchyScope.Pages));

        // The recycle bin's Deleted Pages lies in the notebook's folder too.
        Assert.Equal([V.OpenSectionId, V.VaultId, V.GroupVaultId, V.DeletedSectionId], Grants.Withheld(root));
        // Backlog 15 (Phill, 2026-11-19, Q2 a): one warning for all of them, giving the count by reason; the IDs are in meta.withheld.
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == ToolWarnings.ReadNotGranted);
        var message = warning.GetProperty("message").GetString()!;
        Assert.Contains("4 protected section(s)", message, StringComparison.Ordinal);
        Assert.Contains("2 password-protected and 2 under an export root or the backups folder", message, StringComparison.Ordinal);
        Assert.DoesNotContain(V.OpenSectionId, message, StringComparison.Ordinal);
    }
}

/// <summary>
/// D180: a section opened by hand (<c>one:OpenSections</c>, entry 15's probe P3) under an export root, through the real
/// read tools. Its parent chain ends at once and it carries no marker, so only its path protects it: a broad read withholds
/// it, a target read of it or its page needs a read grant, and a stored grant for its path lets the page through.
/// </summary>
public sealed class LooseSectionProtectedByPathReadTests : IDisposable
{
    private const string Ns = "xmlns:one=\"" + OneNoteXml.Namespace2013 + "\"";
    private const string NotebookId = "{NB000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string OpenId = "{SO000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string OpenPageId = "{SO000000-0000-4000-8000-000000000001}{1}{E1}";
    private const string LooseId = "{LS000000-0000-4000-8000-000000000001}{1}{B0}";
    private const string LoosePageId = "{LS000000-0000-4000-8000-000000000001}{1}{E1}";
    private const string LoosePath = @"C:\exports\Loose.one";
    private const string Secret = "SECRET-LOOSE-TEXT";

    private readonly TempDirectory temp = new();
    private readonly FakeClock clock = new();
    private readonly FakeGateway gateway = new();
    private readonly SettingsStore store;
    private readonly ReadAccess access;

    public LooseSectionProtectedByPathReadTests()
    {
        const string loose = $"name=\"Loose\" ID=\"{LooseId}\" path=\"{LoosePath}\"";
        const string page = $"<one:Page ID=\"{LoosePageId}\" name=\"Loose page\" pageLevel=\"1\" />";
        gateway.Parents[NotebookId] = string.Empty;
        gateway.Parents[OpenId] = NotebookId;
        gateway.Parents[OpenPageId] = OpenId;
        gateway.Parents[LooseId] = string.Empty;
        gateway.Parents[LoosePageId] = LooseId;
        gateway.Hierarchies[(LooseId, HierarchyScope.Self)] = $"<?xml version=\"1.0\"?>\n<one:Section {Ns} {loose} />";
        gateway.Hierarchies[(LooseId, HierarchyScope.Pages)] = $"<?xml version=\"1.0\"?>\n<one:Section {Ns} {loose}>{page}</one:Section>";
        gateway.Hierarchies[(string.Empty, HierarchyScope.Pages)] =
            $"<?xml version=\"1.0\"?>\n<one:Notebooks {Ns}>"
            + $"<one:Notebook name=\"A\" nickname=\"A\" ID=\"{NotebookId}\" path=\"C:\\notebooks\\A\">"
            + $"<one:Section name=\"Open\" ID=\"{OpenId}\" path=\"C:\\notebooks\\A\\Open.one\"><one:Page ID=\"{OpenPageId}\" name=\"Open page\" pageLevel=\"1\" /></one:Section>"
            + "</one:Notebook>"
            + $"<one:OpenSections ID=\"{{OS}}{{1}}{{B0}}\"><one:Section {loose}>{page}</one:Section></one:OpenSections>"
            + "</one:Notebooks>";
        gateway.Pages[LoosePageId] =
            $"<?xml version=\"1.0\"?>\n<one:Page {Ns} ID=\"{LoosePageId}\" name=\"Loose page\" lastModifiedTime=\"2026-11-07T00:00:00.000Z\">"
            + $"<one:Outline objectID=\"{{O}}{{1}}{{B0}}\"><one:OEChildren><one:OE objectID=\"{{E}}{{1}}{{B0}}\"><one:T><![CDATA[{Secret}]]></one:T></one:OE></one:OEChildren></one:Outline></one:Page>";

        var settingsPath = temp.Combine("settings.json");
        TestSettings.SetExportRoots(settingsPath, @"C:\exports");
        store = new SettingsStore(settingsPath, temp.Combine("journal"));
        access = new ReadAccess(gateway, store.Read, new SettingsWriter(store, clock), clock);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task GetHierarchy_OfEveryNotebook_WithholdsTheLooseSection_SayingItIsUnderARoot()
    {
        var result = await Hierarchy(string.Empty);

        var root = Grants.Ok(result);
        Assert.Equal([LooseId], Grants.Withheld(root));
        Assert.DoesNotContain(LoosePageId, Grants.Xml(root), StringComparison.Ordinal);
        Assert.Contains(OpenPageId, Grants.Xml(root), StringComparison.Ordinal);
        var warning = Assert.Single(root.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("code").GetString() == ToolWarnings.ReadNotGranted);
        Assert.Contains("under an export root or the backups folder", warning.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetHierarchy_OfTheLooseSection_WithoutAReadGrant_IsReadNotGranted_SayingWhy()
    {
        var error = Grants.NotGranted(await Hierarchy(LooseId), LooseId);

        Assert.Contains("an export root", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(LoosePageId, error.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPageContent_InTheLooseSection_WithoutAReadGrant_IsReadNotGranted_ThePageNeverRead()
    {
        var result = await PageContent();

        var error = Grants.NotGranted(result, LooseId);
        Assert.Contains("an export root", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
        Assert.Empty(gateway.PageCalls);
    }

    [Fact]
    public async Task GetPageContent_InTheLooseSection_WithAStoredReadGrantForItsPath_ReturnsThePage()
    {
        new SettingsWriter(store, clock).SaveReadGrant(
            new ProtectedSectionGrant(GrantKeys.SectionKey(LoosePath), "Loose", string.Empty, LooseId, clock.GetUtcNow(), GrantedVia.Cli));

        var root = Grants.Ok(await PageContent());

        Assert.Contains(Secret, Grants.Xml(root), StringComparison.Ordinal);
    }

    private Task<CallToolResult> Hierarchy(string start)
    {
        var useCase = new GetHierarchyUseCase(gateway, access);
        return new ToolRunner(NullLogger<ToolRunner>.Instance).RunAsync("get_hierarchy", useCase, (c, ct) => useCase.ExecuteAsync(c, start, HierarchyScope.Pages, ct), Ct);
    }

    private Task<CallToolResult> PageContent()
    {
        var useCase = new GetPageContentUseCase(gateway, access);
        return new ToolRunner(NullLogger<ToolRunner>.Instance).RunAsync("get_page_content", useCase, (c, ct) => useCase.ExecuteAsync(c, LoosePageId, false, ct), Ct);
    }
}
