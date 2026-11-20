# Exporting and opening packages: recipes

Read this before an export (`mcp__plugin_onenote_desktop__publish`) or before opening a OneNote package (`mcp__plugin_onenote_desktop__open_package`). The safety rules in `SKILL.md` (Writing) always apply. Each tool's description is the contract for its arguments and refusals; this file says which tool to use, in what order, what to tell the user, and how to undo it. Example names ("notebook A", "MCP Test", "Trip notes") are placeholders.

## Before an export or an open

- Do it only when the user asks for that export or that package. Never export or open something to try it out.
- Neither tool needs a write grant, and neither changes the user's notebooks. An export writes a file; opening a package adds a separate copy in OneNote.
- Nothing is ever overwritten: an existing file or folder at the target is `FileExists`. Ask the user for another name; never delete what's there.
- Only the user changes where files may go. Never edit OneNoteMcp's `settings.json`, by any tool or shell command.

## 1. Export a page, section or notebook

**Where files go.** Exports go only under the export folders: the `exportRoots` in the user's `settings.json`, by default `Documents\OneNote Exports` (created on first use). Pass `path` as a file name ("Meeting notes.pdf", or "Trip\Day 1.docx" for a subfolder), which lands under the first export folder, or as a full local path inside one. A path anywhere else is `ExportPathNotAllowed`: relay its `userAction` (the user can add a folder to `exportRoots` themselves). On many PCs `Documents` is synced to OneDrive, so exports land in OneDrive too; that is fine for files.

**The call.** `mcp__plugin_onenote_desktop__publish` with `objectId` (from `mcp__plugin_onenote_desktop__get_hierarchy`), `path` and `format`, whose extension must match:

- **A page:** any format: `pdf` (the default), `xps`, `docx`, `mhtml`, `html` (its images may go into a `<name>_files` folder beside it), `emf`, `one`, `onepkg`.
- **A section:** any format but `emf` (`NotSupported`: a section's EMF holds only its first page). `one` writes a plain OneNote section file.
- **A notebook:** only `onepkg`, a OneNote package; any other format, the default `pdf` included, is `NotSupported`, so pass `format: onepkg`.
- **A section group:** `NotSupported`. Export it section by section: read the group with `mcp__plugin_onenote_desktop__get_hierarchy`, `startNodeId` = the group, `scope: sections`, which lists every section in it, nested groups included. Export each section into a folder named after the group, with a subfolder for each nested group, mirroring the tree ("Group A\Section 1.pdf", "Group A\Inner group\Section 2.pdf"), unless the user wants them all in one folder; then give the user the list of files. A locked section in it is `SectionLocked`: say so, and go on with the others.

**Tell the user** where the file is: `meta.outputPath`.

**Password-protected content.**

- Exporting a protected section, or a page in one, needs the user's read grant for that section, held when the call starts (`ReadNotGranted` otherwise). Reading the section with `mcp__plugin_onenote_desktop__get_hierarchy` (`startNodeId` = the section, `scope: pages`), or one of its pages with `mcp__plugin_onenote_desktop__get_page_content`, asks the user; the section read works even when it has no pages. Such an export succeeds with the warning `exportNotEncrypted`: the file isn't password-protected. Always tell the user that, and where the file is.
- A notebook package needs no read grant for its password-protected sections: they stay encrypted inside it. A section protected only by its location (under an export root, the opened-copies folder or the backups folder) would be plain in the package, so it needs its read grant (`ReadNotGranted` names it).
- A locked section, a page in one, or a notebook holding one, is `SectionLocked`, checked before the read grant: ask the user to unlock it in OneNote first, since a read grant can't help until then, or export the other sections one by one. If you have no read grant for it, the error's `userAction` says so: after unlocking, read the section (the grant dialog asks), then export again.

**When it fails.** With `outcomeUnknown: true` (a `Timeout`, say), the file may still have been written: its path is in the message. Check it with the user before trying again under a new name. Never retry under the same name (`FileExists`).

**Undo:** an export only writes a file. The user deletes it if they don't want it.

## 2. Open a OneNote package

`mcp__plugin_onenote_desktop__open_package` unpacks a `.onepkg` file into a new folder and opens that folder in OneNote as a separate notebook, a copy beside the original.

**The call.** `package` is the full local path (`C:\…`) of the `.onepkg`, in any folder, such as an export's `meta.outputPath`. Leave `destination` out: the copy then goes into a new folder named after the package with the date and time, in OneNoteMcp's opened-copies folder, `%LOCALAPPDATA%\OneNoteMcp\opened`. A `destination` may only be a new folder name there (or a full path to a folder directly inside it); anything else is `ExportPathNotAllowed`.

**Never OneDrive.** A destination inside a OneDrive folder is refused (`ExportPathNotAllowed`): OneDrive's sync client turns a notebook folder unpacked there into an online notebook while OneNote has it open, and OneNote then closes. If the opened-copies folder itself is inside OneDrive (a redirected AppData folder), the tool can't be used on this PC: relay `userAction`. The user can open the package in OneNote themselves (File > Open), which uploads it as an online notebook.

**Tell the user:**

- where the copy is (`meta.outputPath`) and that it is open in OneNote as a separate notebook (`meta.objectId`);
- that every section of the copy counts as password-protected, because it sits in the opened-copies folder, so reading it needs their read grant (reading a page with `mcp__plugin_onenote_desktop__get_page_content` asks them); and that the copy has no write grant;
- that they close the copy in OneNote when they're done (no tool closes a notebook); once it is closed and nothing in it has changed for more than 7 days, OneNoteMcp deletes its folder the next time it starts while OneNote is running, and `OneNoteMcp uninstall --purge` deletes the closed ones too.

With the warning `pagesNotListed`, no page was listed within 5 s. That is expected when every section is empty, locked or protected; otherwise read the notebook again in a few seconds.

**When it fails.** A failure after the folder was made names the folder and says whether the package was unpacked. Another try needs a new destination (or none). Tell the user the folder may hold a partly unpacked package, which they can delete.

**Undo:** the original is untouched. The user closes the copy in OneNote; its folder is cleaned up as above.
