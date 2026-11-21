# Writing sections, section groups and notebooks: recipes

Read this before any hierarchy write: creating a section, section group or notebook, renaming or reordering one, deleting a page, section or section group, moving one, merging sections, restoring from the recycle bin, or changing a page's level. The safety rules in `SKILL.md` (Writing) always apply. Each tool's description is the contract for its arguments and refusals; this file says which tool to use, in what order, what to tell the user, and how to undo it. Example names ("notebook A", "MCP Test", "Ideas") are placeholders.

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
- **A password-protected section's pages** need the section's read grant as well as the write grant. Step 1's read asks the user for it; if they decline, you can't list its pages, so don't reorder. Without it the reorder is `ReadNotGranted`, whatever `childIds` holds, and this tool never shows the dialog: if the user wants it, read the section again (step 1), then retry. Reordering a notebook's or section group's sections needs no read grant, protected sections included.
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

**Undo:** restore it with `mcp__plugin_onenote_desktop__restore_from_recycle_bin` (recipe 8), or the user restores it in OneNote. For a page there is also `mcp__plugin_onenote_desktop__restore_from_journal` with the delete's `meta.journalEntryId`, which brings it back as a **new** copy while the original stays in Deleted Pages. A section's or section group's entry can't be restored that way: the tool says where it went.

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

## 6. Move a page, section or section group

`mcp__plugin_onenote_desktop__move_hierarchy_object` moves a page to another section, or a section or section group to another notebook or section group, in the same notebook or another.

1. Read where it is: for a page, its section with `mcp__plugin_onenote_desktop__get_hierarchy` (`scope: children`), whose `one:Page` element carries the page's `lastModifiedTime`; for a section or group, its parent (`scope: children`). Note where it was: that is the undo.
2. Read the target (`scope: children`) for its ID and, if the user wants it somewhere particular, the position (from 1; for a page, before a top-level page).
3. Call it with `objectId`, `parentId` (a section for a page; a notebook or section group for a section or group), `position` if needed (left out, it goes last) and `basis` = `{lastModifiedTime}` of the item from step 1.
4. Tell the user where it is now. **A page gets a new ID** (`meta.objectId`): use that one from now on. A section or group keeps its ID.

- **Grants:** both notebooks need the user's write grant (a dialog may ask for each). Moving a page into a password-protected section needs only the write grant, unless you pass `position`, which also needs that section's read grant, held when the call starts (`ReadNotGranted`; leave `position` out to put it last).
- **Subpages stay behind:** a page moves alone. With `subpagesLeftBehind`, its subpages stayed in the old section as top-level pages: tell the user, and move them too if they want.
- **Refused with `NotSupported`:** a page in a password-protected section, a protected section, or a group holding one (locked or not): its content can't leave it this way; the user moves it in OneNote. A section or group leaving a cloud notebook for another notebook's section group: move it to that notebook's root first, then into the group. A group OneNote can't move whole: move its sections one by one.
- **Refused with `InvalidArgument`:** a target of the wrong kind, its own parent, a group into itself, a target that already holds a section of that name, a position past the end or at a subpage, or a section that would land where a read grant is stored. Anything in or into the recycle bin is `ObjectNotFound` (restoring is recipe 8). A locked target is `SectionLocked`.
- **After the move:** a section or group move touching a cloud notebook waits 10 s (`settled`): ask the user to check it synced. `readBackDiffers` means it wasn't confirmed where it was sent (for a page, `meta.objectId` may be the old ID): read both parents. `readGrantAtLocation`: a read grant the user holds for the place where the section landed now covers it, so it can be read without asking; tell the user. `StaleWrite`: re-read and decide again.

**Undo:** move it back with a fresh read's basis (a page by its new ID). Both parents are saved to the recovery journal (`meta.journalEntryIds`), but `mcp__plugin_onenote_desktop__restore_from_journal` can't restore them.

## 7. Merge one section into another

`mcp__plugin_onenote_desktop__merge_sections` copies every page of a source section to the end of a destination section, in the same notebook or another. The source stays as it was: nothing is deleted.

1. Confirm with the user which section's pages go into which, and that the source stays. Merging twice makes a second set of copies, and the server can't tell, so never merge again because a result looked odd: read the destination first.
2. Read the source's parent (`scope: children`) for the source's `{lastModifiedTime}`.
3. Call it with `sourceId`, `destinationId` and `basis` = that date.
4. Tell the user how many pages were copied (`meta.pageIds`, the copies' new IDs in order) and that the source is unchanged. If they want the source gone, delete it to the recycle bin afterwards (recipe 4), on their say-so.

- **Grants:** both notebooks' write grants. A password-protected destination needs only the write grant: the copies land encrypted, and `meta.pageIds` is left out.
- **Refused:** a password-protected source, locked or not (`NotSupported`); anything in the recycle bin (`ObjectNotFound`); not a section, the same section twice, or an empty source (`InvalidArgument`); a locked destination (`SectionLocked`).
- **After the merge:** `settled` in a cloud notebook (ask the user to check it synced); `readBackDiffers` (the page count differs, or the source changed): read both sections and tell the user.
- **`outcomeUnknown: true`:** some copies may have landed. Read the destination with `mcp__plugin_onenote_desktop__get_hierarchy` (`scope: pages`), tell the user what is there, and with their say-so delete partial copies before any new try.

**Undo:** delete the copies (`meta.pageIds`) to the recycle bin (recipe 4). The destination's journal entry can't be restored with `mcp__plugin_onenote_desktop__restore_from_journal`.

## 8. Restore from the recycle bin

`mcp__plugin_onenote_desktop__restore_from_recycle_bin` brings a soft-deleted page or section back out of its notebook's recycle bin.

1. Find it: `mcp__plugin_onenote_desktop__get_hierarchy` on the notebook with `scope: pages` shows its recycle bin, with deleted pages under Deleted Pages and deleted sections directly in the bin. If more than one item there matches what the user asked for (same names), list them, with the section each was in and when it was deleted, and ask which before restoring.
2. Call it with `objectId` = the item's ID there. Leave `parentId` out to put it back where it was deleted from; pass `parentId` (and `position`) only when the user names another place.
3. Tell the user where it is. **A page comes back with a new ID** (`meta.objectId`), as a top-level page; a section keeps its ID.

- **A section group** isn't restored whole: OneNote bins its sections one by one. Restore each section, into a group re-created with `mcp__plugin_onenote_desktop__create_section_group` if the user wants it.
- **It doesn't know where it came from** (deleted in OneNote, deleted with its group, or older than the journal keeps entries, 30 days by default) or that place is gone: it is refused. Ask the user where it should go and pass `parentId`.
- **Several candidates:** an item whose recycle-bin ID still matches its delete's journal entry is restored directly. Only when that ID has gone stale (the notebook was reopened, or the section protected) is it matched by name; if several deletes match, nothing is restored, and the error lists each one's entry ID, deletion time and original parent, marking one an earlier restore already used "already restored" (tell the user; it may still be the one they mean, and an unmarked one isn't proof it was never used). Ask the user which, then retry with `entryId`. `restoredByName` means it was matched by name: check with the user that it's the item they meant.
- **A protected section** (password-protected, or under the opened-copies folder, an export root or the backups folder) goes back only to where it was deleted from, still protected (a password-protected one also locked); another `parentId` is `InvalidArgument`, and if that place is gone the user restores it in OneNote (`ObjectNotFound`).
- **A section to another notebook** is restored in its own notebook first, then moved (recipe 6). With `restoredNotMoved`, the move was refused or failed: it is restored in its own notebook. Tell the user, and move it if they still want. If the move's outcome is unknown, `meta.parentId` is absent: read both notebooks first.
- **Grants:** the notebooks' write grants; a page at a `position` in a protected section also needs its read grant.

**Undo:** delete it again (recipe 4).

## 9. Make a page a subpage, or add one

**An existing page:** `mcp__plugin_onenote_desktop__set_page_level` with `pageId`, `level` (1, 2 or 3; absolute) and `basis` = the page's `{lastModifiedTime}` from its section's `mcp__plugin_onenote_desktop__get_hierarchy` read (`scope: children`). Its subpages move by the same amount. Tell the user its new level; `unchanged` means it already had it.

- A level is refused (`InvalidArgument`) when it wouldn't sit under a parent page: the section's first page must be level 1, a page can be at most one level deeper than the page before it, its subpages must stay within 1–3, and an outdent can't leave a later page more than one level deeper. Explain which neighbour is in the way and, with the user's OK, change that one first, or reorder the pages (recipe 3) so the page follows its new parent.
- In a password-protected section it also needs that section's read grant, held when the call starts (`ReadNotGranted`).
- `readBackDiffers`: OneNote kept other levels or another order; read the section and tell the user.

**A new subpage:** `mcp__plugin_onenote_desktop__create_page` with `parentPageId` = a level 1 or 2 page of that section. The new page goes after the parent's existing subpages, one level deeper. With `subpageNotPlaced`, it was created but not placed: it is last in the section at level 1 (or, if the message says so, may or may not have been placed: read the section first). Move it under its parent with `mcp__plugin_onenote_desktop__reorder_hierarchy_children` and then `set_page_level`, with the user's OK. In a password-protected section it needs that section's read grant (leave `parentPageId` out for a top-level page).

**Undo:** set the level back with a fresh basis; a new subpage is deleted like any page (recipe 4). The section is journaled (`meta.journalEntryId`), but `mcp__plugin_onenote_desktop__restore_from_journal` can't restore it.

## When a hierarchy write fails

- **`outcomeUnknown: true`:** the write may or may not have happened. Before anything else, re-read with `mcp__plugin_onenote_desktop__get_hierarchy`: the parent's children (`scope: children`) after a section or section group create, a reorder or a delete; the notebooks (no `startNodeId`, `scope: notebooks`) after `create_notebook`; the node (`scope: self`) after a rename; both parents after a move; the destination (`scope: pages`) after a merge; the target and the recycle bin after a restore; the section (`scope: children`) after a level change. Tell the user what you found, and redo the change only if it's absent and they still want it. Never retry a write blindly.
- **Without `outcomeUnknown`:** nothing was written. Follow the error rows in `SKILL.md`.
- **`ComBusy`:** another write held the lock, or OneNote was busy; nothing was written. Retry once. If it's busy again, tell the user and wait for them.

## What this release can't do

Deleting or moving a notebook, deleting images or ink, and creating a notebook on OneDrive. Tell the user this release can't do it, and that they can do it in OneNote. Never work around it (`SKILL.md`, Writing).
