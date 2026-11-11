namespace OneNoteMcp.Core;

/// <summary>
/// Core's own <c>CreateNewPage</c> page style. Com maps it to the interop enum; no COM type leaks into Core (AD-2).
/// <see cref="Default"/> is the section's own template, which may add content.
/// </summary>
public enum NewPageStyle
{
    Default,
    BlankPageWithTitle,
    BlankPageNoTitle,
}
