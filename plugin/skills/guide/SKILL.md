---
name: guide
description: This skill should be used when the user asks about their OneNote notebooks, sections, section groups, pages or subpages, for example "list my OneNote notebooks", "what sections are in my Work notebook", "find the page called Trip plan", "read my meeting notes page", "search OneNote for budget", "show my To Do items on that page", "find pages tagged with project", "what's in Quick Notes", "where is the OneNote backup folder", "get a link to this OneNote page", "show the image on that page", "download the missing images on that page", "show me that page in OneNote" or "which notebook is this page in", "read my password-protected section", "add a new page to that section". Also use it when the user asks to create, edit, move, rename or delete anything in OneNote, because this release can only add empty pages. It covers tool choice and scope, reading OneNote 2013 XML, password-protected sections, search caveats, and errors.
---

# OneNote desktop: reading notebooks

The `onenote` plugin's `desktop` server reads the OneNote desktop app on this Windows PC through its COM API. It has eleven read tools (one of them, `list_grants`, only lists the user's read and write permissions), one user-visible tool that shows a page in OneNote (`navigate_to`), and two write tools for notebooks the user has allowed you to write to: `create_page`, which adds an empty page to a section, and `update_page_content`, which changes a page. It can't yet move, rename or delete anything but the OEs `update_page_content` removes.

The tools are thin wrappers over COM. Their descriptions say what each parameter means; this guide says which tool to pick, in what order, and how to interpret what comes back.

## When to use

Use this guide for any OneNote read: listing notebooks, section groups, sections and pages, reading pages, searching, getting links, and looking up special locations such as Quick Notes.

Call `mcp__plugin_onenote_desktop__create_page` only when the user asks for a new page; never to try something out. Each call adds another page. It needs the user's write grant for the section's notebook. Without one, the call may raise a dialog asking the user to type the notebook's name; if they don't, or the client can't show it, it fails with `WriteNotGranted`, and only the user can grant it, with the terminal command in `userAction`. Never run that command yourself. The user revokes a write grant with `OneNoteMcp grant --revoke --write "<notebook name>"`.

Call `mcp__plugin_onenote_desktop__update_page_content` only when the user asks to change what's on a page: read the page first with `get_page_content`, pass its `meta.basis` unchanged as `basis`, and send each Outline you change whole (every OE it has, with its objectID), naming OEs to delete in `removeObjectIds`. It needs the same write grant as `create_page`. If the user asks to move, rename or delete a page or section, say that this release can't do it yet, and that they can make the change in OneNote themselves. Don't work around it: never edit `.one` files or other notebook files, never script or automate the OneNote window, and never use any other route to change a notebook.

## Tool map

Claude Code shows each tool as `mcp__plugin_onenote_desktop__` followed by its bare name.

| Tool (bare name) | Use it to | Returns |
| --- | --- | --- |
| `mcp__plugin_onenote_desktop__get_hierarchy` (`get_hierarchy`) | List notebooks, section groups, sections and pages, from the root or from any ID | `xml` |
| `mcp__plugin_onenote_desktop__get_hierarchy_parent` (`get_hierarchy_parent`) | Step one level up from an ID | `meta.parentId` |
| `mcp__plugin_onenote_desktop__get_page_content` (`get_page_content`) | Read one page | `xml`, `meta.basis` |
| `mcp__plugin_onenote_desktop__get_binary_page_content` (`get_binary_page_content`) | Read one image (or other binary) from a page | `meta.binary` (base64) |
| `mcp__plugin_onenote_desktop__find_pages` (`find_pages`) | Full-text search under a notebook, section group or section | `xml`, `meta.filtered` |
| `mcp__plugin_onenote_desktop__find_meta` (`find_meta`) | Find pages tagged with a `one:Meta` name | `xml`, `meta.filtered` |
| `mcp__plugin_onenote_desktop__get_hyperlink_to_object` (`get_hyperlink_to_object`) | Get a `onenote:` link to a notebook, section, page or paragraph | `meta.link` |
| `mcp__plugin_onenote_desktop__get_web_hyperlink_to_object` (`get_web_hyperlink_to_object`) | Get an `https` link, for notebooks stored on OneDrive or SharePoint | `meta.link` |
| `mcp__plugin_onenote_desktop__get_special_location` (`get_special_location`) | Get the path of the backup folder, the Quick Notes section or the default notebook folder | `meta.path` |
| `mcp__plugin_onenote_desktop__read_result` (`read_result`) | Read a result that was too large to return whole, part by part (see Large results) | `xml` (a part) or `index` |
| `mcp__plugin_onenote_desktop__list_grants` (`list_grants`) | Show which password-protected sections the user has allowed you to read, and until when (see Password-protected sections), and which notebooks they have allowed you to write to (kind `writeNotebook`) | `meta.grants` |
| `mcp__plugin_onenote_desktop__navigate_to` (`navigate_to`) | **User-visible.** Show a notebook, section group, section or page in OneNote (and select one object on it). Only when the user explicitly asks; see Missing images | nothing |
| `mcp__plugin_onenote_desktop__create_page` (`create_page`) | **Writes.** Add an empty page at the end of a section (`style`: `blankPageWithTitle` by default, `blankPageNoTitle`, or `default` for the section's template). Only when the user asks for a new page; see When to use | `meta.pageId` |
| `mcp__plugin_onenote_desktop__update_page_content` (`update_page_content`) | **Writes.** Change a page with OneNote 2013 page XML (`pageId`, `xml` whose root `one:Page` has that ID, `basis` = the `meta.basis` of your `get_page_content` read, unchanged, `removeObjectIds` for OEs to delete). Only when the user asks to change a page. A sent Outline with an `objectID` replaces its whole OE list: send every OE it has, or name the ones to delete. Saves the page to the recovery journal first, then re-reads it (`readBackDiffers`, `imageLost`, `readBackFailed` warnings) | `meta.basis` for the next write |

Every result is an envelope: `{ok: true, xml?, index?, meta, warnings[]}` or `{ok: false, error: {code, retryable, userAction, message, ...}, meta: {correlationId}}`. Check `ok` first, then read `warnings` even when `ok` is true.

When a result has XML (or `meta.binary`), its text arrives as two parts: the first line is the envelope's JSON without the body, and everything after that first newline is the XML (or base64) exactly as OneNote returned it, not JSON-escaped.

### Choosing the cheapest call

- Use `mcp__plugin_onenote_desktop__get_hierarchy` at the narrowest scope that answers the question. Start from the notebook or section the user means, not from the root, and go only as deep as needed:
  - `self`: the start node only (its name and attributes).
  - `children`: its direct children only.
  - `notebooks`: notebooks only. This is the default, and with no `startNodeId` it lists every open notebook.
  - `sections`: down to sections, including section groups.
  - `pages`: down to pages. This can be large; use it from one section, or at most one notebook, never from the root unless the user asks for everything.
- Read pages with `mcp__plugin_onenote_desktop__get_page_content` and `includeBinary` false (the default). Ask for binaries only when the user wants an image, and then fetch the one image with `mcp__plugin_onenote_desktop__get_binary_page_content`.
- Search with a `startNodeId` (a notebook or section). Use `fromRoot: true` only when the user wants every notebook searched or the location is unknown.
- A narrow call is still cheaper than a large one: a result is returned whole up to about 100,000 characters, and past that it comes back as an index you page through with `mcp__plugin_onenote_desktop__read_result` (below).

## Large results

A result too large to return whole (over the server's `results.inlineMaxChars`, 100,000 characters by default) comes back with `meta.result` and an `index` instead of `xml` (or instead of `meta.binary`). Nothing is lost: every character is readable with `mcp__plugin_onenote_desktop__read_result`.

- `meta.result` is `{resultId, totalChars, expiresAt, namespaces, pages}`. Pass `resultId` to every `mcp__plugin_onenote_desktop__read_result` call. The original call's other `meta` and its `warnings` come with this first index.
- The `index` lists the children of one element (`index.ref`; the whole result is `/`), in document order. Each entry is either one element, `{ref, element, chars, children, contains?, label?}`, or a run of small consecutive siblings, `{run: [firstRef, lastRef], count, chars, firstLabel?, lastLabel?}`. A `label` is a hierarchy node's `name`, or the first 80 characters of a page element's text. `contains` counts tables, images and attachments below it.
- Read with exactly one of:
  - `ref`: that element's XML, unchanged.
  - `run: [firstRef, lastRef]`: those siblings together.
  - `expand: ref` (and `page`, from 0, when `meta.result.pages` is more than 1): that element's own index, to go one level deeper.
  - `ref` with `offset` (and optionally `length`): characters of that one element's span, markup included, from `offset`. Use it for an element with no children but a huge body, such as a long `one:T`, an image's `one:Data`, or a large `get_binary_page_content` result (its index is empty and its whole body is `/`). Read from offset 0, then add the length you got back, until you reach that element's own `chars`, from its index entry or its `expand` response (`meta.result.totalChars` for `/`).
- A `ref` or `run` too big to return comes back as its index instead, so a part is never cut in the middle. Expand it and read its children.
- `offset` takes a single element's `ref` only. When a `run` comes back as an index, that index has `run` set, `ref` set to the run's parent, and `chars` set to the run's length: don't offset-read that `ref` up to those `chars`. Read the run's members by their own refs instead.
- A part is a fragment, not a document: its `one:` prefix is declared in `meta.result.namespaces`. Parts can overlap (a parent's ref and its child's); don't double-count text.
- Read only what the user needs: use the labels to pick the parts, rather than reading everything.
- A page part's `meta.basis` has the hashes of the Outlines and OEs wholly inside it. Writes don't exist in this release, so ignore it.
- A result expires 30 minutes after its last read (by default) and is gone when the server restarts. `ResultExpired` means: repeat the original call to get a new `resultId`.

## Password-protected sections

A section with a password (`encrypted="true"` on its `one:Section`) stays private even after the user unlocks it in OneNote: the server returns its content only with the user's **read grant** for that one section. You can't grant it, and must never try to get the content another way.

- **Broad reads** never ask. `mcp__plugin_onenote_desktop__get_hierarchy` from above the section keeps its `one:Section` but leaves out its pages; `mcp__plugin_onenote_desktop__find_pages` and `mcp__plugin_onenote_desktop__find_meta` never search it. The section's ID is then in `meta.withheld`, with a `readNotGranted` warning. Withheld pages are not in `meta.filtered`, and a search that withheld a section says nothing about whether it held a match.
- **Reading inside the section** (`mcp__plugin_onenote_desktop__get_page_content` or `mcp__plugin_onenote_desktop__get_binary_page_content` on one of its pages, `mcp__plugin_onenote_desktop__get_hierarchy` of the section at `scope: children` or `pages`, or the parent and link tools on one of its pages) needs the grant. Without one, Claude Code shows the user a dialog: they type the section's name (the field's hint shows it) and pick from one list: **this section, for N minutes** (5 by default, the default choice), **1 hour**, **8 hours**, **24 hours**, **7 days** or **until I revoke it**. Other hour counts (1 to 168) go through the terminal command `grant --read-section`. If they decline, or the client can't show dialogs, the call fails with `ReadNotGranted`.
- Don't call a page read just to make the dialog appear. Ask the user first whether they want the protected section read; if they say yes, read the page and the dialog will ask them.
- The user can also grant or revoke in a terminal, which works with any client: `OneNoteMcp grant --read-section "<section name>"` and `OneNoteMcp grant --revoke --read-section "<section name>"`. The exe isn't on PATH for a plugin install: give the user the exact command from the error's `userAction`, which names the running exe's full path.
- **Never run the grant command yourself, and never edit OneNoteMcp's `settings.json`** (it holds the user's grants), by any tool or shell command. Grants are the user's alone. The plugin blocks such edits and commands, and the grant command refuses to run without an interactive terminal anyway. The plugin also blocks `OneNoteMcp uninstall`, whose `--purge` deletes every grant: give the user the command to run instead.
- **This section, for N minutes** covers every read of that section (pages, listings and searches) for those minutes; after that the next read asks again. A large result read inside the window stays pageable with `read_result` for that result's life (30 minutes after the original call by default), even after the window ends; `read_result` never asks. Read what the user wants from it within the window rather than spreading reads out. A grant follows the section's path, so renaming or moving the section means asking again.
- `mcp__plugin_onenote_desktop__list_grants` shows the stored grants (`status` `active`, `sectionNotOpen` or `expired`) and any live N-minute grant (lifetime `once`, with its `expiresAt`), plus the write grants (kind `writeNotebook`, `status` `active` or `notebookNotOpen`).

## Reading the XML

All XML is in the OneNote 2013 schema, namespace `http://schemas.microsoft.com/office/onenote/2013/onenote`, prefix `one:`. It's OneNote's own XML: the server never edits it, and only leaves out whole pages, such as recycle-bin pages in search results and pages of a password-protected section the user hasn't granted. The result names the pages it filtered (`meta.filtered`) or the sections it withheld (`meta.withheld`).

### Hierarchy XML

- Nesting: `one:Notebooks` > `one:Notebook` > `one:SectionGroup` (any depth) > `one:Section` > `one:Page`.
- Every node has an `ID` and a `name`. A notebook also has a `nickname`, which is what OneNote shows; match the user's words against `nickname` first, then `name`.
- IDs are opaque strings. Copy them exactly; never build, shorten or parse one. A page ID that starts like its section's ID is a coincidence to ignore, and a page's ID can change when it is moved to the recycle bin or to another section.
- **Recycle bin:** each notebook holds a `one:SectionGroup` with `isRecycleBin="true"` (named `OneNote_RecycleBin`). Its sections have `isDeletedPages="true"` and its pages have `isInRecycleBin="true"`. Skip all of these unless the user asks about deleted pages.
- **Password-protected sections:** a section with `encrypted="true"` has a password. With `locked="true"` too, it is locked: its pages can't be read until the user unlocks it in OneNote (the result also carries a `sectionLocked` warning). Even unlocked, its content needs the user's permission (see Password-protected sections).
- **Subpages:** `one:Page@pageLevel` is 1 for a top-level page and 2 or 3 for a subpage. Pages are listed in section order, so a subpage belongs to the nearest earlier page with a lower level.
- `lastModifiedTime` and `dateTime` (created) are UTC.
- `one:Meta` (`name`, `content`) can appear on a page in hierarchy XML; these are the tags `mcp__plugin_onenote_desktop__find_meta` searches.

### Page XML

- `one:Page` (with `ID`, `name`, `pageLevel`) starts with definitions and settings (`one:TagDef`, `one:QuickStyleDef`, `one:Meta`, `one:PageSettings`), then `one:Title`, then the content: `one:Outline` elements, and page-level `one:Image` or `one:InsertedFile`.
- **Title:** `one:Title/one:OE/one:T`.
- **Text:** each `one:Outline` is a text box. Inside it, `one:OEChildren` holds `one:OE` paragraphs, and an OE's own `one:OEChildren` holds its indented children. Each OE has an `objectID`.
- **`one:T` holds HTML inside CDATA**, such as `<![CDATA[Plan <span style='font-weight:bold'>B</span>]]>`. For plain text, drop the tags and decode entities. Keep `<a href="...">` targets when the user wants links.
- **Headings:** an OE's `quickStyleIndex` points at a `one:QuickStyleDef@index`. Its `name` is `h1`…`h6` for headings. Treat every other name (`p`, `PageTitle`, `cite`, `code` and so on) as body text.
- **Lists:** an OE with `one:List/one:Bullet` or `one:List/one:Number` is a list item.
- **Order:** read Outlines in document order. If the layout matters, `one:Position` (`x`, `y`) places each one on the page.
- **Tables:** `one:Table` > `one:Row` > `one:Cell` > `one:OEChildren` > `one:OE`. `hasHeaderRow="true"` means the first row is the header. Render small tables as Markdown tables.
- **Images:** `one:Image` (inside an OE, or directly on the page). In the default XML it holds `one:CallbackID@callbackID`, the handle for `mcp__plugin_onenote_desktop__get_binary_page_content`. With `includeBinary` true it holds `one:Data` (base64) instead, and images whose binary isn't on this PC are left out of the XML; `meta.binaryUnavailable` counts them. An `alt` attribute, when present, describes the image.
- **Attachments:** `one:InsertedFile` has `preferredName` (the file name shown) and `pathCache`. Reading attachments through these tools hasn't been tested: report the file name, and don't open files from the paths in the XML.
- **Note tags:** `one:Tag` on an OE (`index`, `completed`) refers to the `one:TagDef` with the same `index`, whose `name` says what it is ("To Do", "Important"). A To Do with `completed="true"` is checked.
- **Page tags:** `one:Meta` (`name`, `content`) is data attached by add-ins or tools, not visible text.
- Ignore `one:PageSettings`, `one:Size` and authorship or time attributes unless asked.
- `meta.basis` records the version that was read. Writes will need it later; this release has no writes, so ignore it.

## Recipes

Example names below (such as "Work" or "Trip plan") are placeholders.

**List notebooks.**
Call `mcp__plugin_onenote_desktop__get_hierarchy` with no arguments. Report each `one:Notebook@nickname`.

**List the sections of a notebook by name.**
1. `mcp__plugin_onenote_desktop__get_hierarchy` with no arguments. Find the notebook whose `nickname` (else `name`) matches the user's words. If several match, or none, ask which one.
2. `mcp__plugin_onenote_desktop__get_hierarchy` with `startNodeId` set to that notebook's `ID` and `scope: sections`. Report the sections, grouped under their section groups, and leave out the recycle-bin group. If the user wants only top-level items, use `scope: children` instead.

**Find a page by title.**
1. If the user names the section or notebook, resolve its ID as above, then call `mcp__plugin_onenote_desktop__get_hierarchy` from it with `scope: pages` and match `one:Page@name` (ignoring case; prefer exact matches over partial ones).
2. If the location is unknown, call `mcp__plugin_onenote_desktop__find_pages` with the title's words as `query` (it matches titles as well as text), from the most likely notebook, or with `fromRoot: true`. Then keep only the pages whose `name` matches.
3. Skip pages with `isInRecycleBin="true"`. If several pages match, list them with their section and ask.

**Read a page as text.**
1. Get the page ID (recipes above).
2. `mcp__plugin_onenote_desktop__get_page_content` with `pageId`, leaving `includeBinary` false.
3. Render the title, then each Outline's paragraphs in order, with indentation for nested OEs, headings from `quickStyleIndex`, Markdown tables, `[ ]`/`[x]` for To Do tags, and `[image]` or the attachment name where those appear.

**Read an image.**
1. `mcp__plugin_onenote_desktop__get_page_content` with `includeBinary` false, and find the `one:Image` the user means. Take its `one:CallbackID@callbackID`.
2. `mcp__plugin_onenote_desktop__get_binary_page_content` with the `pageId` and that `callbackId`. `meta.binary` is the base64 image. Its type isn't stated: a value starting `iVBOR` is PNG, `/9j/` is JPEG, `R0lG` is GIF.
3. If the result has a `binaryUnavailable` warning and no `meta.binary`, the image isn't stored on this PC (common in notebooks stored online), or the callbackID was wrong. Check that you copied the callbackID from the default XML. If you did, follow **Missing images** below.
4. If the callbackID was a page-level `one:XPSFile`'s, the warning says it is a File Printout: the printout document itself can't be read through OneNote's API. Tell the user, and read the printout's page images (`one:Image` with `isPrintOut="true"`) instead if they want them.

**Missing images.**
Images in notebooks stored online often aren't on this PC until OneNote downloads them. Showing the page in OneNote downloads them (in the one page measured, within 2 seconds; large pages or slow connections may take longer), but only with OneNote open in a window, and it takes the user's keyboard focus and changes what OneNote shows, so never do it unasked.
1. Tell the user the image (or, from `mcp__plugin_onenote_desktop__get_page_content` with `includeBinary`, the `meta.binaryUnavailable` images) isn't on this PC. Mention that OneNote's File > Options > Sync > "Download all files and images" keeps images on this PC from then on.
2. Ask whether to show the page in OneNote now so it downloads the images. Make clear it will bring OneNote to the front.
3. Only if the user says yes: call `mcp__plugin_onenote_desktop__navigate_to` with the page's ID as `hierarchyId`. Then call `mcp__plugin_onenote_desktop__get_binary_page_content` again with the same `pageId` and `callbackId` (or re-read the page). If it still gives `binaryUnavailable`, don't retry again: `navigate_to` returns ok but shows and downloads nothing when OneNote runs without an open window. Ask the user to bring OneNote's window up (then you may show the page once more if they ask), or to use the Sync setting, and stop.
4. If the user says no, or says nothing about it, don't call `mcp__plugin_onenote_desktop__navigate_to`. A request to read or show an image is not a request to change what OneNote shows.

**Full-text search.**
1. Pick the narrowest start: the notebook or section the user means, else `fromRoot: true`. Give exactly one of `startNodeId` or `fromRoot: true`.
2. `mcp__plugin_onenote_desktop__find_pages` with the words as `query`. Every word must appear on a page, ignoring case.
3. Report the matching pages (name, and section from the enclosing `one:Section`), then the search caveat below. If `meta.withheld` is set, add that password-protected sections weren't searched. Read a page's content only if the user asks for more than the list.

To find pages by an add-in tag, use `mcp__plugin_onenote_desktop__find_meta` with the `one:Meta` name. The name is matched exactly, including case.

**Get a link.**
- `mcp__plugin_onenote_desktop__get_hyperlink_to_object` with the notebook, section or page ID as `hierarchyId`. To link to one paragraph, also pass that OE's `objectID` as `pageContentObjectId`. `meta.link` is a `onenote:` link that opens in the desktop app.
- For a link that works in a browser, use `mcp__plugin_onenote_desktop__get_web_hyperlink_to_object` the same way. If it returns an empty `meta.link` with a `webLinkUnavailable` warning, the notebook is stored only on this PC: offer the `onenote:` link instead.
- Give the link to the user. Don't open it yourself; that would move the user's OneNote view.

**Walk up from a page ID.**
1. `mcp__plugin_onenote_desktop__get_hierarchy_parent` with the page ID gives its section. Call it again with each `meta.parentId` until it returns the empty string, which means the previous ID was the notebook.
2. To name each level, call `mcp__plugin_onenote_desktop__get_hierarchy` with `startNodeId` set to that ID and `scope: self`.

**Quick Notes.**
`mcp__plugin_onenote_desktop__get_special_location` with `location: unfiledNotesSection` gives the Quick Notes section's file path, not its ID. To read it, find the `one:Section` whose `path` matches that path in a `scope: sections` hierarchy, comparing without regard to case or a trailing `\` or `/`. If none matches, the notebook holding Quick Notes isn't open in OneNote; tell the user.

## Search caveats

- Search results are **always possibly incomplete** (`meta.possiblyIncomplete` is always true). A notebook that is still syncing, or a page OneNote hasn't indexed, can be missing.
- The server remembers page text between searches, so a page that another program (another add-in, or a second OneNote MCP session) changed through COM while it wasn't open in OneNote can still match its old text. If the user says a just-changed page is missing or wrongly matched, read it with `mcp__plugin_onenote_desktop__get_page_content`.
- So every time you report search results, say briefly that the search may have missed pages. Never tell the user a page doesn't exist because a search didn't find it. Say it wasn't found, and offer to browse the hierarchy or try other words.
- `meta.filtered` lists the pages the server removed from the XML, as `{pageId, reason}`:
  - `recycleBin`: a deleted page. Don't report it unless the user asked about deleted pages.
  - `noMatch`: OneNote returned it, but it doesn't contain every query word (or, for `mcp__plugin_onenote_desktop__find_meta`, has no `one:Meta` of exactly that name). It is not a result.
  - `notFound` (`mcp__plugin_onenote_desktop__find_pages` only): the page disappeared during the search.
- An empty result with many `noMatch` entries usually means one of the words is wrong. Try fewer words.
- `meta.withheld` lists password-protected sections the search skipped because the user hasn't granted a read of them. Their pages may or may not match; never guess.

## Errors and warnings

On `ok: false`, read `error.code`, `error.retryable` and `error.userAction`.

- **`retryable: true`:** tell the user what `userAction` says, and retry only after they say they've done it. The exceptions: for `ComBusy` or `Timeout` on the read tools, retry once straight away. If that also fails, relay `userAction` and stop. For `Cancelled`, repeat the call only if you still need the result; there's no need to ask the user. Never retry `mcp__plugin_onenote_desktop__navigate_to` without asking the user again.
- **`retryable: false`:** don't retry with the same input. Follow `userAction`, which may mean re-reading to get a current ID, or telling the user.

| Code | What to do |
| --- | --- |
| `OneNoteNotRunning` | Ask the user to start OneNote desktop. Never start it yourself. |
| `ModalDialogOpen` | Ask the user to close the open dialog in OneNote, then retry. |
| `SectionLocked` | Ask the user to unlock the section in OneNote, then retry. If they say it's already unlocked, pass on the error's `userAction`: it names the OneNote option that keeps protected sections from add-ins. Never change that option yourself. |
| `ComBusy` | OneNote is busy or syncing. Retry once now; then ask the user to wait and tell you when to try again. |
| `Timeout` | Retry once now; then ask the user to check that OneNote is responding. |
| `ComUnavailable` | The server may already have retried. Relay `userAction` (check that OneNote is running and responding, or restart it), then retry once the user says it's done. |
| `Cancelled` | The call was cancelled before it finished, usually because the request was stopped. Repeat it only if you still need the result. |
| `ObjectNotFound` | The ID is stale (pages get new IDs when deleted or moved to another section, and every ID changes when a notebook is closed and reopened). Re-read the hierarchy and use a current ID. If the notebook itself is missing, ask the user to open it. |
| `InvalidQuery` | For a search: check that you gave exactly one of `startNodeId` or `fromRoot: true`. Otherwise change the query. From `mcp__plugin_onenote_desktop__read_result`: give exactly one of `ref`, `run` or `expand`, and keep `offset` below the element's `chars`. |
| `IntegrityMismatch` | The server and OneNote run at different elevation. Relay `userAction`; this needs a restart, not a retry. |
| `ReadNotGranted` | The content is in a password-protected section the user hasn't allowed you to read (or, from `mcp__plugin_onenote_desktop__read_result`, the permission has since ended). Tell the user, and if they want it read, give them the `OneNoteMcp grant --read-section` command from `userAction` or let them retry so the dialog appears. From `mcp__plugin_onenote_desktop__read_result`, once the user grants again, repeat the original call: `read_result` itself never shows the dialog. Never retry on your own. |
| `ResultExpired` | The stored large result expired or the server restarted. Repeat the original call, then continue with its new `resultId`. |
| `UnknownObjectId` | From `mcp__plugin_onenote_desktop__read_result`: the ref isn't in that result. Use a ref from its index, or `/`. |
| `NotSupported` | Not in this release. Tell the user. |
| `WriteNotGranted` | From `mcp__plugin_onenote_desktop__create_page`: the user hasn't allowed writes to that notebook (the message says why: no grant, or the dialog was declined, cancelled, unavailable or answered with another name). Tell the user, and if they want the page, give them the `grant --write` command from `userAction` to run in their own terminal. Never run it yourself; retry only after they say they have. |
| `SettingsInvalid` | OneNoteMcp's `settings.json` is invalid, or a newer OneNoteMcp wrote it, so writes are off. Relay `userAction`. Never edit `settings.json` yourself. |
| `InvalidXml` | OneNote or the server rejected the XML of a read; ignore the write-oriented `userAction`. Don't retry; tell the user, with `error.message` and `error.hresult` if present. |
| `InvalidArgument` | An argument doesn't fit the tool's input schema: a name the tool doesn't declare, a required argument left out, or a value of the wrong type or outside its allowed values (names and values are case-sensitive). `error.message` names each wrong argument, says what it expects, and lists the tool's arguments. Fix each one, spelled exactly and with a value of the type it expects, and call again. Don't drop an argument you meant to set just to get past the error: the tool would then use its default, which may not be what the user asked for. To leave an optional argument unset, leave it out instead of sending null. |
| `Internal` | Tell the user, quoting `error.hresult` if present. Don't retry. |

Any other code: follow `userAction`. When reporting a failure the user may want to investigate, include `meta.correlationId`; it matches the server's log. `outcomeUnknown: true` only appears on writes: the page may or may not have been created. Re-read the section's pages (`mcp__plugin_onenote_desktop__get_hierarchy` with `scope: pages`) before you call `mcp__plugin_onenote_desktop__create_page` again, and never retry it blindly.

Warnings (`ok: true`, with `warnings[]`) mean the result is usable but degraded. Mention them to the user:

| Warning | Meaning |
| --- | --- |
| `sectionLocked` | A section in the hierarchy is locked; its pages can't be read until the user unlocks it. If it's already unlocked, the warning's `userAction` names the OneNote option to check. |
| `binaryUnavailable` | An image isn't stored on this PC. From `mcp__plugin_onenote_desktop__get_page_content` with `includeBinary` true, those images were left out of the XML and `meta.binaryUnavailable` counts them. From `mcp__plugin_onenote_desktop__get_binary_page_content` it can also mean the callbackID was wrong, or that it is a File Printout's `one:XPSFile`, which can't be read at all. Say the image isn't available and the rest of the result is fine, then follow **Missing images** (not for an XPSFile). |
| `webLinkUnavailable` | The notebook isn't stored online, so there is no web link. Offer the `onenote:` link. |
| `readNotGranted` | A password-protected section's pages were left out (its ID is in `meta.withheld`). Say so, and that the user can grant a read (see Password-protected sections). |
| `grantNotSaved` | The user's grant for some hours or until revoked couldn't be saved (its settings file is invalid or not writable), so it works like the N-minute grant: it covers that section for those minutes, in this server only. Relay `userAction`. |

## User-visible actions and limits

- Never take keyboard focus or move the user's OneNote view unasked. The read tools never do: `mcp__plugin_onenote_desktop__find_pages` never shows its results in OneNote, and the link tools don't open links.
- `mcp__plugin_onenote_desktop__navigate_to` is the one user-visible tool: it brings OneNote forward, takes keyboard focus and changes the page it shows. Call it only when the user explicitly asks to see something in OneNote, or agrees to it in **Missing images**. Never call it to help yourself read something.
- Never close or restart OneNote, and never start it on the user's behalf.
- Don't quote whole pages back unless asked; summarise or show the part the user wants.
