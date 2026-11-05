namespace OneNoteMcp.Core.Results;

/// <summary>The warning codes (AD-4, AD-9). A warning is a degraded but successful detail; its message never carries page content.</summary>
public static class ToolWarnings
{
    /// <summary>A section in the returned hierarchy is password-locked; its pages can't be read until it is unlocked in OneNote.</summary>
    public const string SectionLocked = "sectionLocked";

    /// <summary>OneNote returned no web link, as it does for an object in a notebook that isn't stored online.</summary>
    public const string WebLinkUnavailable = "webLinkUnavailable";

    /// <summary>
    /// A page's binary content isn't on this PC, as with an image in a notebook stored online that hasn't been
    /// downloaded (AD-4). From <c>get_binary_page_content</c> it can also mean the callbackID is wrong: OneNote returns
    /// the same code for both.
    /// </summary>
    public const string BinaryUnavailable = "binaryUnavailable";
}
