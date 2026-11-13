namespace OneNoteMcp.Core;

/// <summary>
/// SEAM(5.3 cloud probe): creating a notebook in the user's OneDrive (a cloud notebook, by URL) is closed until a live
/// probe proves it (Phill, 2026-11-13, 5.3 plan Q3 and build directions). While <see cref="Enabled"/> is false:
/// <list type="bullet">
/// <item><c>create_notebook</c>'s location dialog offers a local folder only;</item>
/// <item>a URL <c>folder</c>, or a <c>oneDrive</c> answer, is <see cref="ErrorCode.NotSupported"/> before the lock;</item>
/// <item>no code path sends a URL to COM.</item>
/// </list>
/// Before it opens, the probe (<c>tests/OneNoteMcp.Integration/CreateHierarchyIntegrationTests.cs</c>,
/// <c>CloudNotebookProbe_…</c>) must show, on MCP Test Cloud's OneDrive: that <c>OpenHierarchy(url, "", cftNotebook)</c>
/// creates a notebook at a new OneDrive URL and returns its ID rather than an HRESULT; the <c>path</c> OneNote reads back
/// for it (its <c>NotebookKey</c> must equal the key of the URL sent, or the auto-grant can't be keyed); that it syncs; and
/// what an existing name at that URL does. Opening it also needs the OneDrive base URL (from the default notebook folder
/// when that is a URL, or an open cloud notebook's parent), the probe's to settle; it is not implemented here.
/// </summary>
public static class CloudNotebookCreate
{
    /// <summary>False until the cloud probe passes. Never true in this build.</summary>
    public static bool Enabled => false;

    /// <summary>The <c>userAction</c> for a cloud create while the seam is closed.</summary>
    public const string ClosedUserAction =
        "Creating a notebook in OneDrive isn't supported yet. Ask the user to create it in OneNote (File > New), or to choose a folder on this PC.";

    /// <summary>True when <paramref name="folder"/> looks like a URL (a scheme followed by <c>://</c>), such as a OneDrive notebook folder.</summary>
    public static bool IsUrl(string? folder) =>
        folder is not null && folder.Contains("://", StringComparison.Ordinal);
}
