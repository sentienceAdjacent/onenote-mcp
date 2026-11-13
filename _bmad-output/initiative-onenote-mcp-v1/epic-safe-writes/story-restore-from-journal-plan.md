---
title: 'restore_from_journal'
type: 'feature'
ticket: '11'
created: '2026-11-12'
status: 'draft'
route: 'full'
route_source: 'auto'
review: ''
review_source: ''
lenses_ran: []
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
warnings: ['oversized']
deferred: []
---

<intent-contract>

## Intent

**Problem:** Every page write (2.9, 2.10) leaves a journal entry (2.8), but nothing reads one back: a page Claude overwrote can't be recovered, epic 2's Done-when 3 and the initiative's tracer path (write, snapshot, restore) are open, and the gateway has no `OpenHierarchy`, which the `Recovered` fallback needs and epic 5's entry 3 builds on.

**Approach:** A `restore_from_journal` tool, a `Create`-class write through the existing pipeline (AD-7 table, AD-8). It reads one entry, finds its notebook by `notebookPath` only, picks the section (AD-8 order; a protected entry only its own `SectionKey` section), creates a page there and writes the snapshot's content into it with every `objectID` stripped, as OneMore's Duplicate Page does. The gateway gains section creation by parent ID (`OpenHierarchy`, `cftSection`).

## Boundaries & Constraints

**Always:**
- AD-7 order. Step 1 (before the lock): the entry is found and read, its kind checked, its notebook found among the open notebooks by `NotebookKey` equal to `notebookPath` (never `notebookId`), then `WriteAccess` on that notebook (the grant dialog as for `create_page`). No read grant is ever needed or asked for, protected entries included (AD-21 Writes).
- Steps 3–4 under the lock, re-reading the notebook's sections live. Target, for a page entry from an unprotected section: the live section with the entry's `parentId` in that notebook; else the one live section whose group/name path equals `parentNamePath` (ignoring case, as Windows names compare); else a live section named `Recovered` directly under the notebook, created in step 7 if none exists. "Live" excludes the recycle bin (`isInRecycleBin`, `isDeletedPages`, `OneNote_RecycleBin`).
- A protected entry (`sectionKey` set): only the live section whose `SectionKey` equals the marker and that is still `encrypted`. Locked: `SectionLocked`. Absent or no longer protected: `ObjectNotFound`, `userAction` naming the entry file, which stays for its 30 days. Never `parentId`, `parentNamePath` or `Recovered` (AD-8; 2.3 Q6).
- `hierarchy` and `onenoteBackup` entries: `NotSupported` before any COM call, `userAction` naming the entry file (or the backup file, saying the user can open it in OneNote, without promising it brings the section back; AD-8).
- The page XML sent is built in `OneNoteMcp.Xml` from the snapshot: parsed with DTD prohibited, root `ID` set to the new page's, every `objectID` removed, then the AD-16 gate. Images and ink keep their inline `one:Data`.
- Result: `meta.pageId` (the new page) and `meta.parentId` (its section). Warnings: `binaryMissing` with counts by kind when anything wasn't restored (the entry's `missingBinary` plus anything the XML build dropped), never IDs or content; `restoredElsewhere` saying `parentNamePath` or `Recovered` when the entry's `parentId` wasn't used (an existing `Recovered` is simply used).
- Never overwrites: the only content write goes to the page this call created. The new page is dropped from the search cache whatever the outcome (AD-7).
- Messages and logs carry IDs, counts and the entry's file name only: never a title, page content or `parentNamePath` of a protected entry (AD-9, AD-13, AD-21). `outcomeUnknown` per AD-9 for an ambiguous failure in step 7.
- Gateway: section creation by parent object ID only (`bstrRelativeToObjectID` + a relative name ending `.one`); never an absolute path (AD-7 `Create` row). Write `CallKind`, never retried. The relative name is checked before COM (no `..`, `\`, `/`, or characters a Windows file name can't hold; spike 5.1 constraint 5).
- Tool contract snapshot updated; `<Version>` stays `0.9.0-rc.1` (2.4 Q3: later contract changes ride on it until a release). Tool row only in the guide skill and README (recipes are 2.12's).

**Never:** overwriting or merging into an existing page; using `notebookId`; restoring hierarchy or backup entries, or opening a backup; a journal listing tool; `NavigateTo`; deleting pages or sections; a read-back for `Create`; automatic retries; spine edits (spine sync goes to `deferred-work.md`); `OpenHierarchy` by absolute path, section groups or notebooks (epic 5, entry 3); touching OneNote, setting `ONENOTE_MCP_INTEGRATION`, or running the server exe.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Restore in place | page entry, `parentId` live | new page in that section with the snapshot's content; `meta.pageId`, `meta.parentId` | — |
| Section re-made | `parentId` gone, a section at `parentNamePath` | new page there | warning `restoredElsewhere` (`parentNamePath`) |
| Section gone | neither matches | new page in `Recovered` (created if absent) | warning `restoredElsewhere` (`Recovered`) |
| Notebook not open | no open notebook's key = `notebookPath` | nothing written | `ObjectNotFound`, `userAction`: open the notebook at that path |
| No write grant | target notebook ungranted | grant dialog, as `create_page` | `WriteNotGranted` if declined |
| Target locked | chosen section `locked` | nothing written | `SectionLocked` (Q6) |
| Protected, section live | marker section encrypted, unlocked | new page there, write grant only, no read-grant dialog | — |
| Protected, locked | marker section locked | nothing written | `SectionLocked`, retryable after unlock |
| Protected, gone or unprotected | no live section with that key, or not `encrypted` | nothing written | `ObjectNotFound`, `userAction` naming the entry file |
| Hierarchy / backup entry | `snapshotKind` not `page` | no COM call | `NotSupported`, `userAction` naming the file |
| Unknown entry | no file for that ID in the journal folder | no COM call | `ObjectNotFound` (pruned after 30 days, or wrong ID) |
| Bad argument | not a GUID, or empty | nothing read | `InvalidArgument` naming the argument |
| Unreadable entry | gzip/JSON/DPAPI/XML failure, or a newer `schemaVersion` | no COM call | `JournalFailed` (newer schema: `NotSupported`) |
| Missing binaries | `binaryComplete: false`, or attachments dropped (Q2) | page restored without them | warning `binaryMissing`, counts by kind |
| Content write fails | page created, then e.g. `ModalDialogOpen` on `UpdatePageContent` | an empty page is left | the step's code; message names the empty page's ID (Q3) |
| Ambiguous step 7 | `Timeout` / `ComUnavailable` / `Cancelled` / `0x800706BE` | — | `outcomeUnknown` |

</intent-contract>

## Open Questions

1. **How does the caller name the entry?** Nothing returns an entry's ID today: the write tools log it, and the file name holds it (`{takenAtUtc}-{pid}-{entryId:N}.json.gz`).
   - (a) `restore_from_journal(entryId)`, and `update_page_content` and `delete_page_content` add `meta.journalEntryId` to their success result. A failure after the snapshot (step 7 or the read-back) names the entry's ID in its message, since AD-9's error `meta` holds only `correlationId`. The server finds the file by its name pattern; no scan.
   - (b) As (a), and `entryId` or `pageId`: with `pageId`, the newest page entry for that page, scanning newest-first and reading only each file's leading JSON members. Serves "undo yesterday's change" in a later session without the ID.
   - (c) `entryId` only, with no change to the write tools: the user reads the ID from the journal folder's file names.
   Recommended: (a). It meets the Verify exactly and keeps the write tools' change to one meta field. A later-session lookup is better served by a listing tool (titles, dates; protected titles withheld) than by guessing the newest entry, so I'd record that as deferred work rather than build half of it here.
2. **Attachments and media.** The snapshot keeps each `one:InsertedFile`'s `pathCache` (a `%TEMP%\{GUID}.bin` copy OneNote made during the snapshot read and never deletes; sitting B3) and `pathSource`, but not the bytes (2.8 Q4). OneMore's Duplicate Page sends them unchanged.
   - (a) Drop every `one:InsertedFile` and `one:MediaFile` from the restored page and count them in `binaryMissing`. Nothing reads a path from a journal file.
   - (b) Keep an attachment whose `pathCache` exists and, made canonical, lies under the user's temp folder, sending only `pathCache`; drop and count the rest. Unprobed: whether `UpdatePageContent` takes a `pathCache`-only `InsertedFile` on a new page; the live sitting would settle it.
   - (c) Send the paths through 2.9's file-path dialog.
   Recommended: (a). It matches 2.8 Q4 (attachments are listed as not kept) and never turns a journal file into a way to read files into a notebook. (b) is a cheap follow-up once probed; I'd log it in deferred work.
3. **The page is created but its content write fails** (for example `ModalDialogOpen`: spike 2.2 saw a modal block only `UpdatePageContent`, not `CreateNewPage`). The gateway can't delete a page until epic 5.
   - (a) Fail with that step's own code and retry rule; the message names the empty page's ID and the `userAction` says to delete it in OneNote before retrying (a retry makes another page). `outcomeUnknown` only for AD-9's ambiguous codes, as the pipeline does now.
   - (b) Add an optional `intoPageId` so a retry fills the empty page this tool made, checked to be empty.
   Recommended: (a). (b) adds an argument that writes into an existing page, which is the overwrite AD-8 rules out; an empty page is easy to see and delete.
4. **Section page templates.** OneNote applies a section's default template to a page `CreateNewPage` makes; OneMore's Duplicate Page deletes those template objects (top-level `Outline`, `Image`, `InkDrawing`, `InsertedFile`, `MediaFile`) before writing. `UpdatePageContent` keeps any Outline it isn't sent (AD-7), so template content would stay beside the restored content.
   - (a) Read the new page (piBasic), delete each such object with `DeletePageContent` (it is the page this call created), then re-read for the date and write.
   - (b) Leave template content in place; one read for the date, then the write.
   Recommended: (a), so a restore gives back the page as it was. It costs a delete per template object, usually none.
5. **Gateway shape for `OpenHierarchy`.**
   - (a) A narrow `CreateSectionAsync(parentId, sectionFileName)`: section creation by parent ID only, as the ticket names it. Epic 5's entry 3 adds `CreateSectionGroupAsync` and `CreateNotebookAsync` beside it, and epic 4 its own `AppState` open, so no gateway method can create a section from an absolute path.
   - (b) One general `OpenHierarchyAsync(path, relativeToObjectId, createType)` mirroring COM, used here for sections only.
   Recommended: (a). The gateway then can't express what AD-7 forbids, and each epic adds the one form it owns.
6. **An unprotected entry whose chosen section is now locked** (it was protected after the snapshot, or `Recovered` is locked).
   - (a) `SectionLocked`, retryable after the user unlocks it, as for `create_page`.
   - (b) Fall through to the next candidate (`parentNamePath`, then `Recovered`).
   Recommended: (a). The restore lands where AD-8's order says or not at all; falling through would put the page somewhere the user didn't expect because of a lock.

## Code Map

- `src/OneNoteMcp.Core/IOneNoteGateway.cs:11-75` -- no `OpenHierarchy` yet. Add `CreateSectionAsync(string parentId, string sectionFileName, CancellationToken)` (Q5) beside `CreateNewPageAsync` (`:52`), documented as a `Create` write the pipeline alone calls.
- `src/OneNoteMcp.Com/ComGateway.cs:244-257` -- `CreateNewPageAsync` is the shape: `RunAsync(method, app => ..., CallKind.Write, ct)` (`:414`; `CallKind` `:840`). The interop is already declared (`Interop/IApplication.cs:32`, slot 9; `cftSection` in `Interop/Enums.cs:45`). Refuse a bad name with `InvalidArgument` before COM, as `BadExpectedDate` does for dates. `HResultMap.cs`: `0x80042004` → `ObjectNotFound` (`:100`), `0x80042006` → `Internal` (`:102`); the name check keeps both from being reached.
- `tests/OneNoteMcp.Tests/FakeApplication.cs:90` -- `OpenHierarchy` throws `NotSupportedException`; script it through `OnCall` like `CreateNewPage` for gateway tests (`ComGatewayLifecycleTests.cs:507-530` shows the pattern).
- `src/OneNoteMcp.Core/Writes/WritePipeline.cs:78` -- `RunAsync` runs `AuthorizeAsync` (grant dialog on `WriteGrantMissing`, deadline paused), lock, abandoned-write `ComBusy`, `ReadAsync`, `Validate`, `CheckBasisAsync`, `SnapshotAsync`, `CallAsync`, `AfterCallAsync`; `outcomeUnknown` only for step-7 failures (`IsAmbiguous`, `:253`); `finally` evicts `WrittenPageIds` (`:229`). Unchanged.
- `src/OneNoteMcp.Core/UseCases/CreatePageUseCase.cs:17-79` -- the pattern for `RestoreFromJournalUseCase.cs` (private `WriteOperation<TResult>`, `WrittenPageIds`, `meta.pageId`). Warnings travel in the result type, as `PageWriteResult` (`Writes/PageWrite.cs:20`) and `UpdatePageContentUseCase.cs:32-34` do.
- `src/OneNoteMcp.Core/Access/WriteAccess.cs:48` -- `CheckAsync(objectId, ...)` walks parents, so a notebook ID fails (`notInsideANotebook`). Add a sibling that takes the already-found `OpenNotebook` (ID and `Key`) and applies the same grant rules (`EnsureWritable`, empty path → `WriteNotGranted`, missing → `WriteGrantMissing`), sharing the existing code path.
- `src/OneNoteMcp.Core/Access/OpenNotebooks.cs:19` -- `Find(hierarchyXml)` gives `OpenNotebook(Id, Name, DisplayName, Path, Key)`; match `Key` to `entry.NotebookPath` with `GrantKeys.Comparer` (`GrantKeys.cs:10`). `GrantKeys.SectionKey` (`:22`) for the protected match.
- `src/OneNoteMcp.Xml/` -- no helper lists a notebook's sections with their group path (`EncryptedSections.From` `:40-54` does it for encrypted ones only). Add a public `NotebookSections.Find(sectionsScopeXml)` → `(Id, Name, Path, Groups, Encrypted, Locked, InRecycleBin)` that skips the `OneNote_RecycleBin` group. `HierarchyNode` (`HierarchyNode.cs:21`; `InRecycleBin` covers `isInRecycleBin` and `isDeletedPages`).
- `src/OneNoteMcp.Xml/` -- add a public `RestoredPage` with `Build(snapshotXml, newPageId)` → XML plus dropped counts (Q2), and `TemplateObjectIds(newPageBasicXml)` (Q4). `OneNoteXml.Parse` (`OneNoteXml.cs:18`, internal; DTD prohibited) is reachable only inside Xml, so the transform lives there. `SchemaValidation.Problems` (`:85`) is the AD-16 gate.
- `src/OneNoteMcp.Core/Journal/JournalStore.cs:95` -- `Read(path)` decompresses, decrypts a protected entry, throws only `JournalFailed`. `JournalFiles.cs:51,100` -- the name and pattern; add a finder for `*-{entryId:N}.json.gz` in `settings().Journal.Path` (`SettingsSnapshot.cs:30,234`).
- `src/OneNoteMcp.Core/Journal/PageSnapshot.cs:84-88` -- how `NotebookPath`, `ParentId`, `ParentNamePath` (groups and section `name`, `/`-joined) and `SectionKey` were made; the restore matches the same forms.
- `src/OneNoteMcp.Core/Results/ToolWarnings.cs` -- add `binaryMissing` and `restoredElsewhere` as `public const string` with docs (`NoBasis` `:52` is the shape). `ToolMeta.cs:15-28` -- `PageId` and `ParentId` exist; Q1 (a) adds `JournalEntryId`. `ToolErrors.cs:46,69-85` -- default `userAction` per code; the restore passes its own where AD-8 names one.
- `src/OneNoteMcp.Core/Writes/PageWrite.cs` -- Q1 (a): `PageSnapshot.TakeAsync` already returns `JournalWritten.EntryId`; carry it into `PageWriteResult` and on to `meta.journalEntryId` in both page-write use cases, and into the message of a failure after step 6.
- `src/OneNoteMcp.Server/Tools/CreatePageTool.cs:13-46` -- the tool shape (`ReadOnly=false`, `Destructive=false`, `Idempotent=false`, `OpenWorld=false`, `ElicitationGrantPrompt.For(server)`). `ServerSetup.cs:107-157` -- register the use case and tool; argument names come from the schema (`ArgumentNames.cs:25`).
- Lists of tools: `ToolContractTests.cs:81-87`, `Contract/tool-contract.json` (contract `0.9`), `McpSdkBindingTests.cs:364-370` (fifteen → sixteen; add `CreateSectionCalls` to the "nothing written" asserts), `NavigateToToolTests.cs:120`, `spikes/stress-harness/Calls.cs:70` and `Program.cs:88`, `PluginPackagingTests.cs:244`, `plugin/skills/guide/SKILL.md:20-40`, `README.md` tool table.
- Tests to copy: `CreatePageToolTests.cs:44-66,873-982` (tree, `Grant()`, `UseCase()`, `Ok`/`Error`); `DeletePageContentToolTests.cs:87,522` (temp journal through `new SettingsStore(path, journalPath)`, a real `PageSnapshot` to make entries); `FakeGateway.cs:144-200` (add `CreateSectionCalls`, `OnCreateSection`, `CreateSectionFailure`, and let `Pages` serve the created page). Fixtures: `Fixtures/mcp-test/page/image-BinaryData.xml` (inline `one:Data`), `Fixtures/synthetic/update-page-Basic.xml`.
- `tests/OneNoteMcp.Integration/TestNotebook.cs:21,135,459` -- the MCP Test guard; `CreatePageIntegrationTests.cs` and `UpdatePageContentIntegrationTests.cs` are the shape for the opt-in test.

## Tasks & Acceptance

**Execution:**
- [ ] `src/OneNoteMcp.Xml/RestoredPage.cs`, `NotebookSections.cs` + tests -- the transform (ID set, every `objectID` removed, `one:Data` kept, Q2's drops counted, DOCTYPE refused) and the section list (group paths, recycle bin skipped), against the fixtures.
- [ ] `IOneNoteGateway.cs`, `ComGateway.cs`, `FakeApplication.cs`, `ComGatewayTests` -- `CreateSectionAsync` by parent ID (Q5): name checks before COM, `cftSection`, `CallKind.Write`, never retried.
- [ ] `src/OneNoteMcp.Core/Journal/` -- find an entry file by ID in the configured folder; refuse a newer `schemaVersion`.
- [ ] `src/OneNoteMcp.Core/Access/WriteAccess.cs` -- the check for an already-found notebook.
- [ ] `src/OneNoteMcp.Core/UseCases/RestoreFromJournalUseCase.cs` -- the `Create` operation: step 1, steps 3–4 (target choice, protected rule, lock check, XML build and gate), step 7 (Design Notes), result and warnings.
- [ ] `ToolWarnings.cs`, `ToolMeta.cs`, `PageWrite.cs`, `UpdatePageContentUseCase.cs`, `DeletePageContentUseCase.cs` -- the new warnings; Q1's `meta.journalEntryId`.
- [ ] `src/OneNoteMcp.Server/Tools/RestoreFromJournalTool.cs`, `ServerSetup.cs` -- `restore_from_journal(entryId)`.
- [ ] `tests/OneNoteMcp.Tests/RestoreFromJournalToolTests.cs`, `FakeGateway.cs` -- every matrix row, Q2–Q6 behaviour, entries made by a real `PageSnapshot` (one protected, DPAPI), lock free after each outcome, cache entry dropped.
- [ ] Contract snapshot, tool lists, binding test, stress harness, guide skill and `README.md` tool rows (call when the user asks to bring back a page's earlier version).
- [ ] `tests/OneNoteMcp.Integration/RestoreFromJournalIntegrationTests.cs` -- opt-in, MCP Test only: update a page, restore its entry, the new page holds the earlier Outlines and image. Not run here.
- [ ] `deferred-work.md` -- spine sync for what Q1–Q6 decide (AD-8, AD-9 warnings, AD-10's gateway note for epic 5); Q1's later-session lookup; Q2 (b) if not chosen.

**Acceptance Criteria:**
- Given any refusal before step 7, when the pipeline ends, then `CreateCalls`, `CreateSectionCalls` and `UpdateCalls` are empty and the lock is free.
- Given a protected entry restored with the write grant only, when the result returns, then no read-grant dialog was raised and no message, warning or log line carries its title, content or `parentNamePath`.
- Given an existing page, when restore runs, then no `UpdatePageContent` targets any page but the one `CreateNewPage` returned.
- Given `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests`, then both pass; `dotnet test tests/OneNoteMcp.Integration` without the variable passes or skips.

## Implementation Notes

## Plan Change Log

## Review Triage Log

## Design Notes

- **Step 7, in order** (Q4 (a) assumed): `CreateSectionAsync(notebookId, "Recovered.one")` only when step 3 found no `Recovered`; `CreateNewPageAsync(section)`; `GetPageContentAsync(new, Basic)`; `DeletePageContentAsync` for each template object, then one more read for the date; `UpdatePageContentAsync(built XML, that date)`. The date comes from the new page, so `force: false` and the gateway's date rule hold.
- **Evidence for the content write:** OneMore's `Commands/Page/DuplicatePageCommand.cs:97-139` creates the page, deletes the template's top-level objects, sets the root `ID`, removes every `objectID` ("let OneNote generate new IDs"), keeps `QuickStyleDef`, `TagDef`, `PageSettings` and the `Title` as read, and sends the whole page with one update. It passes `force: true` and `DateTime.MinValue` (`OneNote.cs`, `Update`); we don't. Learned from, not copied; the README credit and `research-onemore-lessons.md` get a row.
- **Name paths:** a section's hierarchy `name` has no `.one`, and `parentNamePath` is groups plus that name joined by `/` (`PageSnapshot.cs:87`), so the match compares segment by segment, ignoring case. File names can't hold `/`, so the split is safe.
- **`Recovered`:** a live section of that name (ignoring case) directly under the notebook is used as found; otherwise step 7 creates it. Spike 5.1 constraint 5: an existing name returns the existing ID, so a create racing another process's still lands in one section.
- **Spine sync, not edits:** AD-8 gains Q2, Q3, Q4 and Q6; AD-9's warnings list gains `binaryMissing` and `restoredElsewhere`; Q1 (a) adds `meta.journalEntryId` to AD-9's success meta for page writes.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- 0 warnings, 0 errors.
- `dotnet test tests/OneNoteMcp.Tests` -- all pass.
- `dotnet test tests/OneNoteMcp.Integration` (no variable) -- pass or skip.
- `dotnet build spikes/stress-harness -c Release` -- builds.

**Manual checks (live sitting with Phill, not run by the build):**
- `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter RestoreFromJournalIntegrationTests` on MCP Test.
- From Claude Code (the ticket's Verify): overwrite an MCP Test page with `update_page_content`, then `restore_from_journal` with its `meta.journalEntryId`; a new page holds the earlier content, title, styles, tags and image.
- Delete the original section, restore again: the page lands by `parentNamePath` if a same-named section exists, else in `Recovered` (created once, reused next time).
- A section with a page template: the restored page carries no template content (Q4).
- MCP Test's protected section: write grant only, unlocked → restored there, no dialog; locked → `SectionLocked`.
- A page with an attachment: restored without it, `binaryMissing` counts it (Q2).
