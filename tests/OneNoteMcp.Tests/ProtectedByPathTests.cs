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
        var warnings = root.GetProperty("warnings").EnumerateArray().ToList();
        var open = warnings.Single(w => w.GetProperty("message").GetString()!.Contains(V.OpenSectionId, StringComparison.Ordinal));
        Assert.Equal(ToolWarnings.ReadNotGranted, open.GetProperty("code").GetString());
        Assert.Contains("export root or the backups folder", open.GetProperty("message").GetString()!, StringComparison.Ordinal);
        var vault = warnings.Single(w => w.GetProperty("message").GetString()!.Contains(V.VaultId, StringComparison.Ordinal));
        Assert.Contains("is password-protected", vault.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }
}
