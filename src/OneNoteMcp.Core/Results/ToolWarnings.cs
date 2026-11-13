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

    /// <summary>
    /// A page write in a password-protected section with no read grant: the read-back ran, but what OneNote kept, and a
    /// basis for the next write, are withheld from the result (AD-7, AD-21). Count-free; the counts-only
    /// <c>readBackDiffers</c> and <c>imageLost</c> warnings may come with it.
    /// </summary>
    public const string ReadBackWithheld = "readBackWithheld";

    /// <summary>A write ran without a basis, so it was checked against the page as it was just read, not as the caller last read it (AD-17).</summary>
    public const string NoBasis = "noBasis";

    /// <summary>The basis lacked a date or a hash the write compares; that check was skipped or filled from the server's own read (AD-17).</summary>
    public const string PartialBasis = "partialBasis";

    /// <summary>
    /// After a page write, the re-read page differs from what was sent: an attribute OneNote didn't apply, an OE re-created
    /// under a new objectID, or OE lists of another length (AD-7). Names objectIDs and attribute names only.
    /// </summary>
    public const string ReadBackDiffers = "readBackDiffers";

    /// <summary>After a page write, an Outline that was sent has fewer images than were sent in it (AD-7).</summary>
    public const string ImageLost = "imageLost";

    /// <summary>The page couldn't be re-read after a write that succeeded, so what OneNote kept wasn't checked (AD-7).</summary>
    public const string ReadBackFailed = "readBackFailed";
}
