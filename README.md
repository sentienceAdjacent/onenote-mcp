# OneNote MCP

A local [MCP](https://modelcontextprotocol.io) server that lets Claude Code, and other MCP clients, read and manage the notebooks in your **OneNote desktop app** on Windows. It talks to OneNote through its COM automation API on your own machine. No cloud account, Microsoft Graph or sign-in is involved.

> **Status: early preview.** The read tools work, and preview builds are published as GitHub prereleases. The write tools are in preview, with write grants, stale-edit checks and the recovery journal that protect them: page writes, creating, renaming, reordering, moving, merging and deleting sections, section groups and pages, subpages, restoring from the recycle bin, exports and opening OneNote packages.

## What it does

**Reads, which never change anything:**

| Tool | What it does |
|---|---|
| `get_hierarchy` | notebooks, section groups, sections and pages as OneNote's XML, at any depth (`scope`) |
| `get_hierarchy_parent` | the parent of a page, section or section group |
| `get_page_content` | a page as OneNote's own XML, with a `basis` (the version Claude read) that the write tools check an edit against. Binaries are optional |
| `get_binary_page_content` | one image or attachment, base64 |
| `find_pages` | full-text search, including pages Windows Search hasn't indexed yet; recycle-bin pages are filtered out |
| `find_meta` | find pages carrying a given `one:Meta` tag |
| `get_hyperlink_to_object` / `get_web_hyperlink_to_object` | `onenote:` and web links to a notebook, section, page or paragraph |
| `get_special_location` | OneNote's backup, unfiled-notes and default notebook folders |
| `read_result` | reads a result that was too large to return whole, part by part (see below). It never calls OneNote |
| `list_grants` | lists your grants: the notebooks you've allowed Claude to write to, the notebooks where you've allowed permanent deletes without a dialog each time, and the password-protected sections you've allowed it to read, how and until when. It can't create or change a grant |

**User-visible, only when you ask:**

| Tool | What it does |
|---|---|
| `close_notebook` | closes an open notebook in OneNote, by its ID, which removes it from OneNote's notebook list. It needs no write grant and changes no content, and it never forces the close: it first asks OneNote to sync the notebook and waits about 10 s, so an edit Claude just made isn't lost. If OneNote was showing the notebook, its view may go blank, and a notebook stored online may show a "syncing changes" box until it is done. Every ID in the notebook changes when you reopen it; your grants for it are kept |
| `open_notebook` | opens an existing local notebook in OneNote by its folder (such as the path `close_notebook` reports), which adds it to OneNote's notebook list. It never creates anything: the folder must already hold a notebook (a `.onetoc2` file) and not sit inside another notebook's folder, and URLs, network, device and relative paths, files (`.one`, `.onepkg`), folders inside OneDrive and folders without a `.onetoc2` are refused before anything is sent to OneNote. A notebook stored online, or in a OneDrive folder, you reopen in OneNote yourself. A notebook that's already open is returned as it is. It needs no write grant and gives none; the notebook gets new IDs, and its sections may take a few seconds to list |
| `navigate_to` | shows a notebook, section group, section or page in OneNote, optionally selecting one object on it. It takes keyboard focus and changes what OneNote shows, so the plugin's `guide` skill tells Claude to call it only when you explicitly ask, for example to download a page's missing images. It changes no content |

**App state, only when you ask:**

| Tool | What it does |
|---|---|
| `sync_hierarchy` | asks OneNote to sync a notebook, section group, section or page. It changes nothing on screen, but the `guide` skill tells Claude to call it only when you ask for a sync, never on its own after a write. A success means OneNote accepted the request, not that the sync ran or finished: OneNote doesn't report that, so the result carries a `syncRequested` warning. Check File > Info > View Sync Status in OneNote to see how it went |

**Writes, only in notebooks you grant (first preview):**

| Tool | What it does |
|---|---|
| `create_page` | adds an empty page at the end of a section, and returns its ID; with `parentPageId`, a subpage of that page, after its subpages. It works only in a notebook you've granted: in Claude Code, the first write to a notebook raises a dialog where you type the notebook's name, or you grant it with `OneNoteMcp grant --write "Notebook name"` (see [Grants](#grants)); otherwise it fails with `WriteNotGranted`. In a password-protected section it needs only that write grant, never a read grant, except with `parentPageId`, which also needs your read grant for that section |
| `update_page_content` | changes a page with OneNote's own page XML, checked against the version Claude last read: if the page changed since, it fails with `StaleWrite` and nothing is written. Outlines it doesn't send are kept; an OE can be deleted only by naming it. Each write first saves the page to the [recovery journal](#recovery-journal), then re-reads it and warns about any change OneNote didn't apply. Claude calls it only when you ask to change a page. It needs the same write grant as `create_page`; in a password-protected section you haven't granted a read of, it can only add new content. |
| `delete_page_content` | removes one Outline (a block of content) from a page, checked against the version Claude last read: if the page changed since (its last-modified date moved, or that Outline changed), it fails with `StaleWrite` and nothing is deleted. It deletes whole Outlines only; single paragraphs (OEs) are removed with `update_page_content`, and it can't delete images or ink. Each delete first saves the page to the recovery journal, then re-reads it to check the Outline is gone. Claude calls it only when you ask to remove a block of a page. It needs the same write grant as `create_page`, and in a password-protected section also your read grant for that section (Claude reads the page first, which asks). |
| `restore_from_journal` | brings back a page as it was before an `update_page_content`, `delete_page_content` or `delete_hierarchy_object`, from the [recovery journal](#recovery-journal) entry that write saved (its `meta.journalEntryId`; entries are kept 30 days). It never overwrites: it makes a new page in the page's own section, else in the section at the same group/name path, else in a `Recovered` section it creates in the notebook. A page from a password-protected section goes back only into that section, while it is still protected. Images missing from the snapshot, and attachments whose temporary copy is gone, aren't restored, and the result says how many. A section's or section group's entry from `delete_hierarchy_object` can't be restored this way: the result says it is in the notebook's recycle bin. Claude calls it only when you ask to bring back a page's earlier version. It needs the same write grant as `create_page`. |
| `create_section` | adds an empty section to a notebook or section group, by the parent's ID and the section's name (`.one` is added unless the name ends with it), and returns its ID. A name with a path separator, `..`, a character Windows can't hold in a file name, a control, invisible or text-direction character, a reserved name or a leading or trailing space or dot, or one longer than 128 characters, is refused. If a section of that name is already there, it returns that one with an `alreadyExisted` warning and creates nothing; a section group of that name there is refused. Claude calls it only when you ask for a new section. It needs the same write grant as `create_page`. |
| `create_section_group` | adds an empty section group to a notebook or section group, with the same name rules, existing-name behaviour and write grant as `create_section`, except that a name with a `.` anywhere is refused, because OneNote can't create such a group. Claude then creates it without the `.` and, if you agree, renames it to the name you asked for with `rename_hierarchy_object`. |
| `create_notebook` | creates a notebook and opens it in OneNote. In Claude Code it shows you a dialog to choose the folder on this PC it goes in (prefilled with the folder Claude proposes, or OneNote's default notebook folder); the notebook gets its own new folder there, which must not exist yet or be empty, and must not be inside another notebook (open or closed: a folder holding a `.onetoc2` file counts), OneNoteMcp's own folders or a network share. A client that can't show the dialog can't create notebooks. Because you chose the place, the new notebook gets a write grant automatically (`list_grants` shows it as `created`); if that can't be saved, or OneNote hands back a notebook that already holds sections, the notebook is still created, with a `createdNotGranted` warning and no grant. OneDrive notebooks can't be created this way yet, and OneNote's API can't delete a notebook: close it in OneNote and delete its folder yourself. |
| `rename_hierarchy_object` | renames a section, section group or notebook (for a notebook, the name OneNote shows; its folder keeps its name), with `create_section`'s name rules. A name another section or section group under the same parent already uses, or another open notebook's name, is refused. A page is renamed by editing its title. Locked sections, and section groups in a notebook stored on OneDrive (whose rename never reached OneDrive in testing), are refused: rename those in OneNote. Each rename first saves the node's hierarchy to the recovery journal, then re-reads it. Renaming a password-protected section ends its read grant, so you're asked again. Renaming a section group that holds one ends it too. A rename that would move a section onto a location where you hold a read grant (for example where a granted section was before it was renamed) is refused, so a rename can never hand one section's grant to another. Claude calls it only when you ask to rename something. It needs the same write grant as `create_page`. |
| `reorder_hierarchy_children` | puts the sections and section groups of a notebook or section group, or the pages of a section, in a new order. Claude must list every child once, so nothing is left out or moved to another parent; sections stay before section groups, and a page moves with its subpages. The notebook list itself can't be reordered. Each reorder first saves the parent's hierarchy to the recovery journal, then re-reads it. Claude calls it only when you ask to reorder or sort. It needs the same write grant as `create_page`. |
| `delete_hierarchy_object` | moves a page, section or section group to its notebook's recycle bin, where you can restore it in OneNote. A page goes to Deleted Pages under a new ID (returned as `meta.recycleBinId`; the warning `recycleBinIdMissing` means it couldn't be told apart, and the delete still happened); a section keeps its ID; a section group dissolves, its sections going to the recycle bin one by one. It needs the notebook's write grant, and saves a recovery-journal entry first: a page's can be restored as a new copy with `restore_from_journal`, while a section's or group's only records its layout. A notebook, a page in a password-protected section (delete it permanently in OneNote instead), a page with subpages (delete those first), a locked section or a group holding one, and anything already in the recycle bin are refused. |
| `permanently_delete_hierarchy_object` | deletes a page, a section or an empty section group for good, skipping the recycle bin, checked against the version Claude last read (`StaleWrite` otherwise). Besides the notebook's write grant it needs your confirmation: a dialog asks you to type the name of what will go, for that one delete, unless you've given the notebook a [permanent-delete grant](#permanent-delete-grants). A copy is kept first: a page in the recovery journal; a section as a OneNote [backup](#backups) file plus each of its pages in the journal (each restorable as a new page with `restore_from_journal`); an empty group as a journal record. If the backup can't be made, nothing is deleted; OneNote can't back up an empty section, so one is refused (delete it to the recycle bin instead). A section already in the recycle bin can be deleted this way. Notebooks, password-protected sections, groups that hold anything, a page in the recycle bin and Deleted Pages (empty the recycle bin in OneNote), and a page with subpages are refused. Claude calls it only when you explicitly ask to delete something permanently. |
| `move_hierarchy_object` | moves a page to another section, or a section or section group to another notebook or section group, checked against the version Claude last read. It needs the write grant of every notebook it touches. A page gets a new ID and moves without its subpages, which stay behind as top-level pages. Nothing leaves a password-protected section this way: such a page, section, or a group holding one, is refused (move it in OneNote). A section or group can't go from a cloud (OneDrive or SharePoint) notebook into another notebook's section group in one step: move it to that notebook's root first. Both places are saved to the recovery journal first, as a record. Claude calls it only when you ask to move something. |
| `merge_sections` | copies every page of one section to the end of another, in the same notebook or another, under the same grants as a move. The source section stays as it was; delete it afterwards if you want it gone. Merging twice makes a second set of copies. A password-protected source is refused; copies into a protected section stay encrypted. Claude calls it only when you ask to merge. |
| `set_page_level` | makes a page a subpage (level 2 or 3) or a top-level page, with its own subpages moving by the same amount. A level that wouldn't sit under a parent page is refused, with which page is in the way. In a password-protected section it also needs your read grant. Claude calls it only when you ask. |
| `restore_from_recycle_bin` | brings a deleted page or section back out of the recycle bin: where you say, or, by default, where it was deleted from (known when Claude deleted it; for one you deleted in OneNote, Claude asks you where it goes). A page comes back with a new ID, as a top-level page; a section group comes back section by section. When several deleted items match what you asked for, Claude lists them (where each was, and when it was deleted) and asks you which before restoring. If an item's recycle-bin ID has gone stale (the notebook was reopened, say) and it is matched by name to several deletes, nothing is restored and Claude asks you which. A password-protected section, or one under the opened-copies folder, an export root or the backups folder, goes back only where it was, still protected (a password-protected one also locked). Claude calls it only when you ask to restore something. |
| `publish` | exports a page, a section or a notebook to a file: PDF, XPS, Word, MHTML, HTML, EMF (a page only), a OneNote section file, or a OneNote package (`.onepkg`, the only format for a notebook). Files go only under your [export folders](#export-folders), and nothing is ever overwritten. A section group is exported section by section. It needs no write grant and changes nothing in OneNote. Exporting a password-protected section, or a page in one, needs your read grant and gives an unencrypted file, which Claude tells you; a notebook package keeps protected sections encrypted. Claude calls it only when you ask for an export. |
| `open_package` | opens a OneNote package (`.onepkg`) as a separate copy: it unpacks it into a new folder in `%LOCALAPPDATA%\OneNoteMcp\opened` and opens that in OneNote, beside the original. See [Opened packages](#opened-packages). Claude calls it only when you ask to open a package. |

Results are always OneNote's own XML, so tables, tags, ink, images and attachments survive the trip. The server never edits it: it only leaves out whole pages, such as recycle-bin pages in search results and pages of a password-protected section you haven't granted, and the result names the pages it filtered or the sections it withheld. When something is degraded, such as a cloud image that isn't on this PC, a locked section or search results that may be incomplete, the result says so in `warnings` instead of failing.

**Grants.** Claude writes only in notebooks you've granted, deletes permanently only with your confirmation or permanent-delete grant, and reads a password-protected section's content only with your read grant for that section. It can't grant itself any of them: see [Grants](#grants).

**Large results.** A result up to `results.inlineMaxChars` characters (100,000 by default) comes back whole. Its text is one line of JSON (the result's `meta` and `warnings`), then the XML exactly as OneNote returned it, not escaped into JSON. A bigger result, such as a long page or a whole notebook's pages, is kept in the server's memory and comes back as an index: one entry per child element, with its size and a label (a section's or page's name, or the first 80 characters of an element's text). `read_result` then returns any element, run of elements, deeper index, or character range of it, each an exact substring of what OneNote returned, so nothing is lost or rewritten. A stored result lasts 30 minutes after its last read, and is never written to disk.

The plugin also installs a `guide` skill that teaches Claude to use the tools well: when to page a large result, what each warning means, how to change a page safely and undo the change, how to create, rename, reorder, move, merge, delete and restore sections, section groups and pages and undo each, how to make subpages, export and open packages, and never to grant access, edit OneNoteMcp's files, or run the grant or uninstall commands itself.

## Requirements

- Windows 10 or 11
- **OneNote desktop** (Microsoft 365, `ONENOTE.EXE`), **running in a window** (minimised is fine). The Windows 10 "OneNote for Windows" store app has no COM API.
- Claude Code (recommended), or any MCP client that can run a local stdio server
- Run the server at the same privilege level as OneNote, normally *not* as administrator

## Safety model

| | |
|---|---|
| **Reading** | Allowed on every notebook, except the content of password-protected sections, which needs your read grant for that section (see [Grants](#grants)). Reads never change anything. |
| **Focus** | The server never brings OneNote to the front or moves your view unless you ask. Only `navigate_to` does, and no other tool calls it. It never closes or kills OneNote. |
| **Logs** | IDs, error codes and timings only, never note content. |
| **Writing** | Only in a notebook you've granted, by typing the notebook's name in a confirmation dialog or with `OneNoteMcp grant --write` (see [Grants](#grants)). The model can't grant itself access. Writes run one at a time across every server process on this PC. |
| **Permanent delete** *(preview)* | Needs the write grant plus your confirmation for each delete, or a permanent-delete grant for the notebook (see [Permanent-delete grants](#permanent-delete-grants)). A copy is kept first, for 30 days (see [Backups](#backups)). |
| **Undo** | Before every change to a page, rename, reorder or delete, the server saves what it changes to a local journal: a page as a compressed snapshot, which `restore_from_journal` brings back as a new page; a section, section group or notebook's layout as a record. A section's permanent delete also keeps a OneNote backup file (see [Recovery journal](#recovery-journal)). |
| **Stale edits** | A write is refused with `StaleWrite` if the page changed since Claude read it (its last-modified date moved, or what the write changes differs), and nothing is written. For a section, section group or notebook, only its last-modified date is compared, a weaker check. Best effort: on a page that isn't displayed in OneNote, an edit another program makes through COM doesn't move the page's last-modified date, so only the comparison of the content Claude is changing catches it. |

**Limits, stated honestly:**
- Search results may be incomplete while Windows Search catches up. Results always say `possiblyIncomplete`.
- To make repeat searches fast, `find_pages` remembers each page's text in memory (never on disk) until the page's last-modified time changes. A page that another program (another add-in, or a second OneNote MCP session) edits through COM while it isn't open in OneNote keeps its old time, so a search can match it against its old text until it next changes or the server restarts. Edits made in OneNote itself are seen. Set `search.cacheMaxMB` to `0` to turn this off. The cache only helps searches whose candidate pages fit in it: when a search has more candidate pages than `search.cacheMaxMB` holds, each repeat of it reads most or all of them again.
- **Missing images.** Images in notebooks stored online often aren't on this PC until OneNote downloads them, and the API can't read them until then. They're reported as unavailable (`binaryUnavailable`) instead of failing, and the server never fetches them by itself. Two ways to get them: turn on File > Options > Sync > "Download all files and images" in OneNote, or ask Claude to show the page in OneNote (`navigate_to`, which takes focus), which downloads its images (in the one page measured, within 2 seconds; large pages or slow connections may take longer), then read it again. `navigate_to` only works with OneNote open in a window: if OneNote is running without one, it returns ok but shows and downloads nothing.
- A File Printout's printout document (`one:XPSFile`) can't be read through the API at all. It's reported as unavailable too; the printout's page images are separate images.
- **Grants** (read and write) guard against mistakes and casual misuse, not against hostile software already running as you. A grant belongs to its section's or notebook's location (its file, folder or OneDrive URL), so a different section that later appears at a granted location inherits the grant without asking. A Claude Code elicitation hook can answer the grant dialog automatically, and headless runs cancel it (so nothing is granted). The plugin blocks Claude from changing `settings.json`: a `PreToolUse` hook denies any file write in its folder (`%APPDATA%\OneNoteMcp`), any shell command that names it or that folder, and any shell command that runs the grant command or `uninstall` (whose `--purge` deletes it), and the skill tells Claude never to edit it or run the grant command. That block works in Claude Code only (other clients have no such hook), and is best effort: a deliberately disguised shell command can get past it, and nothing stops another program from editing the file.
- **The journal holds copies of your note content** on disk, section backups included, for 30 days by default, and DPAPI encryption of protected-section entries doesn't keep them from programs running as you (see [Recovery journal](#recovery-journal)). `OneNoteMcp uninstall --purge` removes it (see [Uninstall](#uninstall)).
- **What writes can't do.** Claude can add pages, change and add content, remove blocks and paragraphs, restore a page from the journal, create sections, section groups and notebooks, rename, reorder, move and merge them, make subpages, delete pages, sections and section groups, to the recycle bin or permanently, restore them from the recycle bin, export, and open packages. It can't delete a notebook (close it in OneNote and delete its folder), move a notebook, delete images or ink, or create a notebook on OneDrive. `update_page_content` refuses `one:HTMLBlock`, and HTML in page text beyond OneNote's own formatting.
- **Images and attachments from a file.** Claude adds a file from your PC by its full local path, and each new path shows you a dialog naming the file, to accept or decline; declining, or a client that can't show dialogs, writes nothing. OneNote copies the file into the notebook when it inserts it, so the page keeps it after you move or delete the original. Network, device and relative paths are refused.
- **A page you're editing.** A write to a page you're editing in OneNote at that moment can disturb your edit: another OneNote add-in (OneMore) records that a write while your selection spans an equation can corrupt the equation. This server hasn't tested it; finish, or click away from the page, before asking Claude to change it.
- **A write can't always tell whether it happened.** If OneNote times out, disconnects or crashes during a write, the result says `outcomeUnknown`, and Claude is told to re-read the page before doing anything else. The server never retries a write by itself.
- **OneNote's temporary copies of attachments.** Each time a page with an attachment is read, OneNote leaves a new copy of the file in `%TEMP%` (`{GUID}.bin`), and never deletes it; that's OneNote's behaviour. `restore_from_journal` depends on those copies: a page's attachment comes back only while its copy still exists.

## Grants

Claude can't give itself access. There are three kinds of grant, and only you can give them:

- A **write grant** for a notebook lets the write tools change it, until you revoke it.
- A **permanent-delete grant** for a notebook you've granted writes to lets Claude delete in it permanently without a dialog for each delete, until you revoke it.
- A **read grant** for one password-protected section lets Claude read that section's content, for as long as you choose.

No tool can create a grant. `list_grants` shows them all, and the plugin blocks Claude from running the grant command or editing `settings.json`, where they're saved (see the Limits above).

### Write grants

- **In Claude Code**, the first write to a notebook you haven't granted raises a dialog that names the notebook and its path. Type the notebook's name (the field's hint shows it) to grant writes to it until you revoke them. Declining, cancelling, a name that matches no open notebook (or more than one, or a different one), or a client that can't show dialogs grants nothing, and the write fails with `WriteNotGranted`. Time spent on the dialog doesn't count against the write's deadline.
- **In a terminal**, with any client: `OneNoteMcp grant --write "Notebook name"`. It matches the notebook's displayed name, shows the match and its path, and grants only when you type `yes`. It refuses to run without an interactive terminal, so a program can't run it for you. Either way the grant is keyed by the notebook's path and saved in `settings.json` (`notebooks`), and `list_grants` shows it (kind `writeNotebook`).
- **Revoke a write grant** with `OneNoteMcp grant --revoke --write "Notebook name"`; it revokes the notebook's permanent-delete grant too. The next write to that notebook asks again. If `settings.json` is invalid because two grants have the same key, the matching `--revoke` still runs and removes them all.
- A grant follows the notebook's path. Closing and reopening the notebook, or renaming it in OneNote, keeps it; moving or renaming its folder, or its OneDrive item, changes the path, and `list_grants` then shows the grant as `notebookNotOpen` until you grant the new location.
- **In a password-protected section**, the write grant is enough to write, and it never lets Claude read the section. Without your read grant for the section, Claude can only add new content there, since it can't see what it would change, and `delete_page_content` is refused until you grant a read.

### Permanent-delete grants

`permanently_delete_hierarchy_object` skips OneNote's recycle bin, so besides the notebook's write grant it needs your say-so for each delete:

- **In Claude Code**, each permanent delete raises a dialog that names what will go and where, and asks you to type its name. That confirms this one delete only: nothing is stored, so the next permanent delete asks again, and Claude can never turn a confirmation into a lasting permission. Declining, cancelling, another name, or a client that can't show dialogs deletes nothing, and the call fails with `PermanentDeleteNotGranted`.
- **To skip the dialog in one notebook**, run `OneNoteMcp grant --permanent-delete "Notebook name"` in a terminal. It needs the notebook's write grant (it refuses a notebook without one), matches the notebook like `--write`, and grants only when you type `yes`; it refuses to run without an interactive terminal. There is no dialog that grants it. It's saved with the notebook's write grant in `settings.json` (`notebooks`), and `list_grants` shows it as its own row (kind `permanentDeleteNotebook`).
- **Revoke** it alone with `OneNoteMcp grant --revoke --permanent-delete "Notebook name"`; the write grant stays. Revoking the write grant with `grant --revoke --write` revokes this one too.
- Without the notebook's write grant, no permanent delete runs, whatever you confirm.

### Read grants for password-protected sections

A section with a password stays private even after you unlock it in OneNote. Claude sees that it exists, but its pages are left out of hierarchy listings and searches (`meta.withheld` names the section by ID, and one `readNotGranted` warning counts such sections), and reading inside it fails with `ReadNotGranted`, until you grant a read of that one section:

- **In Claude Code**, reading one of its pages raises a dialog. Type the section's name (the field's hint shows it, to copy) and pick from one list: **this section, for 5 minutes** (the default), **1 hour**, **8 hours**, **24 hours**, **7 days** or **until I revoke it**. For any other number of hours (1 to 168), use the terminal command below. Declining, or a client that can't show dialogs, grants nothing. The dialog's name field gets a new name on every request, so the client has no earlier answer to fill it in from.
- **In a terminal**, with any client: `OneNoteMcp grant --read-section "Section name"`. It lists the matches if several open sections share the name, and offers hours or until revoked. It refuses to run without an interactive terminal, so a program can't run it for you.
- **Revoke** with `OneNoteMcp grant --revoke --read-section "Section name"`. `list_grants` shows what's stored.

**Sections under an export folder or the backups folder count as password-protected.** A section whose file sits under one of your `exportRoots` folders or the journal's `backups` folder (for example a `.one` export or a backup you opened in OneNote, or an opened package), or in a notebook stored there, is treated exactly as a password-protected one, whether or not it has a password: reading it needs your read grant, and the same writes are refused. A copy made outside OneNote can't say whether it was protected, so its location decides. Keep your own notebooks out of those folders: an export folder that holds a notebook you opened (or that sits inside one) gives every read an `exportRootHoldsNotebooks` warning and disables writes until you change `exportRoots`. While `settings.json` is unreadable, the folders from its last good read keep counting (at startup, only the default export folder and the backups folder). Journal entries made before this rule for such sections stay unencrypted until they expire. If such a folder is inside OneDrive, OneNote may later report its notebooks by a OneDrive web address; those addresses count too, and an `exportRoots` entry that is your OneDrive folder, or one folder below it, is too broad: it still protects, but reads warn `settingsInvalid` and writes are disabled until you change it.

A grant is for one section, keyed by its file path: renaming or moving the section means granting again. OneNote won't move a page from a password-protected section to its recycle bin (deleting one is permanent), but pages deleted before a section was protected stay readable there. **This section, for 5 minutes** lets Claude read the whole section (pages, listings and searches) for that long, then asks again. It's held in memory by that one server process, never saved, can't be revoked, and `list_grants` shows it while it lasts; the minutes are `readGrants.onceMinutes` in `settings.json` (1 to 60, default 5). A grant for hours or until revoked that can't be saved (because `settings.json` is invalid) works the same way, with a `grantNotSaved` warning. Pages of protected sections are never kept in the search cache, even under a grant. Saved grants live in `settings.json` (`protectedSections`). A large result read under a grant for hours or until revoked can be paged with `read_result` only while that grant lasts. One read under the 5-minute grant stays pageable for the stored result's life, `results.ttlMinutes` after the original call (default 30), even after the window ends; `read_result` never asks.

**OneNote's own switch for add-ins.** OneNote has an option, "Enable add-in programs to access password protected sections when they are unlocked" (File > Options > Advanced > Passwords). While it is off, OneNote keeps password-protected sections from every add-in, this server included: a section you've unlocked still looks locked (`locked="true"`, with no pages), its reads fail with `SectionLocked`, and listings carry a `sectionLocked` warning. Both name the option. So leaving it off is a way to keep protected sections away from every add-in, this server included, whatever you grant. With it on, your read grant for the section is still needed.

### Where `OneNoteMcp` is

A plugin install doesn't put the exe on your PATH: it sits in the plugin's install folder, `%USERPROFILE%\.claude\plugins\cache\sentienceadjacent\onenote\<version>\OneNoteMcp.exe` (the newest `<version>` folder). Run it by its full path, in quotes, and in PowerShell with `&` in front: `& "$env:USERPROFILE\.claude\plugins\cache\sentienceadjacent\onenote\<version>\OneNoteMcp.exe" grant --read-section "Section name"`. When a read or write is refused, the error's `userAction` names the exact path of the exe that's running.

## Recovery journal

Before every write that changes or deletes something, the server saves what it is about to change to a local journal: one compressed file per entry, written before the change is made. If it can't be saved, the write doesn't run (`JournalFailed`). The write's result carries the entry's ID (`meta.journalEntryId`). What each write saves:

- `update_page_content`, `delete_page_content`, and a page's `delete_hierarchy_object` or `permanently_delete_hierarchy_object`: the whole page, its XML and the images it has on this PC.
- `rename_hierarchy_object` and `reorder_hierarchy_children`: the layout of the node or of the parent whose children move (names, IDs and order), not page content.
- `move_hierarchy_object`: the layout of both places, where it was and where it goes. `merge_sections`: the destination's. `restore_from_recycle_bin`: where it goes. `set_page_level`, and `create_page` with `parentPageId`: the section's.
- A section's or section group's `delete_hierarchy_object`: its layout. Its pages stay in OneNote's recycle bin.
- A section's `permanently_delete_hierarchy_object`: each of its pages, one entry each (`meta.pageEntryIds`), then a OneNote [backup](#backups) of the whole section with its own entry. An empty section group's: a record of it.
- `create_page` (without `parentPageId`), `create_section`, `create_section_group`, `create_notebook` and `restore_from_journal` only add, so they save nothing; `publish` and `open_package` change nothing in your notebooks.

- **Undo.** To undo an edit it just made, Claude writes the old content back as a normal edit, so the page stays put and nothing is duplicated.
- **Restore.** For anything else (an earlier change, a delete, or when you want the old version as a copy), `restore_from_journal` brings the page back as it was before that write, as a **new** page. It never overwrites or merges: the edited page stays as it is, and you delete whichever copy you don't want in OneNote. The new page goes into the page's own section if that still exists, else into the section at the same group and name path in that notebook, else into a section named `Recovered` that it creates in the notebook, and the result says so. The notebook must be open in OneNote, at the same location, and needs your write grant.
- **What it can't restore.** `restore_from_journal` restores pages only. A rename's or reorder's entry, a section's or section group's entry from `delete_hierarchy_object`, a move's, merge's, recycle-bin restore's, `set_page_level`'s or subpage `create_page`'s layout entry, and a section backup return `NotSupported`, saying where the object or file is: rename, reorder, move or set the level back to undo those, delete a merge's copies, and restore a deleted section with `restore_from_recycle_bin` or from OneNote's recycle bin (a deleted section group's sections are there one by one).
- **What doesn't come back.** Images that weren't on this PC when the page was saved, such as cloud images OneNote hadn't downloaded. Attachments come back only while OneNote's temporary copy of them still exists (see the Limits above). The result counts what's missing (`binaryMissing`).
- **Finding an entry.** Claude restores by the ID the write returned. There's no tool to list entries, so if that ID is lost, for example in a new session, the entry stays in the folder for its 30 days but Claude can't look it up.
- **Where.** `%LOCALAPPDATA%\OneNoteMcp\journal`, or the folder `journal.path` names in `settings.json` (a full path). Entries are named like `20261107T101112123-<pid>-<guid>.json.gz`.
- **How long.** Each server prunes the journal when it starts and then daily: entries older than `journal.retentionDays` (default 30) go, then the oldest go while the journal is bigger than `journal.maxSizeMB` (default 2048, that is 2 GB), but never the newest entry. Both take whole numbers above 0. A value that isn't valid makes the whole `settings.json` invalid: writes then fail with `SettingsInvalid`, and pruning waits, until you fix it.

  ```json
  {
    "schemaVersion": 1,
    "journal": { "path": "D:\\OneNoteMcp\\journal", "retentionDays": 14, "maxSizeMB": 1024 }
  }
  ```

- **Password-protected sections.** An entry for a page in a protected section has its title and content encrypted with Windows DPAPI for your Windows account. That keeps it from other accounts, from a copy of the disk and from a backup. It doesn't keep it from programs running as you: any of them can decrypt the entry for its 30 days, whereas OneNote locks the section again on its own timer. Such a page is restored only into that same section, while it is still password-protected (and unlocked).
- **Removing it.** The journal holds **copies of your note content**. `OneNoteMcp uninstall --purge` removes it, backups included (see [Uninstall](#uninstall)).

### Backups

Before `permanently_delete_hierarchy_object` deletes a section, OneNote publishes a copy of it as a plain `.one` section file into a `backups` folder inside the journal folder, named after its journal entry (`backups\20261107T101112123-<pid>-<guid>.one`). The delete runs only once that file is complete; if it can't be made, nothing is deleted (`BackupFailed`). OneNote won't publish a section with no pages, so an empty section can't be deleted permanently: delete it to the recycle bin instead.

- **How long.** A backup belongs to its entry: it counts toward `journal.maxSizeMB` with it and is pruned with it, so it's kept for 30 days by default (`journal.retentionDays`). A backup left without an entry, by a delete that failed after the backup or by a crash, goes once it's older than that.
- **Where.** On this PC, outside OneDrive, unless you move the journal there with `journal.path`.
- **Not encrypted.** It's a plain section file that any program running as you can read, which is one reason password-protected sections can't be deleted permanently.
- **Restoring.** `restore_from_journal` can't restore a backup. The section's pages come back one by one, as new pages, from their own entries (`meta.pageEntryIds`). To get at the section itself, copy the backup out of the journal folder and open the copy in OneNote (File > Open); whether that brings the section back as it was hasn't been tested. Never open the backup where it is: pruning and `uninstall --purge` delete it, with any edits made in it.

## Exports and opened packages

### Export folders

`publish` writes files only under your export folders: the `exportRoots` list in `settings.json`, by default `Documents\OneNote Exports`, created on first use. A file name Claude gives lands under the first one; a full path must be inside one of them; anywhere else is refused (`ExportPathNotAllowed`). Nothing is ever overwritten: an existing file is refused (`FileExists`, whose message names the file's full path), and Claude asks you for another name.

- **Changing them.** Only you can: edit `settings.json` yourself (Claude never edits it), with full local paths:

  ```json
  {
    "schemaVersion": 1,
    "exportRoots": ["D:\\Exports\\OneNote", "C:\\Users\\me\\Documents\\OneNote Exports"]
  }
  ```

- **Keep your own notebooks out of them.** Every section under an export folder counts as password-protected (see [Grants](#read-grants-for-password-protected-sections)). An export folder that holds a notebook you use, or sits inside one, gives every read an `exportRootHoldsNotebooks` warning and turns writes off until you change `exportRoots`.
- **OneDrive.** On many PCs `Documents` is synced to OneDrive, so the default export folder is too, and exports land in OneDrive. That's fine for files. OneNote may report a notebook in such a folder by its OneDrive web address; those addresses count as under the export folder too. An export folder that is your OneDrive folder itself, or one folder below it, is too broad to tell apart from your own cloud notebooks: it still protects, but reads warn `settingsInvalid` and writes are turned off until you change it.
- **Exports of protected content aren't encrypted.** A page or section export of a password-protected section needs your read grant for it, and the file is plain: PDF, Word and the rest by nature, and `.one` because OneNote writes it in the clear. Claude tells you (`exportNotEncrypted`). A notebook package (`.onepkg`) keeps protected sections encrypted, so it needs no read grant.

### Opened packages

`open_package` unpacks a `.onepkg` into a new folder named after the package with the date and time, in `%LOCALAPPDATA%\OneNoteMcp\opened`, and opens it in OneNote as a separate notebook beside the original.

- **Never in OneDrive.** A copy unpacked inside a OneDrive folder is converted into an online notebook by OneDrive's sync client while OneNote has it open, and OneNote then closes, so any destination in OneDrive is refused. If your AppData folder is itself in OneDrive, `open_package` can't be used on that PC; open the package in OneNote (File > Open) instead, which uploads it properly.
- **Protected, read-only to Claude.** Every section of the copy counts as password-protected, so reading it needs your read grant, and Claude has no write grant to it.
- **Closing it.** Close the copy in OneNote when you're done; no tool closes a notebook.
- **Cleanup.** When the server starts while OneNote is running, it deletes a copy's folder once the copy is closed and nothing in it has changed for more than 7 days. It only touches folders it made (they hold a `.onenote-mcp-opened` file). `OneNoteMcp uninstall --purge` deletes the copies too, skipping any still open (see [Uninstall](#uninstall)).

## Install

Only preview builds (`0.x` prereleases) exist so far.

### Claude Code plugin (recommended)

```text
/plugin marketplace add sentienceAdjacent/plugins
/plugin install onenote@sentienceadjacent
```

If you added `sentienceAdjacent/onenote-mcp` as a marketplace before, first run `/plugin uninstall onenote-mcp@onenote-mcp` and `/plugin marketplace remove onenote-mcp`.

This installs the MCP server and fetches the matching pre-built `OneNoteMcp.exe` from GitHub Releases, pinned by SHA-256. No .NET SDK is needed. Updates come from `/plugin marketplace update sentienceadjacent`. The catalog lives in [sentienceAdjacent/plugins](https://github.com/sentienceAdjacent/plugins), and its tools appear as `mcp__plugin_onenote_desktop__*`.

### Your own marketplace

To pin a version yourself, for example in a team marketplace, add an `archive` entry that points at a release zip and the hash from its `.sha256` asset:

```json
{
  "name": "onenote",
  "source": {
    "source": "archive",
    "url": "https://github.com/sentienceAdjacent/onenote-mcp/releases/download/v<ver>/onenote-<ver>-win-x64.zip",
    "sha256": "<the hex hash from onenote-<ver>-win-x64.zip.sha256>"
  }
}
```

Use `0.4.0-rc.4` or later: earlier releases ship a plugin named `onenote-mcp`. You can also host the zip internally and point `url` at your copy: keep the same bytes, so the hash still matches.

### Other MCP clients

1. Download `OneNoteMcp.exe`, and its `.sha256` to check it, from the [releases page](https://github.com/sentienceAdjacent/onenote-mcp/releases). Previews are listed as prereleases. Put it in a folder of your choice.
2. Add it to your client's MCP config as a stdio server. For Claude Desktop, use `%APPDATA%\Claude\claude_desktop_config.json`:

   ```json
   {
     "mcpServers": {
       "onenote": { "command": "C:\\path\\to\\OneNoteMcp.exe" }
     }
   }
   ```

3. Restart the client.

Each tool tells Claude Code it may return up to 500,000 characters (`_meta["anthropic/maxResultSizeChars"]`), so results up to the inline limit reach the model whole. Other clients don't know that key, and may cut a result short or save it to a file well below 100,000 characters. For them, lower the inline limit in `%APPDATA%\OneNoteMcp\settings.json` to what the client shows whole, so larger results come back as an index instead:

```json
{
  "schemaVersion": 1,
  "results": { "inlineMaxChars": 20000 }
}
```

`results` takes whole numbers: `inlineMaxChars` 20,000 to 380,000 (default 100,000), `ttlMinutes` 1 to 1,440 (default 30; how long a stored result lives after its last read), and `maxStoreMB` 16 to 4,096 (default 256; past it, the least recently read results are dropped). A value out of range makes the whole file invalid, and the server then uses the defaults.

`search.cacheMaxMB` (0 to 1,024, default 64) caps the estimated size of what `find_pages` remembers between searches: each page's text and ID counted as UTF-16, plus a small fixed overhead per page; past it, the least recently used pages are dropped. `0` turns the cache off, so every search reads every candidate page again:

```json
{
  "schemaVersion": 1,
  "search": { "cacheMaxMB": 0 }
}
```

`readGrants.onceMinutes` (1 to 60, default 5) is how long the grant dialog's **this section, for N minutes** choice lasts:

```json
{
  "schemaVersion": 1,
  "readGrants": { "onceMinutes": 10 }
}
```

As with `results` and `search`, a value out of range makes the whole file invalid: the server then uses the defaults, and your saved read grants count as absent until you fix it.

### From source

You need the .NET 10 SDK.

```powershell
git clone https://github.com/sentienceAdjacent/onenote-mcp
cd onenote-mcp
dotnet publish src/OneNoteMcp.Server -c Release -r win-x64 -o publish
```

Then point your client at `publish\OneNoteMcp.exe`, as in the previous section. In Claude Code without the plugin: `claude mcp add --scope user onenote -- "C:\path\to\onenote-mcp\publish\OneNoteMcp.exe"`.

### Uninstall

Removing the plugin or the exe leaves OneNoteMcp's own files behind: `settings.json`, with your read and write grants, the logs, and the recovery journal with its section backups, which hold **copies of your note content**. `OneNoteMcp uninstall --purge` removes them, so run it *before* you remove the exe:

1. **Quit** every Claude Code session, and any other client, that uses OneNoteMcp, so no server holds its log file open.
2. **Purge**, in a terminal, with the exe's full path. For the plugin (use the newest `<version>` folder):

   ```powershell
   & "$env:USERPROFILE\.claude\plugins\cache\sentienceadjacent\onenote\<version>\OneNoteMcp.exe" uninstall --purge
   ```

   It lists what it will remove and asks you to type `purge`. `--yes` skips the question, and is required where it can't ask, such as in a script. If another OneNoteMcp is still running, it warns first with that process's ID: quit it and run purge again. It removes `settings.json` only while no running server is writing it; if one is, it keeps that file, says so, and removes the rest.
3. **Remove the program.** For the plugin, from the same terminal: `claude plugin uninstall onenote@sentienceadjacent` (starting Claude Code first would start the server again, and with it a new log). For a manual or source install, delete the exe and its MCP config entry (`claude mcp remove --scope user onenote` in Claude Code).

What purge removes, and nothing else:

- `%APPDATA%\OneNoteMcp\settings.json`, and any `.settings.*.tmp` an interrupted save left beside it;
- the `onenote-mcp-*.log` files in `%LOCALAPPDATA%\OneNoteMcp\logs`;
- journal entries (files named like `20261107T101112123-<pid>-<guid>.json.gz`, and the same names with a leading `.` and a trailing `.tmp` that an interrupted write leaves) in the journal folder: `journal.path` from `settings.json`, or `%LOCALAPPDATA%\OneNoteMcp\journal`. If `settings.json` can't be read, it uses that default and says so. It refuses a journal folder that is a drive or share root, your profile, `%APPDATA%` or `%LOCALAPPDATA%` themselves, Desktop, Documents, Windows or Program Files, or fewer than two folders below its root, and then removes nothing from it;
- section backups (files named like `20261107T101112123-<pid>-<guid>.one`) in that journal folder's `backups` folder, unless the journal folder was refused;
- the copies `open_package` made, in `%LOCALAPPDATA%\OneNoteMcp\opened`: each folder holding the `.onenote-mcp-opened` marker, with everything in it. It skips any copy OneNote still has open, and says which; close those in OneNote and run it again. To know what is open it asks OneNote, but only if OneNote is already running (it never starts it); if OneNote isn't running it can't tell, since OneNote reopens its notebooks when it starts, so it skips every copy and lists them. Start OneNote, close the copies, and run it again. A folder there without the marker is left alone;
- then each of those folders (`backups` before the journal folder, and the `opened` folder), and the `OneNoteMcp` folder above the logs and journal, but only once nothing else is left in it. It looks inside no other subfolder.

It prints every path it removed, kept or refused. It exits with 0 when done, 1 when something was refused or couldn't be removed (usually a log still in use: quit the client and run it again), and 2 on bad usage. It never removes `OneNoteMcp.exe` or the plugin's install folder (that's step 3), and it never starts OneNote.

Large results are held in the server's memory only and vanish when it exits, so there are no result files to remove. Claude Code itself may save a large tool result to disk, under `~/.claude/projects/<project>/<session>/tool-results/`, when a result is bigger than it shows inline. Those files are Claude Code's, can hold note content, and neither uninstalling nor `--purge` removes them: delete them there if you need to.

## For agents installing this

Follow these steps in order, and stop to report to the user if any check fails:

1. Confirm the OS is Windows. Check that `ONENOTE.EXE` exists (normally `C:\Program Files\Microsoft Office\root\Office16\ONENOTE.EXE`), and that OneNote is running in a window (`Get-Process ONENOTE | Where-Object MainWindowHandle -ne 0` returns it; minimised is fine).
2. Confirm the shell is **not** elevated: `([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole('Administrator')` must be `False`.
3. Install by one route:
   - **Claude Code plugin** (recommended): run `claude plugin marketplace add sentienceAdjacent/plugins` and then `claude plugin install onenote@sentienceadjacent` in a shell (the user can type the `/plugin` commands above instead). Ask the user to restart Claude Code so the tools load.
   - **Other MCP clients, or Claude Code without the plugin:** download `OneNoteMcp.exe` and `OneNoteMcp.exe.sha256` from the newest release on the [releases page](https://github.com/sentienceAdjacent/onenote-mcp/releases), into a folder the user chooses. Check that `(Get-FileHash OneNoteMcp.exe -Algorithm SHA256).Hash` equals the first word of the `.sha256` file, ignoring case. Then register it: `claude mcp add --scope user onenote -- "<folder>\OneNoteMcp.exe"` in Claude Code, or the `mcpServers` entry under "Other MCP clients" for Claude Desktop. Ask the user to restart the client.
   - **From source:** check that `dotnet --list-sdks` lists a 10.0 SDK, then run the three commands under "From source", register `publish\OneNoteMcp.exe` as in the previous route, and ask the user to restart the client.
4. Verify, after the user has restarted the client: call `get_hierarchy` through the client's own tools (the default scope is `notebooks`). It should list the user's notebooks. Don't start `OneNoteMcp.exe` yourself to check it: the test is that the client loads it.
5. Don't edit `%APPDATA%\OneNoteMcp\settings.json`, never run `OneNoteMcp grant` yourself, and never grant access yourself. Grants are the user's.
6. Uninstall only when the user asks, and follow [Uninstall](#uninstall). Never run `OneNoteMcp uninstall --purge` yourself: it deletes the user's grants, so it is the user's to run (with the Claude Code plugin installed, its guard blocks it). Give the user the step 2 command with the path of the exe they installed, to run in a terminal after quitting Claude Code (a server running in your own session keeps its log open, and purge then exits 1). Purge never removes the exe; step 3 of Uninstall does.

## Files it uses

| Path | Contents |
|---|---|
| `%APPDATA%\OneNoteMcp\settings.json` | timeouts, large-result limits (`results`), the search cache cap (`search`), write and permanent-delete grants (`notebooks`), read grants for password-protected sections (`protectedSections`), the journal's folder and limits (`journal`), and export folders (`exportRoots`). Optional |
| `%LOCALAPPDATA%\OneNoteMcp\logs\` | logs: IDs and error codes only, never note content |
| `%LOCALAPPDATA%\OneNoteMcp\journal\`, or `journal.path` | the [recovery journal](#recovery-journal): compressed page snapshots and hierarchy records, and in its `backups` folder the [section backups](#backups) of permanent deletes (`.one` files); copies of your note content |
| `Documents\OneNote Exports\`, or your `exportRoots` | [exports](#export-folders): files `publish` wrote |
| `%LOCALAPPDATA%\OneNoteMcp\opened\` | [opened packages](#opened-packages): the copies `open_package` unpacked |

## How it works

```text
MCP client ──stdio──► OneNoteMcp.exe ──COM (one STA thread)──► OneNote desktop
```

- **One server process per client:** several can run at once.
- **COM calls go one at a time,** with timeouts. If OneNote restarts, the server reconnects.
- **The server lets go of OneNote 5 seconds after its last call.** While it holds on, closing OneNote's window doesn't end `ONENOTE.EXE`, and starting OneNote again then shows "OneNote is cleaning up". The delay is `timeouts.idleReleaseSeconds` in `settings.json` (whole seconds, default `5`). Above about 10, that message can come back when you restart OneNote soon after closing it. A OneNote with no open window counts as not running, so the server doesn't connect to one that is closing. That covers a OneNote that is still starting, or one another program started without a window. A minimised window counts as open, and the server doesn't refuse a OneNote it can't see at all (as when the server runs on another desktop). With `allowSilentActivation` on, the server connects anyway.
- **Writes go one at a time on the whole PC,** under a machine-wide lock that every server process shares, so two sessions never write at once; a write that can't get the lock in time fails with `ComBusy`, and nothing is written. Reads never wait for it. Each edit or delete of page content runs the same steps in order: check the grant, take the lock, read the page, validate, compare with the basis Claude sent, save the snapshot to the journal, write, read the page back, release the lock. Renames, reorders and deletes of sections, section groups and pages follow the same steps on the node they change: a permanent delete also checks your confirmation or permanent-delete grant first, and saves a section's pages and its backup before the delete; a rename or reorder reads the node back afterwards. A create checks the grant (none for a new notebook) and takes the lock too, but has no basis to compare and saves nothing to the journal.
- **Large results stay in memory,** in each server process, keyed by a `resultId`. The server indexes them by the exact character positions of their elements and serves parts by those positions, so it never re-serialises XML. `read_result` reads only that memory and never calls OneNote. Logs record a stored result's ID and size, never its content.

The design decisions are in the [architecture spine](_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md).

## Contributing

Issues and pull requests are welcome. Every pull request needs the contributor agreement in [CONTRIBUTING.md](CONTRIBUTING.md).

## Cutting a release (maintainers)

1. **Bump `<Version>`** in `Directory.Build.props`, in a PR. Use a new string every time, with a prerelease suffix for candidates (`0.4.0-rc.1`, `0.4.0-rc.2`, then `0.4.0`). Never reuse one: users who already have a version never download it again, even when it was rebuilt.
2. **Merge** the PR to `main`.
3. **Tag** the merge commit `v<Version>` and push the tag:

   ```powershell
   git tag v0.4.0-rc.1 origin/main
   git push origin v0.4.0-rc.1
   ```

`.github/workflows/release.yml` then:

- refuses the tag unless it is exactly `v<Version>` and its commit is on `main`, before building anything. It also refuses a version that already has a release;
- reruns every CI check on the tagged commit: the build with warnings as errors, the unit tests, the offline integration tests, `scripts/pack-plugin.ps1`, and `claude plugin validate --strict` on the staged plugin;
- publishes a GitHub Release, marked as a prerelease when the version has a suffix, with notes generated from the commits. Its assets are the plugin zip, the bare `OneNoteMcp.exe`, and a `.sha256` for each. It downloads them back and checks the hashes.

**Then pin the catalog:** run the `sync` workflow in [sentienceAdjacent/plugins](https://github.com/sentienceAdjacent/plugins) with `gh workflow run sync -R sentienceAdjacent/plugins`. Its daily run is only a backstop: GitHub pauses scheduled workflows in a public repo after 60 days without activity (re-enable it from the Actions tab). It checks the new zip against its `.sha256` and commits the pin to that repo's `main`. The pin only moves forward by semver, so an older release never replaces a newer one.

If a check fails, nothing is published: fix the cause and release a new version. Don't move the tag, and don't upload a rebuilt zip under an existing version. If only the sync fails, the release itself is good: fix the cause and run `sync` again. Release builds are unsigned for now.

## Acknowledgements

Much of what this project knows about OneNote's COM API, its XML and its quirks comes from [OneMore](https://github.com/stevencohn/OneMore) by Steven M Cohn and its [developer documentation](https://onemoreaddin.com/developers/Setup.htm). OneMore is a long-lived OneNote add-in, and its write-ups of measured behaviour and API defects saved us a lot of rediscovery; `restore_from_journal`, for one, re-creates a page the way its Duplicate Page does. This project's code is its own. No OneMore source is included.

## License

Free to use, change and share for any noncommercial purpose, and inside any organization for its own internal use, including at work. Selling it, licensing it for a fee, or offering it as part of a paid product or service needs a separate license. Ask at license@sentienceadjacent.com. Forks and copies must keep the same terms and the attribution notice.

The exact terms are the [PolyForm Noncommercial License 1.0.0 plus an internal-use permission](LICENSE.md). Third-party components keep their own licenses ([notices](THIRD-PARTY-NOTICES.md)).
