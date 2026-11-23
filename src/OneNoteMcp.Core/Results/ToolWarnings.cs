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
    /// Entry 15 (Phill, 2026-11-17, Q1): an <c>exportRoots</c> entry holds a notebook of the user's, or sits inside one, so
    /// that notebook's sections count as password-protected (AD-21, protected by path). The read went on; writes fail with
    /// <c>SettingsInvalid</c> until the user changes the setting. The message names the root (the user's own setting).
    /// </summary>
    public const string ExportRootHoldsNotebooks = "exportRootHoldsNotebooks";

    /// <summary>
    /// Story 3.8: <c>open_package</c> opened the copy, but no page was listed within the wait (spike 3.1 O1: an opened copy
    /// lists late). Expected for a package whose sections are all empty, locked or protected (O5).
    /// </summary>
    public const string PagesNotListed = "pagesNotListed";

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
    /// under a new objectID, or OE lists of another length (AD-7). Names objectIDs and attribute names only. After a
    /// hierarchy rename or reorder (story 5.4): the node reads back with another name, or the parent's children in another
    /// order or at another page level; IDs and counts only.
    /// </summary>
    public const string ReadBackDiffers = "readBackDiffers";

    /// <summary>After a page write, an Outline that was sent has fewer images than were sent in it (AD-7).</summary>
    public const string ImageLost = "imageLost";

    /// <summary>The page (or, after a rename or reorder, the node) couldn't be re-read after a write that succeeded, so what OneNote kept wasn't checked (AD-7).</summary>
    public const string ReadBackFailed = "readBackFailed";

    /// <summary>
    /// <c>restore_from_journal</c> restored the page without some of its binaries: images the snapshot lacked, and each
    /// attachment, media file or other object it couldn't send (no temporary copy left, a path outside the temp folder,
    /// or refused by OneNote) (AD-8; 2.11 plan Q2). Counts by kind only, never IDs or content.
    /// </summary>
    public const string BinaryMissing = "binaryMissing";

    /// <summary>
    /// <c>restore_from_journal</c> couldn't use the entry's <c>parentId</c>, so it restored the page into the section at
    /// the entry's <c>parentNamePath</c>, or into the notebook's <c>Recovered</c> section (AD-8). Names which, never the
    /// path itself; <c>meta.parentId</c> is the section used.
    /// </summary>
    public const string RestoredElsewhere = "restoredElsewhere";

    /// <summary>
    /// A create found a live section or section group of the same kind and name (ignoring case) already under the parent,
    /// so it made nothing and returned that object's ID in <c>meta.objectId</c> (AD-7 <c>Create</c>). From
    /// <c>open_notebook</c> (story 4.5, plan Q1 a): the notebook at that path is already open, so nothing was opened, and
    /// <c>meta.objectId</c> is its current ID. Names the ID only.
    /// </summary>
    public const string AlreadyExisted = "alreadyExisted";

    /// <summary>
    /// <c>create_notebook</c> made the notebook, but couldn't give it its automatic write grant (AD-6 <c>grantedVia:
    /// created</c>): the notebook couldn't be read back, OneNote put it somewhere other than the folder the server checked,
    /// or <c>settings.json</c> couldn't be written. The create stands. Its userAction says how the user grants writes.
    /// </summary>
    public const string CreatedNotGranted = "createdNotGranted";

    /// <summary>
    /// A rename or reorder asked for what the hierarchy already shows (the same exact name, or the same order), so nothing
    /// was sent to OneNote and no journal entry was taken (story 5.4). Names the node's ID only.
    /// </summary>
    public const string Unchanged = "unchanged";

    /// <summary>
    /// A page soft delete succeeded, but the page's new ID in Deleted Pages couldn't be told apart, or the journal entry
    /// couldn't be rewritten with it (AD-7 <c>SoftDelete</c>; story 5.5 Q3): the entry has no <c>recycleBinId</c>, so a
    /// later restore matches by name. <c>meta.recycleBinId</c> is present only when the ID was found and the rewrite alone
    /// failed. It needs no userAction.
    /// </summary>
    public const string RecycleBinIdMissing = "recycleBinIdMissing";

    /// <summary>
    /// An export of content from a password-protected section, made under the section's read grant: the file OneNote wrote
    /// is plaintext, outside the section's encryption (AD-21 Copies outside OneNote; story 3.3). Names the page's and the
    /// section's IDs only. Its userAction says to tell the user.
    /// </summary>
    public const string ExportNotEncrypted = "exportNotEncrypted";

    /// <summary>
    /// A page move left the page's subpages in the source section, where OneNote promotes them to level 1 (AD-7 Moves;
    /// spike 3.1 M1d; story 3.4). Names the source section's ID and the count step 3 read, never a name.
    /// </summary>
    public const string SubpagesLeftBehind = "subpagesLeftBehind";

    /// <summary>
    /// A section or section-group move touching a cloud notebook held the write lock for a settle before its read-back
    /// (AD-7 Moves; spike 3.1 Implication 16; story 3.12). Names the moved item's ID and the wait. Its userAction says to
    /// check the move synced before relying on it.
    /// </summary>
    public const string Settled = "settled";

    /// <summary>
    /// After a section or section-group move, a moved section reads back at a location whose key (compared percent-decoded)
    /// holds a stored read grant or a live <c>once</c> window, which now covers it (AD-21; 3.12 security review, Phill
    /// 2026-11-17). The move stands. Names the moved item's ID and a count only. Its userAction says to tell the user.
    /// </summary>
    public const string ReadGrantAtLocation = "readGrantAtLocation";

    /// <summary>
    /// <c>create_page</c> with <c>parentPageId</c> made the page, but couldn't make it a subpage (an exception, the parent
    /// gone, the deadline or a cancel): it is at the end of the section at level 1, or, when the placement was sent and failed
    /// ambiguously, it may or may not have been placed (story 3.14 Q4 and review). Names the page's,
    /// the parent's and the section's IDs only. Its userAction names <c>set_page_level</c> and
    /// <c>reorder_hierarchy_children</c>.
    /// </summary>
    public const string SubpageNotPlaced = "subpageNotPlaced";

    /// <summary>
    /// <c>restore_from_recycle_bin</c> matched the recycled item to its soft-delete journal entry by name, not by its
    /// recycle-bin ID or section ID (AD-8; story 3.5 Q6): a reopen or a missing <c>recycleBinId</c>. Names the entry's ID and
    /// the parent the item was restored under, never a name. Its userAction says to check it is the item the user meant.
    /// </summary>
    public const string RestoredByName = "restoredByName";

    /// <summary>
    /// <c>restore_from_recycle_bin</c> restored a section in its own notebook, but the move to the other notebook the caller
    /// named was refused or failed (AD-8; story 3.5 Q7). The section stays restored in its own notebook (<c>meta.parentId</c>);
    /// after an outcome-unknown or cancelled move it may or may not have moved, and <c>meta.parentId</c> is absent (3.5 Fable finding 4).
    /// Gives the move's error code and message (IDs only); its userAction says to move it with <c>move_hierarchy_object</c>.
    /// </summary>
    public const string RestoredNotMoved = "restoredNotMoved";

    /// <summary>
    /// <c>sync_hierarchy</c> (story 4.3, 4.3 plan Q1): OneNote accepted the sync request. It returns before any sync is done
    /// (spike 4.1 S1) and offers no way to see one complete, so the warning says the sync was requested, not that it ran or
    /// finished. Every ok from <c>sync_hierarchy</c> carries it. Names no ID.
    /// </summary>
    public const string SyncRequested = "syncRequested";

    /// <summary>
    /// <c>close_notebook</c> (story 4.4; Phill, 2026-11-22, 4.4 plan Q1 a): OneNote closed the notebook. Every ok from
    /// <c>close_notebook</c> carries it. It says the server asked OneNote to sync the notebook and waited about 10 s first,
    /// that every ID in the notebook changes when it reopens, that OneNote's view may go blank, and that its grants
    /// survive; its userAction gives the reopen route (a local notebook by its path, a cloud one in OneNote). Names no ID.
    /// </summary>
    public const string NotebookClosed = "notebookClosed";

    /// <summary>
    /// <c>open_notebook</c> (story 4.5; AD-7 <c>AppState</c>, AD-9): OneNote opened the notebook, but its contents and
    /// OneNote's own notebook list may lag (spike 4.1: a cloud notebook of 11 sections listed in 16 s, OneNote's list about a
    /// minute late). Every ok from an open that sent <c>OpenHierarchy</c> carries it. Names no ID or path.
    /// </summary>
    public const string NotebookListMayLag = "notebookListMayLag";
}
