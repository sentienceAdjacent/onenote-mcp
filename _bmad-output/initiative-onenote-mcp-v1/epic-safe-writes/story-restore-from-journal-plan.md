---
title: 'restore_from_journal'
type: 'feature'
ticket: '11'
created: '2026-11-12'
status: 'built'
baseline_revision: '171021992c9c209810695d2fee682c9aeccd58fa'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
followup_review_recommended: true
context:
  - '{project-root}/CLAUDE.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
warnings: ['oversized']
deferred:
  - summary: >-
      SettingsReplaceTests.ASettingsFileHeldForAMoment_StillSaves fails intermittently under full-suite load.
    evidence: |-
      Pre-existing and untouched by 2.11 (no Settings code or that test in the diff). In this lane it failed in 4 of 7 full runs, including 1 of 2 runs with the new 2.11 test classes filtered out, and passed 3 of 3 alone. A 100 ms file hold against a 0.5 s retry budget.
    location: >-
      tests/OneNoteMcp.Tests/SettingsReplaceTests.cs
    severity: low
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
- Result: `meta.pageId` (the new page) and `meta.parentId` (its section). Warnings: `binaryMissing` with counts by kind when anything wasn't restored (the entry's missing images, plus each attachment or media file not restored: dropped by Q2's rule or refused by OneNote), never IDs or content; `restoredElsewhere` saying `parentNamePath` or `Recovered` when the entry's `parentId` wasn't used (an existing `Recovered` is simply used).
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
| Missing binaries | images missing in the entry, or an attachment whose temp copy is gone or outside the temp folder (Q2) | page restored without them | warning `binaryMissing`, counts by kind |
| Attachment kept | `pathCache` a `{GUID}.bin` directly in the user's temp folder, present | sent with `pathCache` only, `pathSource` removed | — |
| Attachment refused | content write with kept attachments fails with a definite code; re-read shows the page empty | sent once more without attachments | warning `binaryMissing` (refused by OneNote) |
| Content write fails | page created, then e.g. `ModalDialogOpen` on `UpdatePageContent` | an empty page is left | the step's code; message names the empty page's ID (Q3) |
| Ambiguous step 7 | `Timeout` / `ComUnavailable` / `Cancelled` / `0x800706BE` | — | `outcomeUnknown` |

## Decisions (Phill, 2026-11-12, plan approved; guardrails for Q2 and the retry are the coordinator's reading of his answer)

- **Q1 (a):** `restore_from_journal(entryId)`. `update_page_content` and `delete_page_content` add `meta.journalEntryId` to their success result; a failure after their snapshot (step 7) names the entry's ID in its message (AD-9's error `meta` stays `{correlationId}`). The file is found by its name pattern in the configured journal folder; no scan. A later-session lookup (listing tool) goes to deferred work.
- **Q2 (b), with guardrails:**
  - Restore sends only `pathCache`, never `pathSource`: `pathSource` is removed, so nothing the original author's path names is read.
  - A `pathCache` is kept only when its canonical form (`FilePathGate.Canonical`, which refuses network, device and relative paths) is a file directly in the current user's temp folder (`Path.GetTempPath()`, made canonical the same way), named `{GUID}.bin` as OneNote names it, and the file exists now. Otherwise the element is dropped and counted in `binaryMissing`.
  - `one:MediaFile`: the same rule. Images and ink keep their inline `one:Data` (unchanged); an element with any other path attribute (`one:File@path`) is dropped and counted.
  - Unprobed whether OneNote accepts an `InsertedFile` with only `pathCache`: an opt-in integration probe (below) prints `RESULT restore-attachment: kept|dropped|error <hex>`; the live sitting runs it.
  - **If OneNote refuses the write with attachments kept, the restore doesn't fail for that:** when the content write sent at least one kept attachment or media file and fails with a code that isn't ambiguous (AD-9: not `Timeout`, `ComUnavailable`, `Cancelled`, `0x800706BE`) and isn't `ModalDialogOpen`, `ComBusy`, `SectionLocked` or `StaleWrite`, the server re-reads the new page (piBasic). If that read shows no top-level content object (`Outline`, `Image`, `InkDrawing`, `InsertedFile`, `MediaFile`), it sends the page once more with every attachment and media file dropped, using that read's date, and counts them in `binaryMissing` (its message says OneNote refused them). If the page already holds content, or the second write fails, Q3 applies. This is one retry of the server's own payload into the empty page this call created, checked empty first; it is not a retry of a caller's write, so AD-7/AD-17's "never auto-retry writes" stance (CLAUDE.md, OneMore difference) isn't crossed. Flag it in the spine sync.
- **Q3 (a):** a failed content write fails with that step's code and retry rule; the message names the empty page's ID and the `userAction` says to delete it in OneNote before retrying. `outcomeUnknown` only for AD-9's ambiguous codes.
- **Q4 (a):** read the new page, delete each top-level template object (`Outline`, `Image`, `InkDrawing`, `InsertedFile`, `MediaFile`) with `DeletePageContent`, re-read for the date, then write.
- **Q5 (a):** gateway `CreateSectionAsync(parentId, sectionFileName)`; epic 5 adds its siblings.
- **Q6 (a):** an unprotected entry's chosen section that is locked gives `SectionLocked`, retryable; no fall-through.
- **Probe:** `tests/OneNoteMcp.Integration/WriteProbes/` or beside `HtmlLocalRefProbe.cs`, same pattern: opt-in (`ONENOTE_MCP_INTEGRATION=1`), MCP Test only, behind `TestNotebook`'s guard. It snapshots an MCP Test page holding an attachment (through `update_page_content` or a direct `PageSnapshot`), restores the entry, and prints `RESULT restore-attachment: kept|dropped|error <hex>`. Not run in the build lane.

</intent-contract>

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
- `src/OneNoteMcp.Core/Writes/FilePathGate.cs:43` -- `Canonical(path, out refusal)` (junctions resolved; UNC, device and relative refused) for Q2's temp-folder check. The check lives in Core (it touches the file system); the Xml transform takes a predicate deciding which `pathCache` values to keep.
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
- [x] `src/OneNoteMcp.Xml/RestoredPage.cs`, `NotebookSections.cs` + tests -- the transform (ID set, every `objectID` removed, `one:Data` kept, Q2's drops counted, DOCTYPE refused) and the section list (group paths, recycle bin skipped), against the fixtures.
- [x] `IOneNoteGateway.cs`, `ComGateway.cs`, `FakeApplication.cs`, `ComGatewayTests` -- `CreateSectionAsync` by parent ID (Q5): name checks before COM, `cftSection`, `CallKind.Write`, never retried.
- [x] `src/OneNoteMcp.Core/Journal/` -- find an entry file by ID in the configured folder; refuse a newer `schemaVersion`.
- [x] `src/OneNoteMcp.Core/Access/WriteAccess.cs` -- the check for an already-found notebook.
- [x] `src/OneNoteMcp.Core/UseCases/RestoreFromJournalUseCase.cs` -- the `Create` operation: step 1, steps 3–4 (target choice, protected rule, lock check, XML build and gate), step 7 (Design Notes), result and warnings.
- [x] `ToolWarnings.cs`, `ToolMeta.cs`, `PageWrite.cs`, `UpdatePageContentUseCase.cs`, `DeletePageContentUseCase.cs` -- the new warnings; Q1's `meta.journalEntryId`.
- [x] `src/OneNoteMcp.Server/Tools/RestoreFromJournalTool.cs`, `ServerSetup.cs` -- `restore_from_journal(entryId)`.
- [x] `tests/OneNoteMcp.Tests/RestoreFromJournalToolTests.cs`, `FakeGateway.cs` -- every matrix row, Q2–Q6 behaviour, entries made by a real `PageSnapshot` (one protected, DPAPI), lock free after each outcome, cache entry dropped.
- [x] Contract snapshot, tool lists, binding test, stress harness, guide skill and `README.md` tool rows (call when the user asks to bring back a page's earlier version).
- [x] `tests/OneNoteMcp.Integration/RestoreFromJournalIntegrationTests.cs` -- opt-in, MCP Test only: update a page, restore its entry, the new page holds the earlier Outlines and image. Not run here.
- [x] `tests/OneNoteMcp.Integration/RestoreAttachmentProbe.cs` -- the Decisions' probe (`HtmlLocalRefProbe.cs` pattern). Not run here.
- [x] `deferred-work.md` -- spine sync for what Q1–Q6 decide (AD-8: Q2's guardrails and the one empty-page retry, Q3, Q4, Q6; AD-9 warnings and `meta.journalEntryId`; the gateway note for epic 5); Q1's later-session lookup; the live probe result pending.

**Acceptance Criteria:**
- Given any refusal before step 7, when the pipeline ends, then `CreateCalls`, `CreateSectionCalls` and `UpdateCalls` are empty and the lock is free.
- Given a protected entry restored with the write grant only, when the result returns, then no read-grant dialog was raised and no message, warning or log line carries its title, content or `parentNamePath`.
- Given an existing page, when restore runs, then no `UpdatePageContent` targets any page but the one `CreateNewPage` returned.
- Given `dotnet build OneNoteMcp.slnx -warnaserror` and `dotnet test tests/OneNoteMcp.Tests`, then both pass; `dotnet test tests/OneNoteMcp.Integration` without the variable passes or skips.

## Implementation Notes

- Gateway: `CreateSectionAsync` refuses an empty parent or a name failing `SectionFileNames.Problem` (Core; ends `.one`, no `..`, separators, invalid characters, reserved device names, or a leading/trailing space or dot) with `InvalidArgument` before COM.
- `JournalFiles.Find(folder, entryId)` matches `*-{id}.json.gz` (32-hex or dashed) by name only; `JournalStore.Read` now refuses a newer `schemaVersion` with `NotSupported` before decrypting.
- `WriteAccess.CheckNotebook(OpenNotebook)` shares `CheckAsync`'s grant rules (one private `Granted`). Step 1 lists open notebooks with `GetHierarchy("", Sections)`, as the grant dialog does.
- Q1: `PageWrite.JournalEntryId` is set in step 6; both page-write use cases put it in `meta.journalEntryId`, and `CallNamingEntryAsync` appends the entry ID to a step-7 failure's message (code, HRESULT, flags and userAction unchanged).
- Choices the plan left open (listed in `deferred-work.md` for the spine sync): unparsable/DOCTYPE/non-page snapshot XML is `JournalFailed`, a schema-gate failure is `InvalidXml`; the page is created `blankPageWithTitle` when the snapshot has a title, else `blankPageNoTitle`; an ambiguous `parentNamePath` (more than one live match) falls through to `Recovered`; `binaryMissing` kinds are image, ink, attachment, media, other.
- After review: `WriteOperation.FailureNote` (PageWrite: the journal entry; restore: the created page and the delete-it guidance) is appended by the pipeline to its own step-7 cancel and deadline messages. The AD-16 gate runs on both send variants in step 4 and on the exact XML before each content write. A file found by name that holds another entry ID is `JournalFailed`. "Notebook not open" and "section no longer protected" are `ObjectNotFound` with `retryable: true` (AD-9); "protected section gone" stays non-retryable. `SectionFileNames` also refuses a leading dot.
- Tests: `RestoreFromJournalToolTests` (37, every matrix row), `RestoredPageTests` (XML, sections, journal find, Q2 rule, file names), gateway tests in `ComGatewayLifecycleTests`, Q1 asserts in the update/delete tool tests. Integration test and probe written, not run (opt-in).

## Plan Change Log

## Review Triage Log

### 2026-11-12 — Review pass
- verdicts: 7 findings — high 0, medium 4, low 3, false 0, maybe-false 0
- findings:
  - `[medium]` `[patch]` Notebook-not-open `ObjectNotFound` came back not retryable; AD-9's row makes this AD-8 case retryable after the user opens the notebook — `retryable: true`, asserted in the test.
  - `[medium]` `[patch]` A protected entry's "gone" and "no longer protected" shared one non-retryable error; AD-9 makes the second retryable after the user protects the section again — split into two errors (gone: not retryable; unprotected: retryable, `userAction` to protect it again at the same path); the "gone" test was found to be testing the other case and was corrected.
  - `[medium]` `[patch]` A step-7 cancel or deadline (messages the pipeline builds itself) named neither the journal entry (update/delete, Q1) nor restore's new page (Q3) — `WriteOperation.FailureNote` hook appended by `WritePipeline` to its step-7 `Cancelled` and deadline messages; `PageWrite` and the restore supply it; two tests; the deferred-work note removed.
  - `[medium]` `[patch]` The AD-16 gate checked a stand-in build, never the XML sent, and never the Q2 retry's build — `Validate` gates both variants; `CallAsync` gates the exact XML before each `UpdatePageContent` (`InvalidXml`, named page via Q3). The asked-for "no-files variant fails" test can't exist under the 2013 XSD (dropping files never invalidates); a kept-variant failure test was added instead.
  - `[low]` `[patch]` `SectionFileNames.Problem` let a leading dot through while its message and notes said it refused one — leading dot refused; `.hidden.one` and ` lead.one` cases added.
  - `[low]` `[patch]` README status sentence and the "(planned)" Undo row contradicted the new tool row — `restore_from_journal` added to the status sentence; "(planned)" dropped.
  - `[low]` `[patch]` An entry file found by name wasn't checked to hold the requested entry — `JournalFailed` when `EntryId` differs; one test.

## Design Notes

- **Step 7, in order** (Q4 (a) assumed): `CreateSectionAsync(notebookId, "Recovered.one")` only when step 3 found no `Recovered`; `CreateNewPageAsync(section)`; `GetPageContentAsync(new, Basic)`; `DeletePageContentAsync` for each template object, then one more read for the date; `UpdatePageContentAsync(built XML, that date)`. The date comes from the new page, so `force: false` and the gateway's date rule hold. If that write carried kept attachments and fails with a definite code, the Decisions' one empty-page retry without them follows.
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
- `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter RestoreAttachmentProbe` on MCP Test: record `RESULT restore-attachment: kept|dropped|error <hex>`. Then from Claude Code, a page with an attachment: kept while its temp copy exists; after deleting that `%TEMP%` copy, restored without it and counted in `binaryMissing` (Q2).

## Auto Run Result

- **Summary:** `restore_from_journal(entryId)` restores a page journal entry as a new page through the `Create` pipeline (AD-8 order; protected entries only into their own still-protected section), writing the snapshot with every `objectID` stripped after clearing the new page's template objects. Q1–Q6 as decided: `meta.journalEntryId` on `update_page_content`/`delete_page_content` and the entry named in their step-7 failures; Q2's `pathCache`-only attachments from the user's temp folder, with one empty-page retry without them; the gateway's `CreateSectionAsync` by parent ID.
- **Files:** new `RestoreFromJournalUseCase.cs`, `RestoreFromJournalTool.cs`, `RestoredPage.cs`, `NotebookSections.cs`, `RestoreAttachments.cs`, `SectionFileNames.cs`; changed `IOneNoteGateway.cs`/`ComGateway.cs` (`CreateSectionAsync`), `WriteAccess.cs` (`CheckNotebook`), `JournalFiles.cs` (`Find`), `JournalStore.cs` (newer schema → `NotSupported`), `PageWrite.cs`, `WriteOperation.cs`/`WritePipeline.cs` (`FailureNote`), `ToolMeta.cs`, `ToolWarnings.cs`, both page-write use cases and tools, `ServerSetup.cs`; tests (`RestoreFromJournalToolTests`, `RestoredPageTests`, gateway, fakes, tool lists, contract snapshot at 16 tools), opt-in `RestoreFromJournalIntegrationTests` and `RestoreAttachmentProbe`, guide skill and README rows, `deferred-work.md`, `research-onemore-lessons.md`.
- **Review:** one quick pass, 7 findings, all patched (4 medium, 3 low); none rejected; one pre-existing flaky test deferred.
- **Follow-up review recommended: true** (4 medium patched on a first pass). The named risk: the `FailureNote` hook changed `WritePipeline`'s shared step-7 cancel and deadline messages for every write class, and the gate-before-send and Q2 retry paths were added in the patch round; unit-tested, not reviewed again.
- **Verification:** `dotnet build OneNoteMcp.slnx -warnaserror` 0 warnings 0 errors; `dotnet test tests/OneNoteMcp.Tests` 2263/2263 (one run fully green; the deferred flaky test failed in other runs); `dotnet test tests/OneNoteMcp.Integration` without the variable 154 passed, 49 skipped; `dotnet build spikes/stress-harness -c Release` builds. Matrix audit: every row has a passing test.
- **Residual risks (live sitting):** whether OneNote accepts a `pathCache`-only `InsertedFile` (the probe); whether a stripped-ID `Title` replaces the new page's title (OneMore relies on it); whether the template deletes work with the page XML's date; a temp folder reported in 8.3 short form would drop every attachment (fails safe, counted).

