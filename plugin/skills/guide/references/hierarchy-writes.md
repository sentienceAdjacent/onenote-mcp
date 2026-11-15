# Writing sections, section groups and notebooks: recipes

Read this before any hierarchy write: creating a section, section group or notebook, renaming or reordering one, or deleting a page, section or section group. The safety rules in `SKILL.md` (Writing) always apply. Each tool's description is the contract for its arguments and refusals; this file says which tool to use, in what order, what to tell the user, and how to undo it. Example names ("notebook A", "MCP Test", "Ideas") are placeholders.

## Before any hierarchy write

- Write only when the user asks for that change. Never create, rename, reorder or delete anything to try something out, to test a grant, or to make a dialog appear.
- Every tool here except `mcp__plugin_onenote_desktop__create_notebook` needs the notebook's write grant, as page writes do: the first write to a notebook without one shows the user a dialog asking them to type its name, and otherwise fails with `WriteNotGranted`. Relay its `userAction` and stop. Never run the grant command yourself, and never edit OneNoteMcp's `settings.json`.
- Get every ID from a fresh `mcp__plugin_onenote_desktop__get_hierarchy` read. IDs change when a notebook is closed and reopened, and a deleted page gets a new one.
- Writes queue: every OneNote MCP session on this PC shares one write lock, and a wait that runs out is `ComBusy` with nothing written.

## The basis for a hierarchy write

- **A section, section group or notebook:** `{lastModifiedTime}`, the attribute on its element in `mcp__plugin_onenote_desktop__get_hierarchy`. For a rename, read the node itself (`startNodeId` = its ID, `scope: self`); for a reorder, the parent (`scope: children`), whose element carries the parent's date.
- **A page you delete:** `{lastModifiedTime, pageHash}` from `mcp__plugin_onenote_desktop__get_page_content`'s `meta.basis`. Never send `elementHashes` with a delete (`InvalidArgument`).
- The check on a node is weak: a node's date doesn't always move when something inside it changes. Read just before the write anyway.
- Straight after another write (a create, a rename, a delete nearby, or a page write in a section), a node's date can move a few seconds late, so the next write may get `StaleWrite` once. After writing to a page, re-read its section just before you delete or rename the section. On `StaleWrite`, re-read and try once more with the new date; never resend the old basis. If it's stale again, tell the user what changed and ask.

## 1. Create a section, section group or notebook

Only when the user asks for a new one. Nothing is journaled: a create only adds.

**A section:** `mcp__plugin_onenote_desktop__create_section` with `parentId` = the notebook's or section group's ID and `name` as OneNote shows it (`.one` is added). Tell the user it's there; `meta.objectId` is its ID. With the warning `alreadyExisted`, a section of that name was already there: nothing was created, and `meta.objectId` is the existing one, so say so. A section group of that name in the same place is `InvalidArgument`: ask the user for another name.

**A section group:** `mcp__plugin_onenote_desktop__create_section_group`, with the same arguments and rules. OneNote can't create a section group whose name has a `.` in it, so such a name is refused before anything is written (`InvalidArgument`). For a name like "v1.2":

1. Tell the user OneNote can't create a group named with a `.` directly, then create it without the `.` (such as "v12" or "v1-2").
2. Ask whether you should rename it to "v1.2". If they decline, leave it as it is and tell them its name.
3. If they agree, read the new group (`mcp__plugin_onenote_desktop__get_hierarchy`, `startNodeId` = its `meta.objectId`, `scope: self`) and rename it with `mcp__plugin_onenote_desktop__rename_hierarchy_object` to "v1.2" (recipe 2). OneNote keeps the `.` in a rename, and it survives a sync and a restart. It is a write straight after the create, so it may get `StaleWrite` once (see The basis).
4. Check the result: with `readBackDiffers`, OneNote kept another name; tell the user, who can rename it in OneNote.

**A notebook:** `mcp__plugin_onenote_desktop__create_notebook` with `name`. It shows the user a dialog to choose the folder on this PC it goes in; the notebook gets a new folder named `name` there. `folder` only prefills the dialog: pass it only when the user named a place, and never pick or insist on one for them.

- `NotSupported`: the client can't show the dialog, or the place is OneDrive (OneDrive notebooks can't be created this way). Tell the user to create the notebook in OneNote.
- `Cancelled`: the user declined the dialog; nothing was created. Don't ask again unless they do.
- `FileExists`: a folder of that name is already there and isn't empty. Ask for another name or place.
- `InvalidArgument`: a network share, or a folder inside another notebook. Ask for another place.
- The new notebook gets the user's write grant automatically (`mcp__plugin_onenote_desktop__list_grants` shows it as `created`). With the warning `createdNotGranted` it was created without one: relay `userAction`; writing to it needs the user's grant.

**Undo:** a new section or section group is deleted like any other (recipe 4), if the user asks. A new notebook can't be deleted through these tools: the user closes it in OneNote and deletes its folder.

## 2. Rename a section, section group or notebook

1. Read the node: `mcp__plugin_onenote_desktop__get_hierarchy` with `startNodeId` = its ID and `scope: self`. Note its current name: it is the undo.
2. Call `mcp__plugin_onenote_desktop__rename_hierarchy_object` with `objectId`, `name` as OneNote shows it (a section without `.one`), and `basis` = `{lastModifiedTime}` from that read.
3. Tell the user the new name. With `unchanged`, it already had that name, and nothing was written.

- **A page** is renamed by its title: change the `one:Title` OE with `mcp__plugin_onenote_desktop__update_page_content` (`references/page-writes.md`, recipe 1).
- **A notebook:** only the name OneNote shows changes; its folder keeps its name. The write grant stays.
- **Refused:** a name another section or section group under the same parent uses (ignoring case), or another open notebook's name (`InvalidArgument`: ask for another; changing only the case of the node's own name is fine); a locked section (`SectionLocked`: the user unlocks it in OneNote); a section group in a notebook stored on OneDrive (`NotSupported`: the user renames it in OneNote); anything in the recycle bin (`ObjectNotFound`).
- **Read grants follow a location.** A rename that would move a section onto a location where the user holds a read grant is `InvalidArgument`: suggest another name, or the user revokes that grant first. Renaming a password-protected section ends its read grant, and so does renaming a section group that holds one: the user is asked again on the next read.
- **After the write:** `readBackDiffers` means OneNote kept another name; tell the user. `readBackFailed` means the rename happened but couldn't be checked: read the node before its next write.

**Undo:** rename it back to the name you noted, with a fresh read's basis. `mcp__plugin_onenote_desktop__restore_from_journal` can't restore a rename: its `meta.journalEntryId` only records the node as it was.

## 3. Reorder sections, section groups or pages

1. Read the parent: `mcp__plugin_onenote_desktop__get_hierarchy` with `startNodeId` = the notebook, section group or section and `scope: children`. Keep the list of child IDs in the order it shows: it is the undo.
2. Build `childIds`: every child ID from that read, each exactly once, in the new order. Leave out the recycle bin. Sections come before section groups, so sort sections among themselves and groups among themselves. Pages keep their level, and a page moves with its subpages: keep each subpage right after its own parent page.
3. Call `mcp__plugin_onenote_desktop__reorder_hierarchy_children` with `parentId`, `childIds` and `basis` = the parent's `{lastModifiedTime}` from that read.
4. Tell the user the new order. With `unchanged`, it already was in that order.

- A missing, extra, repeated or foreign ID is `InvalidArgument`, and nothing moves: re-read and rebuild the list. Nothing moves to another parent here.
- The list of open notebooks can't be reordered: tell the user they can drag notebooks in OneNote.
- **A reorder that moves only section groups may not be kept:** OneNote accepts it but keeps the old order (`readBackDiffers`). Tell the user, suggest dragging the groups in OneNote, and don't retry.
- `readBackDiffers` for anything else means OneNote kept another order or subpage level: tell the user what it kept. `readBackFailed`: the reorder happened but couldn't be checked; read the parent before its next write.

**Undo:** reorder back with the `childIds` you kept, with a fresh read's basis. `mcp__plugin_onenote_desktop__restore_from_journal` can't restore a reorder.

## 4. Delete to the recycle bin

When the user asks to delete a page, section or section group and doesn't say "permanently", use `mcp__plugin_onenote_desktop__delete_hierarchy_object`. It moves the object to its notebook's recycle bin, where the user can restore it in OneNote.

1. Read it: a page with `mcp__plugin_onenote_desktop__get_page_content` (basis `{lastModifiedTime, pageHash}` from `meta.basis`), a section or section group with `mcp__plugin_onenote_desktop__get_hierarchy` (basis `{lastModifiedTime}` from its element).
2. Call `mcp__plugin_onenote_desktop__delete_hierarchy_object` with `objectId` and that `basis`.
3. Tell the user where it went:
   - **A page** goes to Deleted Pages in the notebook's recycle bin, under a new ID (`meta.recycleBinId`; the warning `recycleBinIdMissing` means the new ID couldn't be told apart, and the delete still happened).
   - **A section** goes to the recycle bin under the same ID.
   - **A section group** dissolves: its sections go to the recycle bin one by one, and the group itself isn't there. An empty group leaves nothing in the bin.

- **Refused:** a notebook (`NotSupported`: the user closes it in OneNote and deletes its folder); a page in a password-protected section (`NotSupported`: such a page can only be deleted permanently, recipe 5, and only if the user asks for that); a page with subpages (`InvalidArgument`: ask whether the subpages go too, then delete them first, deepest first); a locked section, a page in one or a group holding one (`SectionLocked`: the user unlocks it in OneNote); anything already in the recycle bin (`ObjectNotFound`).
- `StaleWrite`: it changed since your read. Re-read and decide again with the user if what changed matters; straight after your own write, see The basis.

**Undo:** the user restores it from the recycle bin in OneNote; that is the way to suggest. For a page there is also `mcp__plugin_onenote_desktop__restore_from_journal` with the delete's `meta.journalEntryId`, which brings it back as a **new** copy while the original stays in Deleted Pages. A section's or section group's entry can't be restored that way: the tool says where it went.

## 5. Delete permanently

`mcp__plugin_onenote_desktop__permanently_delete_hierarchy_object` deletes a page, a section or an empty section group for good, skipping the recycle bin. Use it only when the user explicitly asks to delete something permanently, for good, or without the recycle bin.

1. Ask in the chat first, and wait for the user's yes before you call the tool: name what will go, and say it won't be in the recycle bin. Do this even when the user already said "permanently". The server's own check comes next; your question never replaces it.
2. Read it, as in recipe 4: a page's `meta.basis` (`{lastModifiedTime, pageHash}`), or a section's or group's `{lastModifiedTime}`.
3. Call it with `objectId` and `basis`. Unless the user allowed permanent deletes in that notebook, a dialog asks them to type the object's name for this one delete; nothing is stored, so the next permanent delete asks again. A section's backup can take a while.
4. Tell the user what was kept, and give them the IDs, since no tool lists them later:
   - **A page:** its journal entry, `meta.journalEntryId`.
   - **A section:** a OneNote backup file, `meta.backupFile` (a file name in the `backups` folder of the recovery journal; `meta.journalEntryId` is its entry), and one journal entry per page, `meta.pageEntryIds`, in section order.
   - **An empty section group:** a journal record of it, `meta.journalEntryId`.

- **`PermanentDeleteNotGranted`:** the user declined or cancelled the dialog, typed another name, or the client can't show dialogs. Nothing was deleted. Relay `userAction`: it holds the command the user can run to allow permanent deletes in that notebook. Never run it yourself, and stop. Retry only after the user says they've granted it, or wants to confirm in the dialog again.
- **`BackupFailed`:** the copy couldn't be saved, so nothing was deleted. An empty section always gets it, because OneNote can't back one up: offer to delete it to the recycle bin instead (recipe 4), or, if it is already in the recycle bin, tell the user to empty the bin in OneNote. Otherwise relay `userAction`. Don't retry on your own.
- **Refused with `NotSupported`:** a notebook (the user closes it in OneNote and deletes its folder); a password-protected section (its backup would be unencrypted: the user deletes it to the recycle bin in OneNote, then empties the bin); a section group that holds anything (delete what's inside first, each with its own confirmation, or the user deletes the group in OneNote); a page already in the recycle bin, and Deleted Pages (the user empties the recycle bin in OneNote). A page with subpages is `InvalidArgument`. In each case relay `userAction`.
- A section already in the recycle bin can be deleted permanently, with the same backup as a live one.
- A page in a password-protected section you have no read grant for: its basis is `{lastModifiedTime}` only. Without the notebook's permanent-delete grant no dialog can name it, so the call fails with `PermanentDeleteNotGranted`: relay `userAction`, which also offers reading the page first (that asks the user for the section's read grant), after which the dialog can ask them.

**Undo:** for each page, `mcp__plugin_onenote_desktop__restore_from_journal` with its entry (`meta.journalEntryId` for a page, each of `meta.pageEntryIds` for a section) brings it back as a new page. With its section gone, it goes into the section at the same group/name path, else into a `Recovered` section (warning `restoredElsewhere`). The section's backup file can't be restored with a tool. The user can copy it out of the journal's `backups` folder and open the copy in OneNote (File > Open); that isn't proven to bring the section back, so don't promise it. Never suggest opening it where it is: the journal's pruning and `uninstall --purge` delete it. An empty group can be created again with `mcp__plugin_onenote_desktop__create_section_group`.

## When a hierarchy write fails

- **`outcomeUnknown: true`:** the write may or may not have happened. Before anything else, re-read with `mcp__plugin_onenote_desktop__get_hierarchy`: the parent's children (`scope: children`) after a section or section group create, a reorder or a delete; the notebooks (no `startNodeId`, `scope: notebooks`) after `create_notebook`; the node (`scope: self`) after a rename. Tell the user what you found, and redo the change only if it's absent and they still want it. Never retry a write blindly.
- **Without `outcomeUnknown`:** nothing was written. Follow the error rows in `SKILL.md`.
- **`ComBusy`:** another write held the lock, or OneNote was busy; nothing was written. Retry once. If it's busy again, tell the user and wait for them.

## What this release can't do

Moving a page, section or section group to another place, deleting a notebook, deleting images or ink, creating a notebook on OneDrive, and exports. Tell the user this release can't do it, and that they can do it in OneNote. Never work around it (`SKILL.md`, Writing).
