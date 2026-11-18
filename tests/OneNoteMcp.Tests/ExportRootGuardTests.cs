using OneNoteMcp.Core;
using OneNoteMcp.Core.Access;
using OneNoteMcp.Core.Results;
using OneNoteMcp.Core.Settings;
using Xunit;
using V = OneNoteMcp.Tests.VaultGateway;

namespace OneNoteMcp.Tests;

/// <summary>
/// Entry 15, Phill's decisions of 2026-11-17: the export-root guard (Q1: an export root that holds an open notebook the
/// user opened, or sits inside one, is reported; one `open_package` made carries a marker file and is allowed) and the
/// invalid-settings fallback (Q2: this run's last valid roots keep protecting; at startup the default root and the
/// backups folder do, with a warning).
/// </summary>
public sealed class ExportRootGuardTests
{
    private const string Ns = "xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\"";

    private static string Notebooks(params string[] paths) =>
        "<one:Notebooks " + Ns + ">"
        + string.Concat(paths.Select((p, i) => $"<one:Notebook name=\"N{i}\" ID=\"{{N{i}}}\" path=\"{p}\" />"))
        + "</one:Notebooks>";

    // --- The rule -------------------------------------------------------------------------------------------------------

    [Fact]
    public void ARootHoldingAUserNotebook_IsAProblem_NamingTheRootAndWhy()
    {
        var problem = ExportRootGuard.Problem([@"C:\Users\x\Documents"], Notebooks(@"C:\Users\x\Documents\OneNote Notebooks\Work"), _ => false);

        Assert.NotNull(problem);
        Assert.Contains(@"C:\Users\x\Documents", problem, StringComparison.Ordinal);
        Assert.Contains("holds a notebook", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void ARootHoldingOnlyNotebooksOpenPackageMade_IsFine()
    {
        var problem = ExportRootGuard.Problem(
            [@"C:\exports"],
            Notebooks(@"C:\exports\Pkg 20261117-120000"),
            path => string.Equals(path, Path.Combine(@"C:\exports\Pkg 20261117-120000", ExportRootGuard.MarkerFileName), StringComparison.OrdinalIgnoreCase));

        Assert.Null(problem);
    }

    [Fact]
    public void ARootInsideANotebooksFolder_IsAProblem()
    {
        var problem = ExportRootGuard.Problem([@"C:\notebooks\A\exports"], Notebooks(@"C:\notebooks\A"), _ => false);

        Assert.NotNull(problem);
        Assert.Contains("inside", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\exports2\N")]
    [InlineData("https://d.docs.live.net/abc/Documents/N")]
    [InlineData(@"D:\elsewhere\N")]
    public void NotebooksOutsideTheRoots_AndCloudNotebooks_AreFine(string notebookPath)
    {
        Assert.Null(ExportRootGuard.Problem([@"C:\exports"], Notebooks(notebookPath), _ => false));
    }

    // --- Through the tools ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARead_WhileARootHoldsAUserNotebook_GoesOn_WithAWarning_AndTheRootStillProtects()
    {
        using var h = new VaultHarness(byPath: true, guard: true);
        TestSettings.SetExportRoots(h.SettingsPath, V.VaultsFolder, @"C:\notebooks");

        // A read with no pages goes on, and says which root and why.
        var root = Grants.Ok(await h.Hierarchy(V.NotebookId, HierarchyScope.Sections));
        Assert.Contains(ToolWarnings.ExportRootHoldsNotebooks, Grants.WarningCodes(root));
        Assert.Contains(@"C:\notebooks", root.GetRawText().Replace(@"\\", @"\", StringComparison.Ordinal), StringComparison.Ordinal);

        // C:\notebooks holds Notebook A, so its Open section is now protected too: the root still protects (fails closed).
        Grants.NotGranted(await h.PageContent(V.OpenPageId), V.OpenSectionId);
    }

    [Fact]
    public async Task InvalidSettings_AfterAValidRead_TheLastValidRootsStillProtect()
    {
        using var h = new VaultHarness(byPath: true);
        Grants.NotGranted(await h.PageContent(V.VaultPageId), V.VaultId);

        File.WriteAllText(h.SettingsPath, "{ not json");

        Grants.NotGranted(await h.PageContent(V.VaultPageId), V.VaultId);
        Assert.Empty(h.Gateway.ContentCalls);
    }

    [Fact]
    public async Task InvalidSettings_AtStartup_ProtectsTheDefaultsOnly_AndWarns()
    {
        using var h = new VaultHarness(byPath: true, prime: false);
        File.WriteAllText(h.SettingsPath, "{ not json");

        // C:\vaults isn't the default root, so with no valid read this run the vault isn't protected by path; the read
        // says the protection rule is running on the defaults.
        var root = Grants.Ok(await h.PageContent(V.VaultPageId));
        Assert.Contains(ToolWarnings.SettingsInvalid, Grants.WarningCodes(root));
    }
}
