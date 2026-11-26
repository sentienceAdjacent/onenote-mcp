# What OneNote shows and holds open: recipes

Read this before an app-state call: `mcp__plugin_onenote_desktop__get_windows`, `mcp__plugin_onenote_desktop__navigate_to`, `mcp__plugin_onenote_desktop__navigate_to_url`, `mcp__plugin_onenote_desktop__quick_filing`, `mcp__plugin_onenote_desktop__sync_hierarchy`, `mcp__plugin_onenote_desktop__close_notebook` or `mcp__plugin_onenote_desktop__open_notebook`. The rules in `SKILL.md` (User-visible actions and limits) always apply. Each tool's description is the contract for its arguments, refusals and warnings; this file says when to call it, in what order, what to tell the user, and what can go wrong. Example names ("notebook A", "MCP Test", "page P") are placeholders.

## Before any app-state call

- **Only when the user asks** for that action. These tools act on, or read, the user's OneNote window and notebook list, so never call one on your own: not to help yourself read something, not to tidy up after a write, and not to check one. For `get_windows`, the user referring to what they're looking at ("this page", "the page I'm on") counts as asking.
- **No grant of any kind.** None of them needs a read or write grant, and none gives one. They change no page content, so there is nothing to undo in the journal.
- **They may bring OneNote forward.** The user-visible ones (`navigate_to`, `navigate_to_url`, `quick_filing`, `close_notebook`, `open_notebook`) change what OneNote shows or holds open, and may bring it to the front. Say so before you call one, and never promise that it will, or won't, take focus.
- **Never retry on your own.** None is retried automatically. On `outcomeUnknown: true`, ask the user what OneNote shows (or, after a close or an open, re-read the open notebooks) before calling again.

## 1. Which page am I on?

**When.** The user asks which page they're on, or refers to what they're looking at ("summarise this page", "add a line to the page I'm on").

**The call.** `mcp__plugin_onenote_desktop__get_windows`, no arguments. It changes nothing on screen. In `meta.windows`, the window with `current: true` is the one the user is in, and its `pageId` is "this page". If they mean another window ("my quick note", "the other window"), pick it by `sideNote` or ask which. Then read the page with `mcp__plugin_onenote_desktop__get_page_content` only if the user wants its content.

**Tell the user** the page's name (from a `get_hierarchy` read of its section, or its title from a content read they asked for), and its section and notebook.

**What can go wrong.**

- An empty `meta.windows`: no OneNote window is open. Say so; never open one.
- No `pageId` with the warning `readNotGranted`: the page is in a password-protected section the user hasn't granted reads of. Name the section only, and say they can grant a read (see Password-protected sections in `SKILL.md`).
- No `pageId` without that warning: the section is locked, or the window shows no page. Name what the window does show.

## 2. Show it in OneNote

**When.** The user asks to see a page, section or notebook in OneNote ("show page P in OneNote"), or agrees to it in **Missing images** (`SKILL.md`).

**The call.** `mcp__plugin_onenote_desktop__navigate_to` with the object's ID as `hierarchyId` (and an OE's `objectID` as `pageContentObjectId` to select it). Without `window` it uses OneNote's first window, which isn't always the one the user is in. When the user names a window ("in the other window", "in my quick note"), call `mcp__plugin_onenote_desktop__get_windows` first and pass that window's `handle` as `window`.

**Tell the user** what you showed, and where.

**What can go wrong.** A `window` handle no open window has is `InvalidArgument` and nothing is shown: relay it, and read the windows again only if the user still wants it. A stale ID is `ObjectNotFound`: re-read the hierarchy. On `outcomeUnknown`, ask the user what OneNote shows before calling again.

## 3. Follow this link

**When.** The user asks you to follow or open a `onenote:` link: one they pasted, or one you got from `mcp__plugin_onenote_desktop__get_hyperlink_to_object` and they asked you to open.

**The call.** `mcp__plugin_onenote_desktop__navigate_to_url` with the link as `url` (and `window` from `get_windows` when the user names a window). It follows links into open notebooks stored online (OneDrive or SharePoint) only. A link into a notebook on this PC or a network share is refused (`InvalidArgument`, nothing shown): find the object's ID (`get_hierarchy`, `find_pages`) and offer `mcp__plugin_onenote_desktop__navigate_to` instead, which you call only if the user says yes. Web links, links into a closed notebook and a section group's folder are refused too: relay `userAction`. Never turn a web link into a `onenote:` link to get past a refusal.

**Tell the user** where OneNote landed. Every `ok` carries one of three landing warnings, each saying "OneNote's current page is …":

- `landed`: OneNote is on what the link names. Tell the user it's open. With `landedInRecycleBin` beside it, what OneNote opened is in the notebook's Recycle Bin: say it was deleted, and offer `mcp__plugin_onenote_desktop__restore_from_recycle_bin` (restore only if they say so).
- `landedElsewhere`: OneNote took the link but shows something else (the target may be gone, or a locked section shows no page). Tell the user, relay `userAction`, and don't retry the link.
- `landingUnconfirmed`: where it landed couldn't be checked. Ask the user whether OneNote shows what they wanted.

**What can go wrong.** `ObjectNotFound` straight after another `navigate_to_url`: its `userAction` says to wait a second and try once more, the one retry allowed. Otherwise the target is gone. On `outcomeUnknown`, ask the user what OneNote shows before calling again.

## 4. Let me pick a section

**When.** The user asks to choose a place themselves ("let me pick the section", "I'll show you where"), usually for a write they asked for. Never to find a section yourself: `get_hierarchy` and `find_pages` do that.

**The call.** `mcp__plugin_onenote_desktop__quick_filing`, with a short `title` (and `description`) saying what the pick is for ("Where should the new page go?"). Tell the user the picker is on their screen. It may bring OneNote forward but doesn't block it, and it waits up to 300 s. A pick returns the section's ID in `meta.objectId` and its `one:Section` element as `xml`. Then make the write the user asked for with that ID, under that write's own reference (for example `create_page` in `references/page-writes.md`).

**Tell the user** which section they picked (its `name`), and then the result of the write.

**What can go wrong.**

- `pickerCancelled`: they pressed Cancel or Esc. Nothing was chosen; don't open the picker again unless they ask.
- `pickerLeftOpen`: no answer in 300 s. The picker may still be on screen, and a choice made in it now is ignored. Ask them to close it, and call again only if they still want to choose.
- `ComBusy`: a picker of this server's is already waiting. Ask the user to answer or cancel it.
- `outcomeUnknown` or a `Cancelled` wait: a picker may be on screen. Ask the user before calling again.

## 5. Sync it now

**When.** The user asks for a sync ("sync notebook A now"). Never on your own, after a write or to check one.

**The call.** `mcp__plugin_onenote_desktop__sync_hierarchy` with the notebook's, section group's, section's or page's ID as `hierarchyId`. It changes nothing on screen.

**Tell the user** the sync was requested, not done: every `ok` carries `syncRequested`, because OneNote returns before any sync runs and never says when one finishes. They can check it in OneNote under File > Info > View Sync Status.

**What can go wrong.** A closed notebook's old ID, or an unknown one, is `ObjectNotFound`: re-read the hierarchy. `ModalDialogOpen`: ask the user to close the dialog in OneNote, then retry once they say so.

## 6. Close and reopen a notebook

**When.** The user asks to close a notebook they name, or to open one again. Never close a notebook to tidy up after your own work, and never one the user didn't name.

**Close.** `mcp__plugin_onenote_desktop__close_notebook` with the notebook's current ID from `mcp__plugin_onenote_desktop__get_hierarchy` (a section, group or page ID is `InvalidArgument`). It asks OneNote to sync the notebook and waits about 10 s first, so tell the user it takes a little while. It never forces the close. Its `ok` carries `notebookClosed`:

- Tell the user it's closed, and that OneNote's view may go blank if it showed that notebook.
- Every ID in it changes when it is opened again: never reuse them. Its read and write grants are kept, matched by its path.
- Relay the warning's `userAction` on how to reopen it. For a notebook on this PC, keep `meta.path` for the reopen.

**Reopen a local notebook.** When the user asks, `mcp__plugin_onenote_desktop__open_notebook` with `path` = the close's `meta.path` (or the folder the user names). It never creates a notebook. Use its `meta.objectId`, the notebook's new ID, and re-read the hierarchy for the new IDs inside it. Its sections may list a few seconds late (`notebookListMayLag`): read again shortly if they're missing. With `alreadyExisted` it was already open, and `meta.objectId` is its current ID: tell the user. The user can also reopen it in OneNote (File > Open > Browse, then the notebook's folder).

**A notebook stored online** (OneDrive or SharePoint) the user reopens in OneNote, under the account that stores it (File > Open): the close's `userAction` says so. If they ask you to open it by its URL, decline and say why: this server opens only local folders, and `open_notebook` refuses a URL or a OneDrive folder (`InvalidArgument`). Once they've reopened it, re-read the hierarchy for its new ID. If it comes back with its sections "not available yet", or a bar saying it isn't syncing, ask the user to close it and open it again in OneNote; that cleared it the one time it was seen.

**What can go wrong.**

- A close that fails before OneNote closes anything (the sync or the wait failed) leaves the notebook open, without `outcomeUnknown`. When OneNote refuses the close, relay `userAction`: let the notebook finish syncing and try again, or the user closes it in OneNote.
- On `outcomeUnknown` from either call, re-read the open notebooks (`get_hierarchy`, scope `notebooks`) before anything else.
- `open_notebook` refuses a URL, a network, device or relative path, a file (`.onepkg` goes to `mcp__plugin_onenote_desktop__open_package`), a OneDrive folder and a folder without a `.onetoc2`, with nothing sent to OneNote: relay `userAction`.
