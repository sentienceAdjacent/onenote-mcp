using OneNoteMcp.Core;
using OneNoteMcp.Core.Results;
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
