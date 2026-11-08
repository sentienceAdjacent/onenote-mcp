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

    /// <summary>
    /// An oversized result's XML couldn't be indexed by element, so it was stored as one span with an empty index (AD-20).
    /// Nothing is lost: <c>read_result</c> offset reads of <c>/</c> reach every character.
    /// </summary>
    public const string ResultNotIndexed = "resultNotIndexed";

    /// <summary>
    /// A password-protected section's pages were withheld because there is no read grant for it (AD-21). The message
    /// names the section's ID only. Its userAction says how the user grants it.
    /// </summary>
    public const string ReadNotGranted = "readNotGranted";

    /// <summary>
    /// The user granted a read for some hours or until revoked, but <c>settings.json</c> couldn't be written (invalid,
    /// written by a newer version, or not writable), so the grant works like <c>once</c>: it covers the section for
    /// <c>readGrants.onceMinutes</c>, in this server's memory only (AD-21).
    /// </summary>
    public const string GrantNotSaved = "grantNotSaved";

    /// <summary>
    /// <c>list_grants</c> read a <c>settings.json</c> that is invalid or written by a newer version: its grants count as
    /// absent (AD-10, AD-21). The listed entries, if any, are shown so the user can see what to fix.
    /// </summary>
    public const string SettingsInvalid = "settingsInvalid";
}
