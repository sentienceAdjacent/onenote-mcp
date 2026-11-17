---
title: 'Move a page'
type: 'feature'
ticket: '4'
created: '2026-11-16'
status: 'built'
baseline_revision: 'b6ae4fa709745d19f9bab2ca0b4320429e1b4095'
route: 'full'
route_source: 'auto'
review: 'quick'
review_source: 'pinned'
lenses_ran: ['quick']
review_loop_iteration: 0
context:
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/architecture-onenote-mcp/architecture-onenote-mcp.md'
  - '{project-root}/_bmad-output/initiative-onenote-mcp-v1/epic-round-2-external-and-structural/spike-write-probes-part-3-exports-move-merge-and-recycle-bin-restore-findings.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Claude can't move a page. Epic 3 E5 needs a separate move tool, and entries 12, 5 and 6 reuse its two-notebook grant check, its E8 refusal and its test notebook.

**Approach:** `move_hierarchy_object(objectId, parentId, position?, basis?)` (AD-5, AD-14), for pages only, as one `HierarchyWrite` through the existing `WritePipeline`. Core builds one `UpdateHierarchy` of the target section's XML with the page inserted (spike 3.1 M1 shape a). This story builds the re-resolve of both notebooks under the lock (AD-7); 3.12 adds sections and groups and extends the re-resolve to other writes.

## Boundaries & Constraints

**Always:**
- Step 1, before the lock: an empty `objectId` or `parentId` is `InvalidArgument`; a basis with any hash is `InvalidArgument` (AD-17). Then the walk from `objectId` to its section, never a `self` read first, because OneNote hides a locked section's pages (3.3 sitting S1). With no section, read `self`: a section or section group is `NotSupported` ("pages only until story 3.12"), and anything else is `InvalidArgument`. Then the write grant on the page's notebook, and then on the target's (`CheckContainerAsync`). Then a pre-check of the refusals below, so the lock is never taken for one.
- Step 3, under the lock: the page is walked to its section and notebook again, and the target to its notebook. If either `NotebookKey` differs from step 1's, the result is `WriteNotGranted`, retryable, before COM (AD-7). The source section and the target are each read at `Children` scope.
- Step 4 refuses in this order: a protected source (`encrypted`, locked or not) is `NotSupported` with a `userAction` to move it in OneNote, checked first (AD-7, AD-21, Q9). A source page or a target in the recycle bin is `ObjectNotFound`. A target that isn't a section is `InvalidArgument`. A locked target is `SectionLocked`. A target equal to the source is `InvalidArgument`, with a `userAction` naming `reorder_hierarchy_children`. A position outside the list, or inside a subpage block, is `InvalidArgument`. The XML must pass the AD-16 XSD gate.
- Step 5: the basis is the page's hierarchy `lastModifiedTime`, compared as `HierarchyWrite` does (`noBasis`, `partialBasis`, `StaleWrite`).
- Step 6: one `hierarchy` journal entry per section, source then target (`HierarchySnapshot`). A protected target's entry is DPAPI-encrypted, as the snapshot already does.
- Step 7: one `UpdateHierarchy`. The target lists every live page in its order, each with its `pageLevel`, and the moved page goes in at `position` with `pageLevel="1"`. OneNote flattens subpages sent without a level (5.4 sitting L6).
- After 7, under the lock: re-read both sections. The moved page is the one page in the target whose ID wasn't there before, preferring the sent name. The result gives `meta.objectId` (its new ID), `meta.parentId` (the target), the journal entry IDs, and the warning `subpagesLeftBehind` with step 3's count when it is above 0. A failed re-read is `readBackFailed`; a page not found, or still in the source, is `readBackDiffers`.
- `0x80042004` from the call sets `outcomeUnknown`, keeping `ObjectNotFound`. Before it is thrown, both sections are re-read, best-effort, and the message says what was found: still in the source, a new page with its name in the target, or both.
- The cache drops the old page ID (`WrittenPageIds`). A target in a protected section needs only the write grant. Messages carry IDs and counts only, never names (AD-9).

**Never:**
- No section or section-group moves, no settle, and no `SectionKey` grant check (3.12).
- No re-resolve for other writes (3.12).
- No skill or README text (entry 10).
- No page snapshot (AD-7: no page move lost a page).
- No retry of the write.
- No `NavigateTo`.
- No second `UpdateHierarchy` on the source (M1 shape b isn't needed).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected | Error |
|---|---|---|---|
| Same notebook | page → another MCP Test section | moved, new ID in `meta.objectId`, two entries | none |
| Other notebook | page → throwaway section, both granted | moved, new ID | none |
| Has subpages | page with 2 subpages | parent moved; subpages stay in source | `subpagesLeftBehind` (2) |
| Target not granted | throwaway without a grant, no elicitation | nothing sent | `WriteNotGranted` |
| Notebook changed under lock | page's section moved to another notebook after step 1 | nothing sent | `WriteNotGranted`, retryable |
| Protected source | page in Vault, locked or unlocked | nothing sent | `NotSupported` |
| Locked target | target is the locked Vault | nothing sent | `SectionLocked` |
| Unlocked protected target | target is an unlocked protected section | moved; its entry encrypted | none |
| Recycle bin | page in Deleted Pages, or target in or is the bin | — | `ObjectNotFound` |
| Not a page | section or group `objectId` | — | `NotSupported` |
| Bad target | target is a group, notebook or page | — | `InvalidArgument` |
| Same section | `parentId` is the page's own section | — | `InvalidArgument` → reorder |
| Bad position | 0, past count + 1, or inside a subpage block | — | `InvalidArgument` |
| Stale | basis date ≠ page's hierarchy date | — | `StaleWrite` |
| Partial copy | `UpdateHierarchy` → `0x80042004` | both sections re-read, findings in message | `ObjectNotFound`, `outcomeUnknown` |
| Dialog | `0x80042030` | nothing moved | `ModalDialogOpen`, retryable |

## Decisions (Phill, 2026-11-17, plan approved with every recommendation)

- **Q1, a.** `meta.journalEntryId` is the source section's entry, and `meta.journalEntryIds` lists both, source then target (as a permanent delete pairs `journalEntryId` with `pageEntryIds`).
- **Q2, a.** `position` is 1-based among the target's pages: the page goes before the page now at that position, which must be top-level. Omitted, the page goes after the last page (where OneMore's move puts it). S2 checks a middle position live.
- **Q3, a.** The pipeline raises the write-grant dialog at most once per distinct notebook key, so a cross-notebook move can ask twice; single-notebook writes are unchanged.
- **Q4, a.** A subpage may move: it lands at level 1, and its own subpages stay behind with `subpagesLeftBehind`. S2 checks it live.
- **Q5, a.** The full plan is kept, over the 1,600-token guide (about 3,500 tokens).

</frozen-after-approval>

## Code Map

- `src/OneNoteMcp.Core/Writes/HierarchyWrite.cs` -- the rename/reorder base (5.4). Copy its basis logic (l.207–253), its read-back warnings and its `FailureNote`. Extract the basis comparison into a shared helper rather than forking it. Don't subclass: its step 1 and 3 read the target at `self`/`Children`, which fails for a page in a locked section.
- `src/OneNoteMcp.Core/Writes/WriteOperation.cs`, `WritePipeline.cs` -- the step hooks. `IsAmbiguous` doesn't cover `0x80042004`: the op throws its own `OneNoteException(..., outcomeUnknown: true, retryable: false)`, which passes through step 7 unchanged. The grant-dialog loop (l.108–135) is where Q3 lands.
- `src/OneNoteMcp.Core/Access/WriteAccess.cs` -- `CheckAsync` (page) and `CheckContainerAsync` (target) return `WriteTarget.NotebookKey` for step 3's compare. `WriteGrantMissing.Key` exists for Q3.
- `src/OneNoteMcp.Core/Access/HierarchyWalk.cs` -- `OwningSectionAsync` (step 1), `PageLocationAsync` (step 3: section and notebook in one walk), `OwningNotebookAsync` (target).
- `src/OneNoteMcp.Xml/HierarchyTarget.cs` -- `Read` (kind, recycle bin, `Encrypted`, `Locked`, pages with `PageLevel`) and `Page(sectionPagesXml, id)` (name, hierarchy date, level, subpage count). Reuse both.
- `src/OneNoteMcp.Xml/HierarchyUpdate.cs` -- add `MovePage(sectionId, pages, pageId, index)` beside `Reorder`, sending the levels the same way.
- `src/OneNoteMcp.Core/Journal/HierarchySnapshot.cs` -- `TakeAsync` once per section, unchanged.
- `src/OneNoteMcp.Core/UseCases/UpdateHierarchyUseCase.cs`, `src/OneNoteMcp.Server/Tools/RenameHierarchyObjectTool.cs`, `ServerSetup.cs` (l.166, l.224) -- the shapes of the use case, the thin tool and the registration.
- `src/OneNoteMcp.Core/Results/ToolMeta.cs`, `ToolWarnings.cs` -- `subpagesLeftBehind` and `JournalEntryIds` (Q1).
- `tests/OneNoteMcp.Tests/FakeGateway.cs:312` -- `UpdateHierarchyCalls`, `UpdateHierarchyFailure`, `OnUpdateHierarchy`. `PublishToolTests.cs` shows how 3.3 hid a locked section's page from the fake's `self` read; do the same here.
- `tests/OneNoteMcp.Tests/McpSdkBindingTests.cs` (24 tools), `Contract/tool-contract.json`, `PluginPackagingTests.cs:255` (`DocumentedByRound2Entry10`) -- add the tool to each.
- `tests/OneNoteMcp.Integration/UpdateHierarchyIntegrationTests.cs:555` (guard: every ID sent was made by this run), `PublishIntegrationTests.cs:81` (Vault ID read unlocked, then the lock pause), `TestNotebook.AdoptThrowawayNotebookAsync`.
- OneMore `Commands/Search/SearchServices.cs` `MovePages` (main, read 2026-11-16): it removes the `Page` from the source XML, strips every attribute but `ID` and `name`, appends it last in the target, calls `UpdateHierarchy` on each source and then the target, and finds the moved page again by ID, then by the last page with its name. We send one call (M1 shape a), keep `pageLevel`, and match by new ID.

## Tasks & Acceptance

**Execution:**
- [ ] `src/OneNoteMcp.Xml/HierarchyUpdate.cs` -- `MovePage` -- the XML is built in Xml from IDs only (AD-5).
- [ ] `src/OneNoteMcp.Core/Writes/HierarchyBasis.cs` (new) + `HierarchyWrite.cs` -- the extracted basis check -- one implementation for both.
- [ ] `src/OneNoteMcp.Core/Writes/MovePageWrite.cs` (new) -- `WriteOperation<MoveResult>` following the Always steps -- the move pipeline.
- [ ] `src/OneNoteMcp.Core/UseCases/MoveHierarchyObjectUseCase.cs` (new) + `src/OneNoteMcp.Server/Tools/MoveHierarchyObjectTool.cs` (new) + `ServerSetup.cs` -- `move_hierarchy_object`, with `ReadOnly=false, Destructive=true, Idempotent=false, OpenWorld=false`. The description says pages only, the hierarchy-date basis, the new ID, subpages left behind, and `outcomeUnknown`.
- [ ] `WritePipeline.cs` -- Q3: the grant dialog at most once per notebook key.
- [ ] `ToolMeta.cs`, `ToolWarnings.cs`, `tool-contract.json`, binding and packaging tests -- the contract. `<Version>` stays `0.9.0-rc.1` (0.9 is unreleased).
- [ ] `tests/OneNoteMcp.Tests/MoveHierarchyObjectToolTests.cs` (new) -- every matrix row on `FakeGateway`, plus the XML's page order and levels, both entries, the cache drop, and step 1 refusing before any dialog.
- [ ] `tests/OneNoteMcp.Integration/MoveHierarchyObjectIntegrationTests.cs` (new) -- see Verification.
- [ ] `research-onemore-lessons.md` row 19 -- add what OneMore's `MovePages` does with position and attributes.

**Acceptance Criteria:**
- Given a page and a target in one granted notebook, when Claude Code calls `move_hierarchy_object`, then the page is in the target at the position sent under the ID in `meta.objectId`, and the old ID is gone.
- Given `rename_hierarchy_object` and `reorder_hierarchy_children`'s existing tests, when they run after the basis extraction, then they pass unchanged.

## Implementation Notes

- **Judgment calls the plan left open.**
  - A `position` below 1 is refused in step 1, before any read.
  - When the read-back finds no new page but the page is still in the source, `meta.objectId` is its old ID (its current ID), with `readBackDiffers`. It is absent with `readBackFailed`.
  - After `0x80042004`, the message names the copies found in the target by ID, or by count only when the target is protected (AD-21 Messages).
- **Files touched beyond the task list.**
  - `HierarchyUpdateXmlTests.cs`: tests for `MovePage`.
  - `ToolContractTests.cs`, `NavigateToToolTests.cs`, `McpSdkBindingTests.cs` and `PluginPackagingTests.cs`: the move tool added. That makes 25 tools and 60 properties. `move_hierarchy_object` is in `DocumentedByRound2Entry10`, so entry 10 documents it.
  - The contract snapshot only grew: the new tool, and `journalEntryIds` in every output schema. `<Version>` stays `0.9.0-rc.1`.
- **The live test's setup** gives the subpage `pageLevel="2"` through the guarded raw `UpdateHierarchy`, as 5.4's L6 did. It makes the throwaway's run section through the guarded raw `OpenHierarchy`.
- **TDD.** The tests were written alongside the code, not strictly first. Instead, a mutation check ran: with the Q3 dialog rule reverted, its test failed, and with the under-lock compare removed, its test failed. Both files were then restored.
- **Verification (2026-11-17, after the review patches):**
  - `dotnet build OneNoteMcp.slnx -warnaserror`: 0 warnings.
  - `dotnet test tests/OneNoteMcp.Tests`: 3,081 passed.
  - `dotnet test tests/OneNoteMcp.Integration` with no integration variable: 217 passed, 97 skipped, 0 failed. The 3 new live tests are skipped.
  - S1 and S2 haven't run.

## Plan Change Log

## Review Triage Log

- 2026-11-17, quick lens, iteration 0: 0 high, 2 medium, 1 low, 2 false, 0 maybe-false. Three were patched; nothing was deferred.
  - **medium, patch.** The target half of step 3's notebook re-resolve (`CheckNotebook("Target", …)`) had no test, though AD-7 requires both notebooks re-resolved. Fix: a unit test that moves the target section to another notebook between step 1 and step 3.
  - **medium, patch.** The live tests read the page's basis date straight after a setup write, and AD-17 records that a node's date can move about a second late, which would fail sitting S1 with a spurious `StaleWrite` (inferred, not observed). Fix: wait 2 s after the setup write.
  - **low, patch.** The tool description and the use case's comment said `meta.objectId` is always the new ID. When the read-back finds the page still in the source, it is the old ID, with `readBackDiffers`. That is the page's current ID, as AD-7 says. Fix: the wording now says "current ID"; the code is unchanged.
  - **false.** "The `0x80042004` error's `userAction` invites a retry with a current ID." `ToolErrors.From` (l.37) replaces the `userAction` of every `outcomeUnknown` error with "Re-read, then retry if the change is absent.", so the generic `ObjectNotFound` text never reaches the caller.
  - **false.** "The step-1 refusals come after the grant dialog." The frozen Always block orders the grant checks before the refusals' pre-check, following the rename tool. The code does what the approved plan says. The note for Phill: a move that will be refused can still ask for a grant first.

## Design Notes

Considered subclassing `HierarchyWrite`; going with a sibling `WriteOperation` that shares the extracted basis check, because the base reads its target at `self` before any walk.

The `0x80042004` re-read runs inside `CallAsync`, still under the lock and before the throw, so the pipeline needs no new branch.

## Verification

**Commands:**
- `dotnet build OneNoteMcp.slnx -warnaserror` -- expected: 0 warnings.
- `dotnet test tests/OneNoteMcp.Tests` -- expected: all pass, the contract snapshot included.
- `dotnet test tests/OneNoteMcp.Integration` with no integration variables -- expected: it builds, and every live test is skipped.
- TDD: write each new test first and watch it fail.

**Integration tests** (temporary settings and journal; a guarded gateway; the run makes its own sections and pages):
- In MCP Test, a page with a subpage moves to a run section at position 2. Then a page moves to a run section in `MCP Test Throwaway 1112-034716 a`, adopted by exact name; the test is skipped if it isn't open. Each move gets a new ID and two journal entries.
- With the throwaway's grant removed, the move is `WriteNotGranted`.
- A Ballast Vault page ID is read while the Vault is unlocked; then a pause for Phill to press Ctrl+Alt+L. Moving that page is `NotSupported`, and moving a run page into the Vault is `SectionLocked`. No `UpdateHierarchy` call is sent.

**Sitting steps** (the main session with Phill; the build never runs them, never sets the integration variable, never touches OneNote, and never reads OneNoteMcp's app-data folders):
- S1: MCP Test and the throwaway open, the Ballast Vault unlocked. Run `ONENOTE_MCP_INTEGRATION=1 dotnet test tests/OneNoteMcp.Integration --filter FullyQualifiedName~MoveHierarchyObjectIntegrationTests`; Phill locks the Vault at the pause. Expected: all pass.
- S2: register the dev build as `onenote-dev`. Ask Claude to move an MCP Test page that has a subpage to the middle of another MCP Test section, then to a section in the throwaway, then to move a subpage (Q4). Phill checks in OneNote: each page is where it was sent, the subpages stay in the source at level 1, and Deleted Pages gained nothing.
