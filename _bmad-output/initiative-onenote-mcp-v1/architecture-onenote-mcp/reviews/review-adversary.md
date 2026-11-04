---
type: review
lens: adversary (pairwise incompatibility)
target: _bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md (status draft, 2026-11-03)
sources: .memlog.md, spike-what-can-the-onenote-desktop-com-api-do-on-this-machine-findings.md, CLAUDE.md
created: '2026-11-03'
---

# Adversarial review: architecture spine, onenote-mcp

## Verdict

**Not build-ready. The direction holds, but the binding layer has holes.** The paradigm, stack, binding, lifecycle, logging and distribution ADs are sound and match the spike. But the spine is written as if every write were "edit one existing page". Once you slice it into stories, two teams can follow every AD exactly and still ship units that don't fit together. That happens in six places: non-page writes, the concurrency basis, the shapes in settings and the journal, the error and success transport, the "XML unchanged" rule against search and degrade, and interleaving inside and across processes. Each needs a new or tighter rule before `bmad-ticket` slices. None of them reopens an ADOPTED decision. They make those decisions specific.

Method: for each hole, two units one level down (stories or processes), each obeying the ADs as written, that still build incompatibly. Then a proposed rule.

---

## H1: AD-7/AD-8 assume a page target. Non-page writes are undefined (severity: high)

AD-7 step 2 says to read "the target's current state". AD-8 says to snapshot "the target page's full XML". Step 4 checks that "every referenced `objectID` exists in the state just read". Step 5 relies on `dateExpectedLastModified`. Here is how each of those lands on the other write methods:

| Write | "Target page" | Step 2 read | Step 3 snapshot | Step 4 objectID check | Step 5 date |
|---|---|---|---|---|---|
| `UpdatePageContent` | the page | `GetPageContent` | page XML | OEs on the page | has `dateExpectedLastModified` |
| `CreateNewPage(sectionId)` | **none yet** | section? | nothing to snapshot | n/a | no date parameter |
| `UpdateHierarchy` (create or rename section, later move) | **none** (section/notebook XML) | `GetHierarchy` at what scope? | hierarchy XML? pages? | section/page IDs in hierarchy XML; new objects have no ID | **no date parameter** |
| `DeleteHierarchy(page)` soft | the page | page | page XML | the ID | has date |
| `DeleteHierarchy(section)` soft | **all its pages?** | section hsPages | every page, or none? | | has date |
| `DeleteHierarchy(section/notebook, permanent)` | n/a | | Publish backup (AD-8) **plus** page snapshots? | | |
| `OpenHierarchy(cftNotebook/cftSection)` creating | **no notebook exists yet**, so AD-6 can't resolve one | | | | |
| `DeletePageContent(pageId, objectId)` | the page | page | page XML | the object | has date |
| `Publish(id, path)` | writes a **file on disk**, not OneNote | | | | |
| `CloseNotebook`, `SyncHierarchy`, `SetFilingLocation` | app state | | | | |

**Pair A: "section-rename story" against "page-write story".** The page-write story builds `WritePipeline<PageTarget>`, with snapshot = `GetPageContent(piBinaryData)`. The section story reads AD-8 literally ("target **page**"), finds no page, and skips step 3, or it writes a hierarchy-XML snapshot under the same `operation` header. Restore, built off the page story, then tries to `UpdatePageContent` a `<one:Section>` snapshot and fails, or silently ignores it. Both stories obey AD-7 and AD-8.

**Pair B: "section soft-delete story" against "restore story".** One team snapshots every page in the section (N GetPageContent-with-binary calls on the single STA, which could take minutes on a big section, against a 30 s call timeout that AD-3 applies per call, not per pipeline). The other snapshots nothing, because a soft delete is "recoverable from the recycle bin" and the section keeps its ID (findings row 22). Restore can't tell which convention produced a given journal.

**Pair C: "notebook-create story" against "access story".** AD-6: "any write needs a per-notebook write grant", resolved through the gateway. A notebook that doesn't exist yet can't have a grant, so either creation is impossible or the story invents an exemption. The same goes for `Publish`: it writes to an arbitrary file-system path, and AD-6 doesn't say whether it's a write at all.

**Proposed rule (new AD-7a, "Write classes"). Replace AD-7's step list with a per-class table:**

> Every COM method is assigned exactly one class in a table in the spine (Core owns the enum `OperationClass`). Classes: `Read`, `PageWrite`, `HierarchyWrite`, `Create`, `SoftDelete`, `PermanentDelete`, `ExternalWrite` (Publish to the file system), `AppState` (CloseNotebook, SyncHierarchy, SetFilingLocation, Navigate*). The pipeline steps run per class as follows:
> - `PageWrite` (`UpdatePageContent`, `DeletePageContent`): access on the page's notebook; `GetPageContent(piBinaryData)`; page snapshot; XSD plus objectID-on-page plus section-not-locked; date plus content basis (H2); call.
> - `HierarchyWrite` (`UpdateHierarchy`): access on the owning notebook of **every** ID in the payload and of the payload root (all must be granted; a payload that spans notebooks needs every grant); `GetHierarchy(rootId, hsSelf)` for rename, `hsChildren` for create; a **hierarchy snapshot** (`snapshotKind: hierarchy`, no pages); XSD plus every non-new ID exists under the root just read; the step 5 content basis is the hierarchy XML of the root; no date; call.
> - `Create` (`CreateNewPage`, `OpenHierarchy` creating a section): access on the parent's notebook; read the parent; snapshot a **marker** entry (`snapshotKind: none`, records the parent and the new ID after the call); locked check on the parent section; call. `OpenHierarchy` creating a notebook needs **no grant** and is the only `Create` without one; the new notebook starts ungranted.
> - `SoftDelete` (`DeleteHierarchy`, default): page target → page snapshot; section or section group target → hierarchy snapshot only (recovery is the recycle bin; per-page snapshots are not taken).
> - `PermanentDelete`: both grants. Page → page snapshot. Section or notebook → the OneNote-format backup (AD-8) and a hierarchy snapshot; refuse if either fails.
> - `ExternalWrite`: no notebook grant. The output path must be under a user-configured `exportRoots` setting (default: none, so refused); never overwrite.
> - `AppState`: no grant. AD-11 governs Navigate*; `CloseNotebook` needs the write grant for that notebook.
> - Pipeline timeout: a pipeline has its own deadline (default 120 s) on top of the per-call timeout.

Push back on the spine's own instinct here: "one pipeline" is a good slogan, but a single ordered list that pretends every write is a page edit is exactly what produces Pairs A to C. One pipeline *engine* with a per-class step table keeps AD-7's intent.

---

## H2: Step 5's "content compare" has no basis in the tool contract (severity: high)

AD-7 step 5: "`dateExpectedLastModified` plus a content compare of the targeted elements". Compare against **what**? Step 2 reads the state at write time, so comparing step 2 with itself is a no-op. A meaningful compare needs the state the *caller* based its edit on. The thin `update_page_content` tool mirrors the COM signature (xml, date), and AD-14 freezes parameter schemas, so whoever ships first sets the contract.

**Pair: "page-edit story" against "delete-page-content story" (or "promoted append task").** Story 1 adds `expectedContentHash` (SHA-256 of the whole page XML as returned). Story 2 hashes only the targeted OE, canonicalised, and names the field `basisHash`. A skill recipe that reads once and then calls both tools has to compute two different hashes. And a hash of the whole page XML includes `lastModifiedTime` and other volatile attributes, so it breaks on displayed pages, where those attributes advance (findings row 17). Both stories obey AD-7 to the letter.

Also undefined: which elements are "targeted". UpdatePageContent's partial XML can name OEs, Outlines or the Title. Is it a new Outline with no objectID? That has no target, so nothing to compare.

**Proposed rule (tighten AD-7 step 5, and add to the Consistency Conventions):**

> Every `PageWrite` and `HierarchyWrite` tool takes an optional `basis` object: `{ lastModifiedTime?: string (UTC ISO-8601, as read), elementHashes?: { [objectID]: string } }`. `OneNoteMcp.Xml` owns the one function `ElementHash(element)`: SHA-256, hex, over the element's canonical XML (C14N, with the attributes `lastModifiedTime`, `creationTime` and `lastModifiedBy` and the `one:Data`/CallbackID content removed). Read tools return `elementHashes` for top-level Outlines and OEs in the success envelope (H5), so callers never compute hashes themselves. Step 5 compares `basis.elementHashes` with the hashes recomputed from step 2's read, for every objectID in the payload. A mismatch returns `StaleWrite`. If `basis` is absent the write proceeds, with `warnings: ["noBasis"]`; the skill always sends `basis`.

---

## H3: Shared data shapes are named but not defined (settings.json, grant keys, journal header) (severity: high)

AD-10 names the store and AD-8 names "a small metadata header: object ID, notebook, operation". Neither has a schema, a schema version or a single owning type. The Deferred table even pushes grant-key choice into "the access story". But the permanent-delete story, the elicitation path, the skill (which "can show" grants, per the memlog) and the user's hand edits all read and write the same file.

**Pair A: "access story" against "permanent-delete story".** The access story writes `"grants": { "<notebookId>": { "name", "path" } }`. The permanent-delete story adds `"permanentDeleteGrants": ["<notebookId>", ...]` in a different shape. The access story's re-match by path (Deferred: "store path and name for re-matching") updates the ID in `grants` but not in `permanentDeleteGrants`. After a notebook reopen, permanent delete is silently revoked. Or, worse, a different notebook that later gets the old ID string inherits it. Also open: does a permanent-delete grant without a write grant work?

**Pair B: "settings-writer (elicitation)" against "user hand-edit".** AD-10 requires a cross-process lock plus temp-and-rename. Notepad doesn't take that lock. A half-saved or malformed file is then read at the next access check. Is a malformed file fail-open (reads and writes go on), fail-closed for writes, or a crash? Story A throws `Internal`. Story B treats it as "no grants", which looks to the user as if all grants were revoked. The lock primitive (named mutex or lock file) isn't specified either. Two processes built against different primitives don't exclude each other.

**Pair C: "page-write story" against "restore story" over the journal header.** The header is "inside" a gzip file, or a sidecar, or in the filename? "notebook" means an ID or a name? Restore needs much more than object ID, notebook and operation. It needs the **parent section ID** (soft-deleted pages get new IDs, so the old page ID is useless: findings row 19, memlog line 20), the page title for user disambiguation (findings constraint 6), the snapshot time, the snapshot kind, and whether binary was complete. The two stories pick different fields, and restore can't read the snapshots written by the page story.

**Proposed rule (new AD-10a "Shared schemas", owned in Core by one type each):**

> `settings.json` has `schemaVersion: 1` and is read and written only by `Core.Settings.SettingsStore`. Grants: `"notebooks": { "<notebookId>": { "name": str, "path": str, "write": bool, "permanentDelete": bool, "grantedAt": UTC, "grantedVia": "elicitation"|"file" } }`. `permanentDelete` is honoured only when `write` is also true. Re-matching by path rewrites the whole entry under its new key, atomically. A grant is honoured only if the notebook's live `path` equals the stored `path`; a mismatch is `WriteNotGranted` with `userAction: "re-grant"`. Cross-process lock: the named mutex `Local\OneNoteMcp.Settings`, held across read-modify-write, with a temp file plus `File.Replace`. On a parse or schema failure, reads continue, every write fails closed with `SettingsInvalid`, and the last good copy is never overwritten by the server.
> Journal entries: `Core.Journal` writes `<ts>_<pid>_<guid>.json.gz`. The gzip holds one JSON document `{ schemaVersion, entryId, correlationId, operation (COM method), operationClass, snapshotKind: page|hierarchy|onenoteBackup|none, objectId, notebookId, notebookName, parentId, title, takenAtUtc, binaryComplete: bool, xml }`. A backup's `.onepkg` path sits beside it as `<same stem>.onepkg`, referenced by name. Restore reads nothing else.

---

## H4: Read degrade versus snapshot completeness versus XML-unchanged (severity: high: a real data-loss path)

AD-4: "Tools take and return OneNote 2013-schema XML unchanged." AD-9: `BinaryUnavailable` "degrades a read to a **placeholder**". AD-8: snapshot "**binary included**". The spike shows that on cloud notebooks `piBinaryData` **silently drops** Image elements, with no HRESULT (findings row 6). Only `GetBinaryPageContent` returns 0x8004200F.

**Pair A: "page-read story" against "page-write story".** The read story honours AD-9 by injecting a placeholder element into the XML. That breaks AD-4's "unchanged". Or it adds a synthetic `<one:Image>` with no data. The model edits the text and writes the whole Outline back (UpdatePageContent's partial-replace semantics). The image is replaced by the placeholder, or deleted. Every AD was obeyed, and the user loses an image.

**Pair B: "journal story" against "restore story".** The journal story reads with `piBinaryData` (as AD-8 says), gets XML with the images silently dropped, and records the snapshot as "full, binary included". The restore story trusts it. Restoring a cloud page that way deletes its images.

**Pair C: "search story" against "hierarchy story".** AD-12 *filters* FindPages output (drops recycle-bin pages, applies metadata filters) and adds `possiblyIncomplete`. AD-4 says to return XML unchanged. The search story either violates AD-4 or returns unfiltered XML with a side list. The hierarchy story returns raw `GetHierarchy` XML, *including* the recycle bin and locked sections listed as empty. That contradicts the CLAUDE.md and findings constraint 11, "show them as locked, not empty".

**Proposed rule (tighten AD-4 and AD-9):**

> The server never inserts, alters or synthesises elements in returned XML. Where the server filters (search: recycle bin, metadata), it removes whole `one:Page` elements only, and lists what it removed in `meta.filtered`. Degraded and derived facts go only in the success envelope's `meta` and `warnings` (H5): `binaryUnavailable: [callbackId|objectId]`, `possiblyIncomplete`, `lockedSections: [id]`. A page read that requests binary compares `piBasic` Image/InsertedFile counts with `piBinaryData` counts, and reports each shortfall in `meta.binaryUnavailable`. A journal snapshot records `binaryComplete: false` when there's a shortfall. A `PageWrite` whose payload contains (by objectID) an Outline that, at step 2, holds an Image whose binary is unavailable is refused with `BinaryUnavailable` and `userAction: "edit the element, not its outline"`, unless the payload omits that Image's objectID entirely (OneNote then keeps it). Restore from a snapshot with `binaryComplete: false` warns before it applies.

The final refusal rule is a guess about UpdatePageContent's partial-update semantics with images. The spike didn't probe it, so the story that implements it has to verify it with a fixture on a cloud page.

---

## H5: Error and success transport are unspecified, and the closed enum is already short (severity: medium-high)

AD-9 fixes the *fields*, but not:
1. **The transport.** Is it a `CallToolResult` with `isError: true` and the object in `structuredContent`, JSON in a text block, or an `McpException` (which the SDK turns into a JSON-RPC error that the model may never see)? Two tool stories will choose differently. AD-14 then freezes both choices.
2. **The layer.** "The HRESULT→code map lives only in `OneNoteMcp.Com`", but `WriteNotGranted`, `StaleWrite` (content compare) and `InvalidXml` start in Core, and Core can't reference Com. If the enum lives in Com, Core can't produce it. If it lives in Core, fine, but the spine doesn't say so. Does the port throw or return a result? The Com story throws `OneNoteGatewayException`. The Core story's `FakeGateway` returns `Result<T>`. CI tests then pass against a contract the real adapter doesn't honour.
3. **Missing codes for known outcomes.** `0x80042019` invalid query (findings row 30), `0x8004201a` file already exists (Publish, row 24), `0x80042014` object does not exist (COM side, versus `UnknownObjectId` at validation; are they the same code?), `RPC_E_CALL_REJECTED` after retries ran out (`ComBusy`?), a journal write failure, a backup failure (AD-8 "refused if the backup fails": with which code?), invalid settings (H3), and the per-pipeline deadline. Each story will funnel these into `Internal`, or add a code independently. "Adding a code is minor" lets two stories add `BackupFailed` and `JournalBackupFailed` in the same minor version.
4. **Success shape.** `possiblyIncomplete` (AD-12) and the degrade placeholder (AD-9) need a place to live. Is a bare XML string the success body for `get_hierarchy`, while `find_pages` returns `{xml, possiblyIncomplete}`?

**Proposed rule (tighten AD-9, and add "Envelope" to the conventions):**

> `OneNoteError` and the `ErrorCode` enum live in `OneNoteMcp.Core`. `IOneNoteGateway` methods throw only `OneNoteException(OneNoteError)`, and the Com adapter's HRESULT map is the only code that creates one from an HRESULT. `FakeGateway` throws the same type. Tools never throw. Every tool returns a `CallToolResult` with `structuredContent` set to either `{ ok: true, xml?: string, meta: {...}, warnings: [string] }` or `{ ok: false, error: {code, hresult|null, retryable, userAction, message} }`, with `isError` set to match, and a text block that duplicates the JSON for clients without structured content. Add these codes now: `InvalidQuery`, `FileExists`, `ObjectNotFound` (from COM 0x80042014; `UnknownObjectId` stays for pre-call validation), `ComBusy`, `JournalFailed`, `BackupFailed`, `SettingsInvalid`, `ExportPathNotAllowed`, `NotSupported`. New codes are added only by an amendment to this AD, never by a story.

---

## H6: The pipeline isn't atomic within one process or across processes (severity: medium-high)

AD-2 serialises **COM calls**, not **pipelines**. A pipeline is 3 to 5 gateway calls (resolve parent chain, read, snapshot read, validate, call). The MCP SDK dispatches tool calls concurrently. So in **one process**, tool call X's step 2 read can come before tool call Y's step 6 write, and X's step 5 compare then passes against data that's stale by the time X's step 6 runs. Across **two processes** (AD-10), the same thing happens with less ordering still. The memlog accepts "best-effort" for same-page edits from two processes. But the server's own interleaving, inside one process, is under its control, and leaving it open isn't best-effort, it's a bug.

**Pair: "server process A" against "server process B"** (or two concurrent tool calls in A). Both run AD-7 exactly as written. A reads page P (hash h1) and B reads P (h1). A writes OE-1 (P becomes h2). B's compare runs before A's write lands on the shared ONENOTE.EXE, or `lastModifiedTime` doesn't advance (an undisplayed page, findings row 18). B writes OE-1 from its stale basis. A's change is lost, and A's journal snapshot doesn't hold B's pre-state, because B snapshotted h1, not h2. Each process obeyed every rule.

**A related case: promoted tasks (AD-5).** "Compose thin use cases through the same pipeline" means a "move page" task runs create, then write, then delete, as three pipelines with three snapshots and no correlation between them. A failure halfway through leaves a duplicate, and nothing in the journal ties the parts together. The memlog promotes tasks "when needing atomicity", which this design can't deliver.

**Proposed rule (new AD-7b, "Pipeline exclusion and correlation"):**

> Steps 2 to 6 of every non-`Read` pipeline run while holding the machine-wide named mutex `Local\OneNoteMcp.Write`, acquired with the pipeline deadline. If it isn't acquired in time, return `ComBusy` with `retryable: true`. This makes server-against-server writes on one machine linearisable. Edits made in OneNote's own UI stay best-effort (H2), and the docs say so. Each MCP tool call gets a `correlationId` (GUID), carried in logs and in every journal entry it writes. A promoted task may hold the mutex across its sub-pipelines (re-entrant within the task), and it records each step's journal entry under the one `correlationId`. Tasks never claim atomicity. On a mid-task failure they return the error with `meta.completedSteps` and the `correlationId`, so the user can restore the partial changes from the journal.

Counter-argument, worth weighing: a global mutex means a 2.9 s docx Publish in process A blocks writes in process B. That's acceptable. Writes already serialise on ONENOTE.EXE, and reads don't take the mutex.

---

## Lesser holes (worth one line each in the spine)

- **H7. Two meanings of "restore".** Journal restore (AD-8) and recycle-bin restore (findings constraint 6, needs disambiguation) are different operations. Neither is a COM method, so AD-14's `snake_case`-of-the-COM-method naming doesn't apply. Name them now (`restore_snapshot`, `restore_from_recycle_bin`), class them (`Create` and `HierarchyWrite`), and state that `restore_snapshot` creates a new page under `parentId` and, after creating it, journals the restored page's empty state as `snapshotKind: none`.
- **H8. A journal path change across processes.** The journal path is in settings and re-read "at each access check". Process A, started before the user changed it, keeps writing to the old path, and process B's `restore_snapshot` lists only the new one. Rule: the journal path is read per write, and `SettingsStore` keeps `journalPathHistory`. Restore and retention scan every path in it.
- **H9. Retention owner and timing.** No AD says who runs retention or when. Rule: every process runs it at startup and every 24 h, holding the named mutex `Local\OneNoteMcp.Retention` (try-acquire; skip if it's held).
- **H10. Resolving the owning notebook of recycle-bin objects and `UpdateHierarchy` payloads.** AD-6 resolves from one target ID. A payload whose root is the Notebooks root, or that reparents a section across notebooks (move is deferred, but AD-5 says rounds are additive), needs every ID resolved. This is covered by H1's `HierarchyWrite` rule. Recycle-bin pages resolve through `OneNote_RecycleBin` up to the notebook. A fixture should prove it.
- **H11. `InvalidXml` messages and logging.** AD-13 forbids logging XML bodies, but XSD validation messages quote content. Rule: `message` may quote a line and column and element names, never text values, and the logger records `code` and `hresult` only.
- **H12. The `allowSilentActivation` race.** Process A (setting true) silently activates OneNote. Process B (setting false) then finds ONENOTE.EXE running and proceeds, which is fine. But A's idle release (AD-3) leaves a windowless `-Embedding` instance running indefinitely. Findings constraint 16 and the launch-blocking observation suggest that matters. Rule: a process that silently activated OneNote records that it did, and its logs and `meta` say so. It still never kills OneNote (AD-3).

## What holds up

AD-1, AD-2, AD-3, AD-11, AD-13, AD-15 and AD-16 survive pairing. AD-3's lifecycle steps trace cleanly to findings constraints 7, 12, 13, 14, 15 and 16. AD-6's "never from the ID string" correctly reflects the spike result that IDs don't embed parents. AD-12's rules match findings row 29 and constraint 4 exactly, apart from the AD-4 conflict in H4.
