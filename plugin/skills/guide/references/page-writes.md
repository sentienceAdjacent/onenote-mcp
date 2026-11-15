# Writing pages: recipes

Read this before any page write; for creating, renaming, reordering or deleting pages, sections, section groups and notebooks, read `references/hierarchy-writes.md`. The safety rules in `SKILL.md` (Writing) always apply. Each write tool's description is the contract for its arguments and for the XML it accepts; this file says which tool to use, in what order, and what to tell the user. Example names ("notebook A", "MCP Test", "Trip plan") are placeholders.

## Before any write

- Write only when the user asks for that change. Never write to try something out, to test a grant, or to make a dialog appear.
- The page's notebook needs the user's write grant. The first write to a notebook without one shows the user a dialog that names the notebook and its path and asks them to type its name. If they decline, or the client can't show dialogs, the write fails with `WriteNotGranted`: relay its `userAction` (it holds the terminal command) and stop. Never run that command yourself, and never edit OneNoteMcp's `settings.json`. `mcp__plugin_onenote_desktop__list_grants` shows the notebooks already granted (kind `writeNotebook`).
- Writes queue. Every OneNote MCP session on this PC shares one write lock, so a write may wait for another session's write to finish. A wait that runs out fails with `ComBusy`, and nothing was written.
- A write to a page the user is editing in OneNote at that moment can disturb their edit. Another OneNote add-in (OneMore) records that a write while the user's selection spans an equation can corrupt the equation; this server hasn't tested it. If the user says they're working on the page, ask them to finish or click away from it first.

## The basis

- `mcp__plugin_onenote_desktop__get_page_content` returns `meta.basis`: the page's date and a hash of each Outline and OE. Pass it unchanged as `basis`. The server compares it with the page as it is now: the date, and the hash of each Outline you send with an `objectID` (and of any OE you send outside an Outline, such as the title's). A change since your read fails with `StaleWrite`, and nothing is written.
- Every successful write returns the page's new `meta.basis`. Use it for the next write to that page instead of re-reading.
- **Always use the newest basis you have.** After a write, OneNote can change Outlines you didn't send: resending an Outline's `one:Position` re-stacks the page's Outlines (`z`), which changes the others' hashes. The write's own `meta.basis` already has the new hashes; an older basis then fails with `StaleWrite` although nobody else changed the page.
- Never edit a basis, reuse one from another page, or build one, except to merge the parts of an oversized page (below). Without `basis`, the write is checked against the page's current version (read internally; nothing of it is returned), not against what you last read, with the warning `noBasis`. Leave it out only for a page you've just created (recipe 7), or in a password-protected section you can't read (recipe 5).
- A basis that lacks a hash the write needs makes the server skip that check, with the warning `partialBasis`.
- **An oversized page** (read part by part with `mcp__plugin_onenote_desktop__read_result`): each part's `meta.basis` has the page's date and the hashes of the Outlines and OEs wholly inside that part. For a write, merge the `elementHashes` from the parts of the same `resultId` into one basis that covers every Outline you send.

## Page XML for a write

- The root is `one:Page` with the page's `ID` and the namespace `xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote"`. No DOCTYPE: a DOCTYPE, or XML that doesn't parse or fit the schema, fails with `InvalidXml`.
- Send only the Outlines you change or add. Outlines you don't send are kept as they are.
- An Outline sent with its `objectID` replaces that Outline's whole OE list. So send every OE `mcp__plugin_onenote_desktop__get_page_content` shows under it, nested OEs and OEs in table cells included, each with its `objectID`. Copy the OEs you don't change exactly as you read them, attributes and children included, and change only what the user asked for. An OE left out without being named in `removeObjectIds` fails with `InvalidArgument`: it isn't deleted.
- An Outline or OE sent without an `objectID` is new, and is added.
- An OE can't move between Outlines (`InvalidArgument`): send it without its `objectID` where it should go, and name it in `removeObjectIds` where it was.
- `one:T` holds HTML in CDATA. Use only OneNote's own formatting: `update_page_content`'s description lists the tags, attributes and links it accepts. Anything else, and every `one:HTMLBlock`, fails with `InvalidArgument`; rewrite the text with allowed formatting, or as plain text. Write `<` and `&` in the text as `&lt;` and `&amp;`.
- An attachment already on the page (`one:InsertedFile`): send it exactly as your latest read shows it. Unchanged paths raise no dialog.

Changing the second of two paragraphs in one Outline:

```xml
<one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="{page ID}">
  <one:Outline objectID="{Outline ID}">
    <one:OEChildren>
      <one:OE objectID="{first OE ID}"><one:T><![CDATA[First paragraph, unchanged]]></one:T></one:OE>
      <one:OE objectID="{second OE ID}"><one:T><![CDATA[Second paragraph, changed]]></one:T></one:OE>
    </one:OEChildren>
  </one:Outline>
</one:Page>
```

## 1. Change text on a page

1. Find the page (the reading recipes in `SKILL.md`) and read it with `mcp__plugin_onenote_desktop__get_page_content`, leaving `includeBinary` false.
2. Build the XML as above: each Outline you change, whole, with every OE and its `objectID`. To delete paragraphs (OEs) from that Outline, leave them out and name their `objectID`s in `removeObjectIds`; an OE's nested OEs go with it. To change the title, send `one:Title` with its OE and that OE's `objectID`.
3. Call `mcp__plugin_onenote_desktop__update_page_content` with `pageId`, `xml` and `basis` = the read's `meta.basis`, unchanged.
4. Check `ok`, then the warnings (`SKILL.md`, Errors and warnings): `readBackDiffers` names an attribute OneNote didn't apply or an OE it re-created under a new `objectID`, and `imageLost` an image gone from a sent Outline.
5. Tell the user what changed, and that the change can be undone (recipe 6). Keep the result's `meta.journalEntryId`.
6. For another change to the same page, use the result's `meta.basis` (the newest one).

**`StaleWrite`:** the page changed since your read; the user or another program edited it. Re-read the page, then redo the change on the new content with the new `meta.basis`. If what changed touches the user's change (the same paragraph, or text the change depends on), tell the user what is different and ask before writing. Never resend the old XML or the old basis.

## 2. Add content

- **A paragraph in an existing Outline:** send that Outline whole (recipe 1), with the new OE, without an `objectID`, where it should go.
- **A new block:** send a `one:Outline` with no `objectID`, holding `one:OEChildren` and its OEs, none with an `objectID`. It's added beside the other Outlines, which are kept. Pass the read's `meta.basis` as usual.

```xml
<one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="{page ID}">
  <one:Outline>
    <one:OEChildren>
      <one:OE><one:T><![CDATA[A new paragraph]]></one:T></one:OE>
    </one:OEChildren>
  </one:Outline>
</one:Page>
```

## 3. Remove content

- **A whole block (an Outline):** `mcp__plugin_onenote_desktop__delete_page_content` with `pageId`, `objectId` = that `one:Outline`'s `objectID`, and `basis`. It deletes one Outline per call; for several, call it again with the result's `meta.basis`. `readBackDiffers` means the Outline is still there.
- **One or more paragraphs (OEs):** `mcp__plugin_onenote_desktop__update_page_content`: resend their Outline without them and name them in `removeObjectIds` (recipe 1). `mcp__plugin_onenote_desktop__delete_page_content` on an OE fails with `InvalidArgument`.
- **The title, an image or ink:** `mcp__plugin_onenote_desktop__delete_page_content` can't delete them (`InvalidArgument`). Ask the user to delete an image or ink in OneNote.
- In a password-protected section, `mcp__plugin_onenote_desktop__delete_page_content` also needs the section's read grant. Without it, it fails with `ReadNotGranted` and never shows a dialog. Ask the user whether they want the section read; if they do, read the page with `mcp__plugin_onenote_desktop__get_page_content` (its dialog asks them), then delete with that read's `meta.basis`.
- Every delete saves the page to the journal first, so it can be undone (recipe 6).

## 4. Add an image or attachment

- **A file on disk goes by its full local path.** For an image, send a `one:Image` holding `one:File` with `path` set to the file's full path; for an attachment, a `one:InsertedFile` with `pathSource` set to it. Put either in a new OE with no `objectID`, as in recipe 2, and pass the read's `meta.basis` as usual.
- **The user confirms each new path.** A file path that isn't already on that element shows the user a dialog naming the file, which they accept or decline. A network, device or relative path fails with `InvalidArgument`: ask the user for a local copy.
- **Declined:** the write fails with `WriteNotGranted`, and nothing is written. Tell the user and stop. Don't send the file another way (inline, or by another path) unless the user asks for that.
- **A client that can't show dialogs** also gets `WriteNotGranted` for a new path. Tell the user; a small image can go inline instead (below) if they agree.
- **OneNote copies the file into the notebook** when it inserts it: a read afterwards shows OneNote's own copy (an attachment's `pathCache` is a `%TEMP%\{GUID}.bin` file), not the source path. So the page keeps the image or attachment after the original file is moved or deleted. That's expected.
- **Inline `one:Data` (base64)** only for a small image you already have as data, such as one copied from another page with `mcp__plugin_onenote_desktop__get_binary_page_content`, or on a client that can't show dialogs. Never read a large file and write it out as base64 to avoid the dialog.
- An image or attachment already on the page: send it exactly as your latest read shows it. Its unchanged paths raise no dialog.
- Afterwards, `imageLost` means a sent Outline holds fewer images than you sent: tell the user.

```xml
<one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" ID="{page ID}">
  <one:Outline>
    <one:OEChildren>
      <one:OE><one:Image><one:File path="C:\pics\a.png" /></one:Image></one:OE>
    </one:OEChildren>
  </one:Outline>
</one:Page>
```

## 5. Write in a password-protected section

- Adding content there with `mcp__plugin_onenote_desktop__update_page_content` needs only the notebook's write grant, and a write never shows the read-grant dialog. Changing or removing existing content, and every `mcp__plugin_onenote_desktop__delete_page_content`, also needs the section's read grant (below, and recipe 3).
- **With the section's read grant** (the user let you read it), write as anywhere else: recipes 1 to 3.
- **Without it, you can only add.** Send only new content: Outlines or OEs with no `objectID` at all, no `removeObjectIds`, and no `basis` (a basis there may carry only `lastModifiedTime`, and nothing gives you that without a read). Anything else, such as an `objectID`, a hash in the basis or a `removeObjectIds` entry, fails with `ReadNotGranted` before the page is read. The section's pages aren't listed without the grant either, so in practice this means adding to a page you've just created there (recipe 7).
- Such a write succeeds with the warnings `noBasis` and `readBackWithheld`, and no `meta.basis`: nothing of the page comes back, and its other warnings carry counts only. Tell the user what you added, and that you can't see the rest of the page.
- To change or remove existing content there, ask the user whether they want the section read. If they say yes, read the page with `mcp__plugin_onenote_desktop__get_page_content` (its dialog asks them), then follow recipes 1 to 3.
- A locked section fails with `SectionLocked`: ask the user to unlock it in OneNote.
- The journal entry of such a page is encrypted for the user's Windows account. A restore puts it back only into that same section, while the section is still password-protected.

## 6. Undo a change

**Your own recent edit, with the old content still in hand: write it back.** Send the previous content as a normal edit (recipe 1 or 3), with the newest `meta.basis`. The page stays where it is, nothing is duplicated, and the write is journaled like any other. Check the result: the returned `pageHash` should match the one from before your edit; if it doesn't, read the page and tell the user what still differs.

**Otherwise restore from the journal:** when you no longer have the old content (a new session, or an earlier change), after a delete you can't rebuild exactly, or when the user wants the old version as a copy beside the current one.

1. Call `mcp__plugin_onenote_desktop__restore_from_journal` with `entryId` = the `meta.journalEntryId` of the `update_page_content` or `delete_page_content` to undo, or of a page's `delete_hierarchy_object` or `permanently_delete_hierarchy_object` (for a section's permanent delete, each of its `meta.pageEntryIds`; `references/hierarchy-writes.md`), or the entry ID a failed write's message names. Each entry is the page as it was just before that write, so to undo several changes to one page, restore the entry of the earliest.
2. Tell the user it's a **new** page: the restore never overwrites, so the edited page stays as it is, and they can delete whichever copy they don't want, in OneNote or, if they ask you, with `mcp__plugin_onenote_desktop__delete_hierarchy_object` (`references/hierarchy-writes.md`, recipe 4).
3. Say where it went. It goes into the page's own section if that still exists; else into the section at the same group/name path in the notebook; else into a section named `Recovered` in the notebook (the warning `restoredElsewhere` says which). `meta.pageId` is the new page, and `meta.parentId` its section: name it with `mcp__plugin_onenote_desktop__get_hierarchy` (`startNodeId` = that ID, `scope: self`).
4. `binaryMissing` counts what didn't come back: images that weren't on this PC when the entry was saved, and attachments whose temporary copy OneNote has since removed. Tell the user which kinds are missing.

- A page from a password-protected section goes back only into that section, while it is still protected: `SectionLocked` while it's locked; `ObjectNotFound` if it's gone or no longer protected (relay `userAction`, which names the entry file).
- The notebook must be open in OneNote, at the path it had (`ObjectNotFound` otherwise: relay `userAction`), and needs the user's write grant.
- If the restore fails after it created the page, the error names that new, possibly empty page: ask the user to delete it before you try again.
- Entries are kept for 30 days by default, and may go sooner if the journal passes its size cap. No tool lists them: an entry ID lost with the session (a new session, a restarted client) can't be looked up. So when a change matters, give the user its `meta.journalEntryId`.
- `create_page` and `restore_from_journal` save no entry. To undo a new or restored page, delete it to the recycle bin with `delete_hierarchy_object` if the user asks (`references/hierarchy-writes.md`, recipe 4), or the user deletes it in OneNote.

## 7. A new page, then its content

1. `mcp__plugin_onenote_desktop__create_page` with the section's ID as `sectionId` (`style` defaults to `blankPageWithTitle`). The new page's ID is in `meta.pageId`.
2. `mcp__plugin_onenote_desktop__update_page_content` with that `pageId` and an XML holding new Outlines only (no `objectID`s), and no `basis`. The warning `noBasis` is expected: nothing can have changed a page you just created.
3. To set the title, read the new page with `mcp__plugin_onenote_desktop__get_page_content` and change its `one:Title` OE as in recipe 1. In a password-protected section you can't read, leave the title as it is.

Each `create_page` call adds another page. If one fails with `outcomeUnknown: true`, list the section's pages before trying again.

## When a write fails

- **`outcomeUnknown: true`:** the write may or may not have happened. Before anything else, re-read: the page with `mcp__plugin_onenote_desktop__get_page_content` after `update_page_content` or `delete_page_content`, the section's pages (`mcp__plugin_onenote_desktop__get_hierarchy`, `scope: pages`) after `create_page` or `restore_from_journal`; after a hierarchy write, what `references/hierarchy-writes.md` (When a hierarchy write fails) names. Tell the user what you found, and redo the change only if it's absent and they still want it. Never retry a write blindly.
- **Without `outcomeUnknown`:** nothing was written, except that a failed restore may name a new page it created (recipe 6). Follow the error rows in `SKILL.md`.
- **`ComBusy`:** another write held the lock, or OneNote was busy; nothing was written. Retry once. If it's busy again, tell the user and wait for them.

## What this release can't do

Moving a page, section or section group to another place, deleting a notebook, and deleting images or ink. Tell the user this release can't do it, and that they can do it in OneNote. Never work around it (`SKILL.md`, Writing). Renaming a page is a title edit (recipe 1); renaming sections, section groups and notebooks, reordering, and deleting pages, sections and section groups are in `references/hierarchy-writes.md`.
